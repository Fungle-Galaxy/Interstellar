using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Interstellar.Voice;

namespace Interstellar.Network.P2P;

// P2P media transport, direct first / relay on failure.
//
// One UDP socket per voice session, shared by every peer. Candidates are
// local unicast addresses plus srflx addresses learned from STUN. Peer
// candidates and an ephemeral ECDH key arrive over the voice server's signal
// channel. Connectivity checks are simultaneous STUN bindings; a path is
// validated by the first binding response. If punching burns its window (or
// NAT fix is on from the start), a single TURN allocation is selected and
// announced on the same signal channel; the relayed path then races the
// direct one per peer. Media is sealed with AES-256-GCM either way.
//
// The receive loop is a dedicated long-running thread; all session state is
// guarded by _lock; callbacks fire outside it.
internal sealed class P2pTransport : IDisposable
{
    public delegate void AudioHandler(int clientId, byte[] opus, uint counter);
    public delegate void SignalHandler(string sid, string json);

    private const int TickIntervalMs = 100;
    private const int ProbeIntervalMs = 100;
    private const int PunchWindowMs = 7000;
    private const int PunchRetryMinBackoffMs = 3000;
    private const int PunchRetryMaxBackoffMs = 30000;
    private const int KeepAliveMs = 10000;
    private const int DeadMs = 45000;
    private const int PathQuietMs = 12000;
    private const int HelloResendMs = 2000;
    private const int HelloMaxCount = 64;
    private const int PendingTtlMs = 6000;
    private const int SrflxTtlMs = 120000;
    private const int StunTimeoutMs = 3000;
    private const int StunAttempts = 2;

    private const int MaxTargets = 8;
    private const int MaxOrphans = 16;
    private const int MaxCandidates = 12;

    private readonly AudioHandler _onAudio;
    private readonly SignalHandler _signal;

    // STUN servers for srflx discovery. The server's clientPeerConfig list
    // wins when present; the config-file list is the no-secret fallback.
    private volatile string[] _stunServers;

    // TURN fallback: punch first, relay when the punch fails; under NAT fix,
    // relay from the first packet. One allocation is shared by every peer
    // (permissions are per peer), selected by measured latency or region.
    private readonly object _turnLock = new();
    private Turn.TurnClient? _turn;
    private bool _turnBusy;
    private volatile bool _turnWanted;
    private long _turnRetryAt;
    private const int TurnSelectTimeoutMs = 4000;
    private const int TurnRetryCooldownMs = 15000;

    // NAT fix: never punch, relay every peer from the start. The server's
    // forceRelayOnly pins this on and cannot be overridden locally.
    private static bool NatFixOn => VoiceConfig.NatFix || IceConfig.ForceRelayOnly;

    private readonly object _lock = new();

    private UdpClient? _udp;
    private int _port;
    private volatile bool _running;
    private bool _disposed;
    private Timer? _tickTimer;
    private Task? _receiveTask;

    private readonly Dictionary<string, P2pSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, P2pSession> _byPeerDir = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, P2pSession> _byFlow = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<(string Sid, string Json)> _outSignals = new();

    private readonly List<(byte[] Pkt, IPEndPoint From, long At)> _orphans = new();

    private readonly List<IPEndPoint> _local = new();
    private readonly List<IPEndPoint> _srflx = new();
    private readonly HashSet<string> _srflxKeys = new(StringComparer.Ordinal);
    private long _srflxAt;
    private long _localAt;
    private volatile bool _gathering;
    private int _errors;
    private volatile int _stunRxAny;

    private sealed class Pending
    {
        public IPEndPoint? Dest;
        public TaskCompletionSource<IPEndPoint>? Tcs;
        public P2pSession? Session;
        public long SentAt;
    }

    private sealed class HelloMsg
    {
        public string? t { get; set; }
        public string? d { get; set; }
        public string? k { get; set; }
        public string[]? c { get; set; }
        public int g { get; set; }
        // Relay address for t="turn"; never present on t="hi".
        public string? r { get; set; }
    }

    // Live transport of the current room (at most one; VoiceRoom is a singleton).
    public static P2pTransport? Active { get; private set; }

    public P2pTransport(AudioHandler onAudio, SignalHandler signal)
    {
        _onAudio = onAudio;
        _signal = signal;
        _stunServers = LoadStunServers();
        Active = this;
    }

    // STUN list from the server's clientPeerConfig. The config-file list is
    // only used until this arrives. STUN has no credentials.
    public void SetStunServers()
    {
        var fromServer = IceConfig.StunServers;
        if (fromServer.Count == 0) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var s in fromServer)
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s.Trim())) list.Add(s.Trim());
        if (list.Count == 0) return;
        _stunServers = list.ToArray();
        Logger("[P2P] STUN servers from server config: " + _stunServers.Length);
    }

    // Ensure a TURN allocation exists. Coalesces concurrent calls and backs
    // off after failures instead of spinning.
    private void EnsureTurn(string reason)
    {
        if (_disposed) return;
        _turnWanted = true;
        lock (_turnLock)
        {
            if (_turnBusy || (_turn != null && _turn.Ready)) return;
            if (Environment.TickCount64 < _turnRetryAt) return;
            _turnBusy = true;
        }
        _ = Task.Run(() => SelectTurnAsync(reason));
    }

    private async Task SelectTurnAsync(string reason)
    {
        var candidates = BuildTurnCandidates(out bool pinned);
        if (candidates.Count == 0)
        {
            Logger("[P2P] TURN needed (" + reason + ") but no candidates — the server " +
                   "sent none in clientPeerConfig and no local override is set");
            lock (_turnLock)
            {
                _turnBusy = false;
                _turnRetryAt = Environment.TickCount64 + TurnRetryCooldownMs;
            }
            return;
        }

        // Race every candidate on its own socket. Allocation time is the
        // latency measure; the fastest wins, the rest are released.
        var tasks = new Task<Turn.TurnClient?>[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            tasks[i] = Turn.TurnClient.AllocateAsync(c.Url, c.User, c.Pass, TurnSelectTimeoutMs);
        }
        Turn.TurnClient?[] results;
        try { results = await Task.WhenAll(tasks); }
        catch { results = Array.Empty<Turn.TurnClient?>(); }

        Turn.TurnClient? best = null;
        long bestRtt = long.MaxValue;
        foreach (var c in results)
        {
            if (c == null || !c.Ready) continue;
            if (c.RttMs < bestRtt)
            {
                var old = best;
                best = c;
                bestRtt = c.RttMs;
                try { old?.Dispose(); } catch { }
            }
            else { try { c.Dispose(); } catch { } }
        }

        if (best == null)
        {
            Logger("[P2P] TURN: all " + candidates.Count + " candidate(s) failed (" +
                   reason + ") — retrying after cooldown");
            lock (_turnLock)
            {
                _turnBusy = false;
                _turnRetryAt = Environment.TickCount64 + TurnRetryCooldownMs;
            }
            return;
        }

        Turn.TurnClient? prev;
        lock (_turnLock)
        {
            prev = _turn;
            _turn = best;
            _turnBusy = false;
            _turnRetryAt = 0;
        }
        best.OnPayload = OnTurnPayload;
        best.OnDead = OnTurnDead;
        try { prev?.Dispose(); } catch { }
        Logger("[P2P] TURN selected " + best.Spec + " rtt=" + best.RttMs + "ms" +
               (pinned ? " (pinned)" : "") + " (" + reason + ")");

        AnnounceAndWire();
    }

    // A pinned local override wins outright (no latency race). Otherwise the
    // server's list, region-filtered when the player pinned a region and
    // auto-pick is off; falls back to the whole list when the region has no
    // entries rather than going silent.
    private static List<(string Url, string User, string Pass)> BuildTurnCandidates(out bool pinned)
    {
        pinned = false;
        var all = new List<(string, string, string)>();
        string local = VoiceConfig.TurnOverrideUrl;
        if (!string.IsNullOrWhiteSpace(local))
        {
            pinned = true;
            all.Add((local.Trim(), VoiceConfig.TurnOverrideUsername, VoiceConfig.TurnOverrideCredential));
            return all;
        }

        var turns = IceConfig.Turns;
        string region = (VoiceConfig.TurnRegion ?? "").Trim();
        // Empty region = auto: race everything. Concrete tag = pin that region.
        bool auto = region.Length == 0;
        if (!auto && turns.Count > 0)
        {
            var regional = new List<(string, string, string)>();
            foreach (var t in turns)
                if (string.Equals((t.Region ?? "").Trim(), region, StringComparison.OrdinalIgnoreCase))
                    regional.Add((t.Url, t.Username, t.Credential));
            if (regional.Count > 0) { pinned = true; return regional; }
        }
        foreach (var t in turns) all.Add((t.Url, t.Username, t.Credential));
        return all;
    }

    // Announce our relayed address to every peer and wire each session.
    private void AnnounceAndWire()
    {
        P2pSession[] snapshot;
        lock (_lock)
        {
            if (_disposed) return;
            snapshot = _sessions.Values.ToArray();
        }
        foreach (var s in snapshot)
        {
            if (s.Disposed) continue;
            SendTurnAnnounce(s);
            WireRelayFor(s);
        }
        FlushSignals();
    }

    private void SendTurnAnnounce(P2pSession s)
    {
        var turn = _turn;
        if (turn == null || !turn.Ready || turn.Relayed == null) return;
        s.TurnAnnounced = true;
        string json;
        try { json = JsonSerializer.Serialize(new HelloMsg { t = "turn", r = turn.Relayed.ToString() }); }
        catch { return; }
        _outSignals.Enqueue((s.Sid, json));
    }

    // Relay wiring for one session. Permissions are needed for the peer's
    // relayed address AND their public candidates (a peer without TURN of
    // their own sends straight at our relayed port). The relayed address also
    // becomes a probe target. Idempotent.
    private void WireRelayFor(P2pSession s)
    {
        var turn = _turn;
        if (turn == null || !turn.Ready || turn.Relayed == null) return;

        IPEndPoint? peerRelayed;
        IPEndPoint[] direct;
        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            peerRelayed = s.PeerRelayed;
            direct = s.Targets.ToArray();
        }

        if (peerRelayed != null) turn.PreparePeer(peerRelayed);
        foreach (var ep in direct)
            if (!IsPrivateAddress(ep)) turn.PreparePeer(ep);

        if (peerRelayed == null) return;
        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            s.PeerRelayed = peerRelayed;
            s.AddTarget(peerRelayed);
            if (!s.Connected && s.PunchRoundEndsAt == 0 && s.HasPeerHello)
                StartPunchLocked(s, reason: "relay target");
        }
    }

    // Packet from the TURN relay: same shapes as the plain socket, flagged so
    // replies ride the relay back.
    private void OnTurnPayload(byte[] data, IPEndPoint from)
    {
        try { HandleDatagram(data, from, viaTurn: true); }
        catch (Exception ex)
        {
            if (++_errors <= 5) Logger("[P2P] turn rx error: " + ex.Message);
        }
    }

    private void OnTurnDead()
    {
        Logger("[P2P] TURN allocation died — releasing, reselecting");
        Turn.TurnClient? dead;
        lock (_turnLock)
        {
            dead = _turn;
            _turn = null;
            _turnBusy = false;
            _turnRetryAt = 0;
        }
        try { dead?.Dispose(); } catch { }
        // Sessions keep PeerRelayed; the quiet-window path switch (or DeadMs
        // re-punch) will move them onto the fresh allocation once announced.
        EnsureTurn("allocation died");
    }

    // Route one raw packet. NAT fix pins everything to the relay; a session
    // on a relay path (or a destination that is a relayed address) rides our
    // TURN allocation; everything else stays on the direct socket. Falls
    // through to UDP only when the relay has no channel yet — under NAT fix
    // that drops instead, since leaking to the direct socket must never happen.
    private bool SendPathLocked(P2pSession? s, byte[] data, IPEndPoint dest, bool viaTurnHint = false)
    {
        if (data == null || data.Length == 0 || dest == null) return false;
        var turn = _turn;
        bool relay = viaTurnHint || NatFixOn || (s?.ViaRelay == true)
                     || (s != null && s.PeerRelayed != null && EqualsAddress(dest, s.PeerRelayed))
                     || IsRelayedAddr(dest);
        if (relay)
        {
            if (turn != null && turn.Ready && turn.Send(data, dest)) return true;
            if (NatFixOn) return false;
        }
        var udp = _udp;
        if (udp == null) return false;
        try { udp.Send(data, data.Length, dest); return true; }
        catch { return false; }
    }

    // True when ep is a TURN relayed address — ours or one a peer announced.
    // Locks internally (re-entrant), so callable under _lock.
    private bool IsRelayedAddr(IPEndPoint? ep)
    {
        if (ep == null) return false;
        var ours = _turn?.Relayed;
        if (ours != null && EqualsAddress(ours, ep)) return true;
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                if (s.PeerRelayed != null && EqualsAddress(s.PeerRelayed, ep)) return true;
        }
        return false;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _udp != null) return;
            try
            {
                _udp = new UdpClient(0);
                _udp.Client.Blocking = true;
                _udp.Client.ReceiveBufferSize = 1 << 20;
                _udp.Client.SendBufferSize = 1 << 20;
                try
                {
                    const int SioUdpConnreset = -1744830452;
                    _udp.Client.IOControl(SioUdpConnreset, new byte[] { 0 }, null!);
                }
                catch { /* non-Windows */ }
                _port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            }
            catch (Exception ex)
            {
                Logger("[P2P] UDP socket failed: " + ex.Message);
                _udp = null;
                return;
            }
            _running = true;
        }

        RefreshLocalCandidates();
        _localAt = Environment.TickCount64;
        _receiveTask = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
        _tickTimer = new Timer(_ => SafeTick(), null, TickIntervalMs, TickIntervalMs);
        _ = RefreshSrflxAsync();
        Logger("[P2P] started port=" + _port + " stun=" + _stunServers.Length);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }

        try { _tickTimer?.Dispose(); } catch { }
        _tickTimer = null;

        UdpClient? udp;
        lock (_lock)
        {
            udp = _udp;
            _udp = null;
        }
        try { udp?.Close(); } catch { }
        try { udp?.Dispose(); } catch { }

        if (Active == this) Active = null;
        Turn.TurnClient? turn;
        lock (_turnLock) { turn = _turn; _turn = null; _turnBusy = false; }
        try { turn?.Dispose(); } catch { }

        P2pSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _byPeerDir.Clear();
            _byFlow.Clear();
            _pending.Clear();
            _orphans.Clear();
        }
        foreach (var s in sessions) { try { s.Dispose(); } catch { } }
    }

    public void AddPeer(string sid, int clientId)
    {
        if (_disposed || string.IsNullOrEmpty(sid)) return;
        try
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sid, out var existing))
                {
                    if (existing.ClientId == clientId) return;
                    UnregisterLocked(existing);
                    existing.Dispose();
                }
                var s = new P2pSession(sid, clientId);
                _sessions[sid] = s;
                SendHelloLocked(s);
                if (NatFixOn) EnsureTurn("natfix");
                // Announce when the allocation already exists: a peer that
                // joins after selection would otherwise never learn our
                // relayed address.
                if (_turn != null && _turn.Ready) SendTurnAnnounce(s);
            }
        }
        finally { FlushSignals(); }
    }

    public void RemovePeer(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return;
        P2pSession? s;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sid, out s)) return;
            _sessions.Remove(sid);
            UnregisterLocked(s);
        }
        s?.Dispose();
    }

    public void Clear()
    {
        P2pSession[] all;
        lock (_lock)
        {
            all = _sessions.Values.ToArray();
            _sessions.Clear();
            _byPeerDir.Clear();
            _byFlow.Clear();
            _orphans.Clear();
            _pending.Clear();
        }
        foreach (var s in all) { try { s.Dispose(); } catch { } }
    }

    public void ResendHellos()
    {
        try
        {
            lock (_lock)
            {
                foreach (var s in _sessions.Values)
                {
                    if (s.Disposed) continue;
                    s.HelloCount = 0;
                    SendHelloLocked(s);
                }
            }
        }
        finally { FlushSignals(); }
    }

    public void HandleSignal(string fromSid, string json)
    {
        if (_disposed || string.IsNullOrEmpty(fromSid)) return;
        HelloMsg? msg;
        try { msg = JsonSerializer.Deserialize<HelloMsg>(json); }
        catch { return; }
        if (msg == null) return;

        // Peer's relayed address — same channel as HELLO, no key material, so
        // the server's "from" alone attributes it.
        if (string.Equals(msg.t, "turn", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(msg.r)) return;
            if (!IPEndPoint.TryParse(msg.r!.Trim(), out var relayed) || relayed == null) return;
            P2pSession ts;
            bool wireOurs;
            lock (_lock)
            {
                if (_disposed || !_sessions.TryGetValue(fromSid, out var found) || found.Disposed) return;
                ts = found;
                ts.PeerRelayed = relayed;
                ts.AddTarget(relayed);
                Logger("[P2P] cid=" + ts.ClientId + " peer relayed=" + relayed);
                if (!ts.Connected && ts.PunchRoundEndsAt == 0 && ts.HasPeerHello)
                    StartPunchLocked(ts, reason: "relay target");
                wireOurs = _turn != null && _turn.Ready;
            }
            // Their packets can only reach us through our relay.
            EnsureTurn("peer announced relay");
            // Answer once per peer; re-answering every announce would ping-pong.
            if (wireOurs && !ts.TurnAnnounced) SendTurnAnnounce(ts);
            WireRelayFor(ts);
            FlushSignals();
            return;
        }
        if (!string.Equals(msg.t, "hi", StringComparison.Ordinal)) return;

        var dirHex = msg.d;
        if (string.IsNullOrEmpty(dirHex) || string.IsNullOrEmpty(msg.k)) return;
        dirHex = dirHex!.ToLowerInvariant();
        byte[]? dir = FromHex(dirHex);
        byte[]? pub;
        try { pub = Convert.FromBase64String(msg.k!); }
        catch { return; }
        if (dir == null || dir.Length != P2pCrypto.DirIdLen || pub.Length != 64) return;

        try
        {
            lock (_lock)
            {
            if (_disposed) return;
            if (!_sessions.TryGetValue(fromSid, out var s) || s.Disposed) return;

            if (dirHex == ToHex(s.Crypto.DirId)) return;

            if (s.HasPeerHello && s.PeerDirHex == dirHex)
            {
                ApplyCandidatesLocked(s, msg.c);
                return;
            }

            if (s.HasPeerHello)
            {
                // Peer restarted with new key material.
                UnregisterPeerLocked(s, s.PeerDirHex);
                s.ResetPeerIdentity();
            }

            if (!s.Crypto.TryDerive(pub, dir)) { Logger("[P2P] key derive failed for " + s.Sid); return; }

            s.PeerDirId = dir;
            s.PeerDirHex = dirHex;
            s.PeerPublicKey = pub;
            s.HasPeerHello = true;
            s.Generation = msg.g;

            _byPeerDir[dirHex] = s;
            _byFlow[ToFlowId(dir)] = s;

            ApplyCandidatesLocked(s, msg.c);
            FlushOrphansLocked(s);

            SendHelloLocked(s);
            // Re-announce under the new identity: their old session is gone.
            SendTurnAnnounce(s);

            if (!s.Connected && s.PunchRoundEndsAt == 0) StartPunchLocked(s, reason: "hello");
            }
        }
        finally { FlushSignals(); }
    }

    // The connect flow as one HUD line: TURN phase first (it blocks everything
    // under NAT fix), then the first pending peer's step. Once every peer is
    // up it becomes a summary of the real paths. Null when nothing to narrate.
    public string? DescribeProgress()
    {
        lock (_lock)
        {
            if (_disposed || _sessions.Count == 0) return null;

            int total = 0, relayed = 0, pending = 0;
            string? step = null;
            foreach (var s in _sessions.Values)
            {
                total++;
                if (!s.Connected) { pending++; if (step == null) step = PeerStep(s); }
                else if (s.ViaRelay) relayed++;
            }

            if (pending == 0)
            {
                string up = (total - pending) + "/" + total;
                if (relayed == total)
                    return TranslationHelper.Get("vc.hud.p2p.allrelay", "P2P · TURN relay ") + up;
                if (relayed == 0)
                    return TranslationHelper.Get("vc.hud.p2p.alldirect", "P2P · direct ") + up;
                return TranslationHelper.Get("vc.hud.p2p.mixed", "P2P · relay/direct ")
                       + relayed + "+" + (total - relayed) + " " + up;
            }

            var turn = _turn;
            if (turn == null && _turnWanted)
                return TranslationHelper.Get("vc.hud.p2p.turnwait", "TURN: waiting for server config…");
            if ((turn != null && !turn.Ready) || _turnBusy)
                return TranslationHelper.Get("vc.hud.p2p.turnalloc", "TURN: allocating…");
            return step ?? TranslationHelper.Get("vc.hud.p2p.waitpeer", "waiting for peer…");
        }
    }

    private static string PeerStep(P2pSession s)
    {
        if (!s.HasPeerHello) return TranslationHelper.Get("vc.hud.p2p.waitpeer", "waiting for peer…");
        if (s.PeerRelayed == null) return TranslationHelper.Get("vc.hud.p2p.announce", "exchanging relay announce…");
        return TranslationHelper.Get("vc.hud.p2p.wiring", "binding / probing…");
    }

    public void SendAll(byte[] opus)
    {
        if (opus == null || opus.Length == 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            if (_sessions.Count == 0) return;

            foreach (var s in _sessions.Values)
            {
                if (s.Disposed || !s.Connected || s.Remote == null) continue;
                var pkt = s.Crypto.Seal(opus);
                if (pkt == null) continue;
                try
                {
                    if (!SendPathLocked(s, pkt, s.Remote)) continue;
                    if (s.MediaTx++ == 0)
                        Logger("[P2P] media tx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame)");
                }
                catch { /* transient — keepalives will notice a dead path */ }
            }
        }
    }

    public bool IsConnectedTo(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        lock (_lock)
            return _sessions.TryGetValue(sid, out var s) && s != null && !s.Disposed && s.Connected;
    }

    public bool TrySendTo(string sid, byte[] opus)
    {
        if (string.IsNullOrEmpty(sid) || opus == null || opus.Length == 0) return false;
        lock (_lock)
        {
            if (_disposed) return false;
            if (!_sessions.TryGetValue(sid, out var s) || s == null || s.Disposed
                || !s.Connected || s.Remote == null) return false;
            var pkt = s.Crypto.Seal(opus);
            if (pkt == null) return false;
            try { return SendPathLocked(s, pkt, s.Remote); }
            catch { return false; } // frame is lost, never doubled
        }
    }

    public int ConnectedCount()
    {
        lock (_lock) return _sessions.Values.Count(s => s.Connected);
    }

    public string Describe()
    {
        lock (_lock)
        {
            int conn = _sessions.Values.Count(s => s.Connected);
            return "p2p peers=" + _sessions.Count + " connected=" + conn +
                   " punching=" + (_sessions.Count - conn) +
                   " srflx=" + _srflx.Count + " local=" + _local.Count + " port=" + _port;
        }
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            UdpClient? udp;
            lock (_lock) udp = _udp;
            if (udp == null) break;

            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try { data = udp.Receive(ref from); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; continue; }
            catch { if (!_running) break; continue; }

            try { HandleDatagram(data, from); }
            catch (Exception ex)
            {
                if (++_errors <= 5) Logger("[P2P] recv error: " + ex.Message);
            }
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint from, bool viaTurn = false)
    {
        if (StunMessage.IsStun(data)) { HandleStun(data, from, viaTurn); return; }
        if (data.Length >= P2pCrypto.HeaderLen + P2pCrypto.TagLen && data[0] == P2pCrypto.MediaMagic)
            HandleMedia(data, from, viaTurn);
    }

    private void HandleStun(byte[] data, IPEndPoint from, bool viaTurn = false)
    {
        ushort type = StunMessage.MessageType(data);

        if (type == StunMessage.BindingSuccess || type == StunMessage.BindingError)
        {
            if (!StunMessage.TryGetTxId(data, out var txId)) return;
            var key = ToHex(txId);

            Pending? p;
            lock (_lock)
            {
                if (!_pending.TryGetValue(key, out p)) return;
                _pending.Remove(key);
            }

            if (p.Tcs != null)
            {
                // STUN server query: XOR-MAPPED-ADDRESS is our own address —
                // this is where the srflx candidate comes from.
                if (type == StunMessage.BindingSuccess &&
                    StunMessage.TryParseBindingResponse(data, txId, out var mapped))
                    p.Tcs.TrySetResult(mapped);
                else
                    p.Tcs.TrySetResult(null!);
                return;
            }

            if (p.Session == null || p.Session.Disposed) return;
            // NAT fix: only relayed answers count.
            if (NatFixOn && !viaTurn) return;
            // Punch response: the source of this packet is the peer's address.
            if (type == StunMessage.BindingSuccess) OnPathValidated(p.Session, from, viaTurn);
            return;
        }

        if (type != StunMessage.BindingRequest) return;
        if (NatFixOn && !viaTurn) return;
        _stunRxAny++;

        if (!StunMessage.TryGetTxId(data, out var reqTx)) return;
        if (!StunMessage.TryGetUsername(data, out var username)) return;

        // USERNAME = <sender dirId hex>:<their view of us> — attributes the
        // probe to a session before any path is validated.
        var senderHex = username.Split(':')[0].ToLowerInvariant();
        byte[]? senderDir = FromHex(senderHex);

        P2pSession? s;
        lock (_lock)
        {
            if (senderDir == null || !_byPeerDir.TryGetValue(senderHex, out s)) return;
            if (s.Disposed) return;
            s.LastInbound = Environment.TickCount64;
            if (s.StunRx++ == 0)
                Dbg("[P2P] rx probe cid=" + s.ClientId + " sid=" + s.Sid +
                    " from=" + from + " (first)");
            if (!s.Connected)
            {
                if (!s.Learned.Contains(from)) s.Learned.Add(from);
                if (s.Targets.Count < MaxTargets) s.AddTarget(from);
            }
        }

        // Probe from an address we hold no permission for: open the relay
        // path, or neither the answer nor anything after it can ride back.
        if (viaTurn && !s.Connected) _turn?.PreparePeer(from);

        byte[] response;
        try { response = StunMessage.BuildBindingResponse(reqTx, from); }
        catch { return; }

        SendPathLocked(s, response, from, viaTurn);
    }

    private void HandleMedia(byte[] data, IPEndPoint from, bool viaTurn = false)
    {
        uint flow = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        P2pSession? s;
        long now = Environment.TickCount64;

        lock (_lock)
        {
            if (_disposed) return;
            // NAT fix: relay only.
            if (NatFixOn && !viaTurn) return;
            if (!_byFlow.TryGetValue(flow, out s))
            {
                // Their HELLO is still in flight; hold briefly and retry.
                if (_orphans.Count < MaxOrphans)
                    _orphans.Add((data, from, now));
                return;
            }
            if (s.Disposed) return;
        }

        if (!s.Crypto.TryOpen(data, out var payload, out uint counter)) return;

        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            if (NatFixOn && !viaTurn) return;
            // On the relay when the packet came through our TURN allocation or
            // its source is a relayed address — either way replies ride back.
            bool relayPath = viaTurn || IsRelayedAddr(from);
            if (!s.Connected)
            {
                AdoptPathLocked(s, from, reason: "authenticated frame", relayPath);
                s.LastInbound = now;
            }
            else if (EqualsAddress(s.Remote, from))
            {
                s.LastInbound = now;
                s.ViaRelay = relayPath;
            }
            else if (IsPrivateUpgrade(s.Remote, from) || now - s.LastInbound > PathQuietMs)
            {
                AdoptPathLocked(s, from, reason: "authenticated frame (path switch)", relayPath);
                s.LastInbound = now;
            }
            if (s.MediaFrames++ == 0)
                Logger("[P2P] media rx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame)");
        }

        try { _onAudio(s.ClientId, payload, counter); }
        catch { /* decoder must never take the receive loop down */ }
    }

    private void OnPathValidated(P2pSession s, IPEndPoint from, bool viaTurn = false)
    {
        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            if (NatFixOn && !viaTurn) return;
            long now = Environment.TickCount64;
            bool relayPath = viaTurn || IsRelayedAddr(from);
            if (s.Connected && !EqualsAddress(s.Remote, from))
            {
                if (!IsPrivateUpgrade(s.Remote, from) && now - s.LastInbound <= PathQuietMs)
                    return;
                AdoptPathLocked(s, from, reason: "binding response (path switch)", relayPath);
            }
            else
            {
                AdoptPathLocked(s, from, reason: "binding response", relayPath);
            }
            s.LastInbound = now;
        }
    }

    private static bool IsPrivateUpgrade(IPEndPoint current, IPEndPoint candidate)
        => IsPrivateAddress(candidate) && !IsPrivateAddress(current);

    private static bool IsPrivateAddress(IPEndPoint ep)
    {
        var ip = ep.Address;
        try
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var b6 = ip.GetAddressBytes();
                return (b6[0] & 0xFE) == 0xFC; // ULA fc00::/7
            }
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 169 && b[1] == 254);
        }
        catch
        {
            return false;
        }
    }

    private void AdoptPathLocked(P2pSession s, IPEndPoint from, string reason, bool relayPath = false)
    {
        bool changed = !s.Connected || !EqualsAddress(s.Remote, from);
        s.Connected = true;
        s.Remote = from;
        s.ViaRelay = relayPath;
        s.LastOutbound = Environment.TickCount64;
        if (changed)
            Logger("[P2P] connected cid=" + s.ClientId + " sid=" + s.Sid + " via " + from +
                   (relayPath ? " [TURN]" : "") + " (" + reason + ")");
    }

    private void StartPunchLocked(P2pSession s, string reason)
    {
        if (s.Targets.Count == 0) return;
        long now = Environment.TickCount64;
        s.Connected = false;
        s.Remote = null;
        s.PunchRoundEndsAt = now + PunchWindowMs;
        s.NextProbeAt = now;
        if (s.PunchRound == 0 || reason == "hello")
            Dbg("[P2P] punching cid=" + s.ClientId + " targets=" + s.Targets.Count +
                " (" + reason + ") " + string.Join(" ", s.Targets) +
                " | me=" + string.Join(" ", CollectCandidateStringsLocked()));
    }

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex)
        {
            if (++_errors <= 5) Logger("[P2P] tick error: " + ex.Message);
        }
    }

    private void Tick()
    {
        if (_disposed || !_running) return;
        long now = Environment.TickCount64;

        if (!_gathering && now - _srflxAt > SrflxTtlMs) _ = RefreshSrflxAsync();

        if (now - _localAt > SrflxTtlMs)
        {
            _localAt = now;
            if (RefreshLocalCandidates())
            {
                Logger("[P2P] local addresses changed (network change) — re-announcing");
                ResendHellos();
            }
        }

        // NAT fix can be flipped mid-session: start the relay the moment it is
        // on, so a running room moves onto the relay within seconds instead of
        // sitting silent until the 45s dead-path re-punch fires.
        if (NatFixOn && (_turn == null || !_turn.Ready)) EnsureTurn("natfix");

        UdpClient? udp;
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                udp = _udp;
                if (udp == null) return;

            PrunePendingLocked(now);

            foreach (var s in _sessions.Values)
            {
                if (s.Disposed) continue;

                if (s.Connected)
                {
                    if (now - s.LastInbound > DeadMs)
                    {
                        Logger("[P2P] path dead cid=" + s.ClientId +
                               " (no inbound for " + (now - s.LastInbound) + "ms) — re-punching");
                        StartPunchLocked(s, reason: "path dead");
                        continue;
                    }
                    if (now - s.LastOutbound >= KeepAliveMs)
                    {
                        s.LastOutbound = now;
                        if (s.Remote != null)
                        {
                            // Under NAT fix a session parked on its old direct
                            // path is unreachable: aim the keepalive at their
                            // relayed address instead, so the response validates
                            // the relay path and the session adopts it.
                            var dest = s.Remote;
                            if (NatFixOn && !s.ViaRelay && s.PeerRelayed != null)
                                dest = s.PeerRelayed;
                            ProbeLocked(s, dest);
                        }
                    }
                    continue;
                }

                if (!s.HasPeerHello || s.Targets.Count == 0)
                {
                    if (s.HelloCount < HelloMaxCount && now - s.LastHelloAt >= HelloResendMs)
                        SendHelloLocked(s);
                    continue;
                }

                if (s.HelloCount < HelloMaxCount && now - s.LastHelloAt >= HelloResendMs)
                    SendHelloLocked(s);

                if (now > s.PunchRoundEndsAt)
                {
                    s.PunchRound++;
                    int backoff = Math.Min(PunchRetryMaxBackoffMs,
                        PunchRetryMinBackoffMs + s.PunchRound * 2000);
                    s.PunchRoundEndsAt = now + PunchWindowMs + backoff;
                    Dbg("[P2P] punch attempt " + s.PunchRound + " cid=" + s.ClientId +
                        " still trying (targets=" + s.Targets.Count +
                        " tx=" + s.Probes + " rx=" + s.StunRx + " all=" + _stunRxAny + ")");
                    // Direct punch had its window: bring the relay up now.
                    if (s.PunchRound >= 1) EnsureTurn("punch failed");
                }

                if (NatFixOn)
                {
                    // NAT fix: never probe a direct address. Only the relayed
                    // target, and only once our own allocation exists.
                    EnsureTurn("natfix");
                    if (_turn == null || !_turn.Ready) continue;
                    if (now < s.NextProbeAt) continue;
                    s.NextProbeAt = now + ProbeIntervalMs;
                    if (s.PeerRelayed != null) ProbeLocked(s, s.PeerRelayed);
                    continue;
                }

                if (now < s.NextProbeAt) continue;
                s.NextProbeAt = now + ProbeIntervalMs;
                for (int i = 0; i < s.Targets.Count && i < MaxTargets; i++)
                    ProbeLocked(s, s.Targets[i]);
            }
        }
        }
        finally { FlushSignals(); }
    }

    private void ProbeLocked(P2pSession s, IPEndPoint dest)
    {
        if (s.PeerDirHex == null) return;

        var txId = StunMessage.NewTxId();
        var username = ToHex(s.Crypto.DirId) + ":" + s.PeerDirHex;
        byte[] req;
        try { req = StunMessage.BuildBindingRequest(txId, username); }
        catch { return; }

        s.Probes++;
        _pending[ToHex(txId)] = new Pending { Dest = dest, Session = s, SentAt = Environment.TickCount64 };
        // Routed, not raw: relay-path destinations ride the TURN allocation.
        if (!SendPathLocked(s, req, dest)) _pending.Remove(ToHex(txId));
    }

    private void PrunePendingLocked(long now)
    {
        if (_pending.Count == 0) return;
        List<string>? stale = null;
        foreach (var kv in _pending)
        {
            if (now - kv.Value.SentAt > PendingTtlMs)
                (stale ??= new List<string>()).Add(kv.Key);
        }
        if (stale == null) return;
        foreach (var k in stale) _pending.Remove(k);
    }

    private void SendHelloLocked(P2pSession s)
    {
        if (s.Disposed) return;
        string[] cands = CollectCandidateStringsLocked();
        var payload = new HelloMsg
        {
            t = "hi",
            d = ToHex(s.Crypto.DirId),
            k = Convert.ToBase64String(s.Crypto.PublicKey),
            c = cands,
            g = s.Generation,
        };
        string json;
        try { json = JsonSerializer.Serialize(payload); }
        catch { return; }

        s.HelloCount++;
        s.LastHelloAt = Environment.TickCount64;
        _outSignals.Enqueue((s.Sid, json));
    }

    private void FlushSignals()
    {
        while (_outSignals.TryDequeue(out var item))
        {
            try { _signal(item.Sid, item.Json); }
            catch (Exception ex) { Logger("[P2P] signal out failed: " + ex.Message); }
        }
    }

    private string[] CollectCandidateStringsLocked()
    {
        var list = new List<string>(MaxCandidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in _local)
            if (seen.Add(ep.ToString())) list.Add(ep.ToString());
        foreach (var ep in _srflx)
            if (seen.Add(ep.ToString())) list.Add(ep.ToString());
        if (list.Count > MaxCandidates) list.RemoveRange(MaxCandidates, list.Count - MaxCandidates);
        return list.ToArray();
    }

    private bool ApplyCandidatesLocked(P2pSession s, string[]? candidates)
    {
        if (candidates != null) s.PeerCandidates = candidates;

        string before = s.Targets.Count == 0 ? "" : string.Join("|", s.Targets);
        s.RebuildTargets(ParseCandidate, MaxTargets);
        string after = s.Targets.Count == 0 ? "" : string.Join("|", s.Targets);
        bool changed = !string.Equals(before, after, StringComparison.Ordinal);
        if (!changed || s.Connected || s.Targets.Count == 0) return false;

        long now = Environment.TickCount64;
        if (s.PunchRoundEndsAt == 0)
        {
            StartPunchLocked(s, reason: "candidates");
        }
        else
        {
            // They moved: give the in-progress round a fresh window rather
            // than letting it time out on stale addresses.
            s.PunchRoundEndsAt = now + PunchWindowMs;
            s.NextProbeAt = now;
        }
        return true;
    }

    private static IPEndPoint? ParseCandidate(string raw)
    {
        if (!IPEndPoint.TryParse(raw.Trim(), out var ep) || ep == null) return null;
        if (ep.Port <= 0 || ep.Address.Equals(IPAddress.Any) ||
            ep.Address.Equals(IPAddress.IPv6Any) || ep.Address.IsIPv6LinkLocal)
            return null;
        if (ep.Address.GetAddressBytes()[0] == 0) return null; // 0/8, ::, unspecified
        return ep;
    }

    private void FlushOrphansLocked(P2pSession s)
    {
        if (s.PeerDirId == null || _orphans.Count == 0) return;
        uint flow = ToFlowId(s.PeerDirId);
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            var (pkt, from, _) = _orphans[i];
            if (pkt.Length < 6) { _orphans.RemoveAt(i); continue; }
            uint f = (uint)((pkt[2] << 24) | (pkt[3] << 16) | (pkt[4] << 8) | pkt[5]);
            if (f != flow) continue;
            _orphans.RemoveAt(i);
            _byFlow[flow] = s;
            // Decrypt + deliver outside this lock via the normal media path.
            Task.Run(() => HandleOrphan(pkt, from));
        }
    }

    private void HandleOrphan(byte[] pkt, IPEndPoint from)
    {
        try
        {
            P2pSession? s;
            uint flow = (uint)((pkt[2] << 24) | (pkt[3] << 16) | (pkt[4] << 8) | pkt[5]);
            lock (_lock) _byFlow.TryGetValue(flow, out s);
            if (s == null || s.Disposed) return;
            if (!s.Crypto.TryOpen(pkt, out var payload, out uint counter)) return;
            lock (_lock)
            {
                if (s.Disposed) return;
                if (!s.Connected) AdoptPathLocked(s, from, reason: "buffered frame");
                s.LastInbound = Environment.TickCount64;
                if (s.MediaFrames++ == 0)
                    Logger("[P2P] media rx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame, buffered)");
            }
            _onAudio(s.ClientId, payload, counter);
        }
        catch { }
    }

    private void UnregisterLocked(P2pSession s)
    {
        if (s.PeerDirHex != null)
        {
            if (_byPeerDir.TryGetValue(s.PeerDirHex, out var cur) && cur == s)
                _byPeerDir.Remove(s.PeerDirHex);
            if (s.PeerDirId != null && _byFlow.TryGetValue(ToFlowId(s.PeerDirId), out var cur2) && cur2 == s)
                _byFlow.Remove(ToFlowId(s.PeerDirId));
        }
        List<string>? mine = null;
        foreach (var kv in _pending)
            if (ReferenceEquals(kv.Value.Session, s))
                (mine ??= new List<string>()).Add(kv.Key);
        if (mine != null) foreach (var k in mine) _pending.Remove(k);
    }

    private void UnregisterPeerLocked(P2pSession s, string? dirHex)
    {
        if (dirHex == null) return;
        if (_byPeerDir.TryGetValue(dirHex, out var cur) && cur == s) _byPeerDir.Remove(dirHex);
        if (s.PeerDirId != null && _byFlow.TryGetValue(ToFlowId(s.PeerDirId), out var cur2) && cur2 == s)
            _byFlow.Remove(ToFlowId(s.PeerDirId));
    }

    private bool RefreshLocalCandidates()
    {
        var list = new List<IPEndPoint>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) continue;
                    if (IPAddress.IsLoopback(a)) continue;
                    if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal) continue;
                    list.Add(new IPEndPoint(a, _port));
                }
            }
        }
        catch { /* platform quirk */ }

        list.Add(new IPEndPoint(IPAddress.Loopback, _port));

        bool changed;
        lock (_lock)
        {
            changed = _local.Count != list.Count;
            if (!changed)
                foreach (var ep in list)
                    if (!_local.Contains(ep)) { changed = true; break; }

            _local.Clear();
            _local.AddRange(list);
        }
        return changed;
    }

    private async Task RefreshSrflxAsync()
    {
        if (_gathering || _disposed || _udp == null) return;
        _gathering = true;
        try
        {
            var servers = _stunServers;
            if (servers.Length == 0) return;

            var tasks = new Task<IPEndPoint?>[servers.Length];
            for (int i = 0; i < servers.Length; i++) tasks[i] = QuerySrflxAsync(servers[i]);
            IPEndPoint?[] results;
            try { results = await Task.WhenAll(tasks); }
            catch { return; }

            var found = new List<IPEndPoint>();
            foreach (var r in results)
                if (r != null && !found.Contains(r)) found.Add(r);

            int answered = 0;
            foreach (var r in results) if (r != null) answered++;
            Logger("[P2P] STUN answers " + answered + "/" + servers.Length);

            bool changed = false;
            lock (_lock)
            {
                _srflxAt = Environment.TickCount64;
                if (found.Count > 0)
                {
                    // Replace, don't accumulate: the public mapping can move
                    // mid-session, and a stale entry crowds out the fresh one
                    // from the peer's MaxTargets budget.
                    changed = _srflx.Count != found.Count;
                    if (!changed)
                        foreach (var ep in found)
                            if (!_srflx.Contains(ep)) { changed = true; break; }
                    _srflx.Clear();
                    _srflx.AddRange(found);
                    _srflxKeys.Clear();
                    foreach (var ep in _srflx) _srflxKeys.Add(ep.ToString());
                }
            }

            if (changed)
            {
                Logger("[P2P] srflx candidates: " + string.Join(", ", found.Select(f => f.ToString())));
                ResendHellos();
            }
        }
        finally { _gathering = false; }
    }

    private void PublishSrflx(IPEndPoint ep)
    {
        bool changed;
        lock (_lock)
        {
            if (_srflxKeys.Add(ep.ToString()))
            {
                _srflx.Add(ep);
                changed = true;
            }
            else changed = false;
        }
        if (!changed) return;
        Logger("[P2P] srflx candidate: " + ep);
        ResendHellos();
    }

    private async Task<IPEndPoint?> QuerySrflxAsync(string spec)
    {
        if (!TryParseStunSpec(spec, out var host, out var port)) return null;

        IPAddress? addr = null;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
            if (addr == null)
                foreach (var a in addrs)
                    if (a.AddressFamily == AddressFamily.InterNetworkV6) { addr = a; break; }
        }
        catch { return null; }
        if (addr == null) return null;

        var dest = new IPEndPoint(addr, port);
        for (int attempt = 0; attempt < StunAttempts; attempt++)
        {
            if (_disposed) return null;
            var txId = StunMessage.NewTxId();
            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = ToHex(txId);
            lock (_lock)
                _pending[key] = new Pending { Dest = dest, Tcs = tcs, SentAt = Environment.TickCount64 };

            try
            {
                var req = StunMessage.BuildBindingRequest(txId, null);
                UdpClient? udp;
                lock (_lock) udp = _udp;
                udp?.Send(req, req.Length, dest);
            }
            catch
            {
                lock (_lock) _pending.Remove(key);
                return null;
            }

            IPEndPoint? mapped = null;
            try { mapped = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(StunTimeoutMs)); }
            catch (TimeoutException) { lock (_lock) _pending.Remove(key); }

            if (mapped != null && IsUsefulSrflx(mapped))
            {
                Dbg("[P2P] STUN " + host + ":" + port + " → " + mapped);
                PublishSrflx(mapped);
                return mapped;
            }
            if (mapped == null) await Task.Delay(150);
        }
        return null;
    }

    private bool IsUsefulSrflx(IPEndPoint ep)
    {
        if (ep.Address.Equals(IPAddress.Any) || IPAddress.IsLoopback(ep.Address)) return false;
        if (ep.Address.GetAddressBytes()[0] == 0) return false;
        lock (_lock)
        {
            foreach (var l in _local)
                if (l.Address.Equals(ep.Address)) return false;
        }
        return true;
    }

    private static bool TryParseStunSpec(string spec, out string host, out int port)
    {
        host = string.Empty;
        port = 3478;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var s = spec.Trim();
        foreach (var prefix in new[] { "stun:", "STUN:", "stuns:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(prefix.Length); break; }
        if (s.Length == 0) return false;

        // IPv6 literal "[2001:db8::1]:3478"
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close <= 1) return false;
            host = s.Substring(1, close - 1);
            if (close + 1 < s.Length && s[close + 1] == ':' &&
                int.TryParse(s.Substring(close + 2), out var p6) && p6 > 0 && p6 <= 65535) port = p6;
            return host.Length > 0;
        }

        int colon = s.LastIndexOf(':');
        if (colon > 0 && colon < s.Length - 1 && s.IndexOf(':') == colon)
        {
            if (int.TryParse(s.Substring(colon + 1), out var p) && p > 0 && p <= 65535)
            {
                host = s.Substring(0, colon);
                port = p;
                return host.Length > 0;
            }
            return false;
        }
        host = s;
        return true;
    }

    private static string[] LoadStunServers()
    {
        string raw;
        try { raw = VoiceConfig.P2PStunServers; }
        catch { raw = string.Empty; }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim();
            if (s.Length == 0 || !seen.Add(s)) continue;
            list.Add(s);
        }
        return list.ToArray();
    }

    private static bool EqualsAddress(IPEndPoint? a, IPEndPoint? b)
        => a != null && b != null && a.Equals(b);

    private static uint ToFlowId(byte[] dir)
        => (uint)((dir[0] << 24) | (dir[1] << 16) | (dir[2] << 8) | dir[3]);

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[]? FromHex(string hex)
    {
        if (hex == null || hex.Length == 0 || hex.Length % 2 != 0) return null;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    private static void Logger(string msg)
    {
        try { InterstellarPlugin.Logger?.LogInfo(msg); }
        catch { /* teardown */ }
    }

    // Per-server / per-packet chatter; goes to BepInEx/LogOutput.log only.
    private static void Dbg(string msg)
    {
        try { InterstellarPlugin.Logger?.LogDebug(msg); }
        catch { /* teardown */ }
    }
}