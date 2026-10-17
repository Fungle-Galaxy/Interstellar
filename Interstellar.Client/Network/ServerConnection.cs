using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Interstellar.Network.P2P;
using Interstellar.Voice;

namespace Interstellar.Network;

internal interface IConnectionContext
{
    void OnAudioFrameReceived(int clientId, float[] samples, int length);
    void OnClientConnected(int clientId);
    void OnClientDisconnected(int clientId);
    void OnClientProfileUpdated(int clientId, string playerName, byte playerId);
    void OnReceiveMuteStatus(int clientId, bool isMute, bool isImpostorRadio);
    void OnCustomMessageReceived(byte[] message);
    void OnHostSettingsReceived(byte[] rawSettings);
    void OnServerInfoReceived(int optimalPlayers, int totalClients, string serverUrl);
}

internal class ServerConnection : IConnectionContext, IDisposable
{
    private readonly string _roomCode;
    private readonly IConnectionContext _context;
    private readonly string _wsUrl;
    private readonly string _httpOrigin;
    private WebSocket? _sws;
    private CancellationTokenSource? _cts;
    private bool _disposed, _connected;
    private int _connecting;
    private string? _sid;
    /// <summary>P2P media transport: STUN discovery, hole punching and the sealed
    /// UDP media path. Media lives here; the websocket is control-plane only.</summary>
    private P2pTransport? _p2p;
    private int _localPlayerId, _localClientId;
    private bool _hasIds;
    private bool _joinIssued;
    private bool _joinPending;
    private bool _socketReady;
    /// <summary>The socket.io handshake completed on the current RunLoop
    /// iteration. Kept across the reconnect boundary so the loop can tell
    /// "server closed a healthy session" (routine, reconnect immediately)
    /// apart from "connection never came up" (the visible three-attempt
    /// flow the user asked for).</summary>
    private bool _handshakeDone;

    private readonly ConcurrentDictionary<string, (int playerId, int clientId)> _peers = new();
    // Per-peer liveness: the server never tells us a peer died, so we prove the
    // link is alive with an "HB:" marker every 5s (independent of speech/VAD).
    // If a peer we have been sending to stops answering, our peer map is stale
    // and re-requesting setClients is the fix — this is what repairs the
    // occasional "neither side can hear the other" state without a refresh.
    private readonly ConcurrentDictionary<int, (int firstSeen, int lastBeat)> _peerBeat = new();
    // Wall-clock of the last packet we actually RECEIVED from each peer. Kept
    // apart from _peerBeat because AddPeer seeds lastBeat with "now" as a
    // registration grace — using that to answer "have I heard from them" would
    // make every freshly announced peer look alive, and the Android watchdog
    // that guards against a broken relay would never fire.
    private readonly ConcurrentDictionary<int, int> _lastRecv = new();
    private const int HeartbeatPeriodMs = 5000;
    private const int HeartbeatTimeoutMs = 15000;
    public bool HasPeers => !_peers.IsEmpty;
    private readonly HashSet<string> _unknownSignalLogged = new();
    private int _parseErrors;
    // TX side: microphones do not always deliver valid Opus frame lengths.
    private readonly float[] _txAccum = new float[2880]; // up to 60ms @48k
    private int _txAccumLen;
    private int _txFrames, _txEncodeErrors, _lastResyncTick;
    private int _resyncBackoffMs = 5000;
    private const int ResyncMinMs = 5000;
    private const int ResyncMaxMs = 30000;
    public int TxFrames => _txFrames;
    public int TxEncodeErrors => _txEncodeErrors;
    /// <summary>Per-peer decode state: the Opus decoder plus the P2P frame counter
    /// that lets us notice a dropped UDP frame instead of playing a hole. The relay
    /// path carries no counter (the server forwards base64 Opus verbatim), so it
    /// reconstructs the same signal from the arrival cadence instead.</summary>
    private sealed class VoiceDecoder
    {
        public readonly Concentus.IOpusDecoder Dec =
            Concentus.OpusCodecFactory.CreateDecoder(48000, 1);
        public bool HasCounter;
        public uint LastCounter;
        /// <summary>TickCount64 of the previous arrival; 0 until the first packet.</summary>
        public long LastArrivalMs;
        /// <summary>Recent inter-arrival gaps (ms). Their median is the stream's true
        /// cadence — it separates "a frame went missing" (elapsed ≫ median) from
        /// "the sender emits in bursts" (median ≈ 0, elapsed means batching).</summary>
        public readonly long[] Deltas = new long[8];
        public int DeltaCount;
        public int DeltaIdx;
        /// <summary>Concealment diagnostics: log the first couple of real events per
        /// peer so recovery is verifiable from the log, then stay quiet.</summary>
        public int ConcealLogged;

        public void NoteDelta(long ms)
        {
            Deltas[DeltaIdx] = ms;
            DeltaIdx = (DeltaIdx + 1) & 7;
            if (DeltaCount < 8) DeltaCount++;
        }

        public long MedianDelta()
        {
            if (DeltaCount == 0) return 0;
            Span<long> d = stackalloc long[DeltaCount];
            for (int i = 0; i < DeltaCount; i++) d[i] = Deltas[i];
            d.Sort();
            return d[DeltaCount / 2];
        }
    }
    private readonly ConcurrentDictionary<int, VoiceDecoder> _decoders = new();
    private readonly HashSet<int> _decodeErrors = new();
    /// <summary>How many frames we are willing to synthesise for one gap. Opus PLC
    /// stays plausible for ~100ms; beyond that it would invent speech nobody said
    /// and the jitter buffer would be further behind than the dropout saved.</summary>
    private const int MaxConcealFrames = 5;

    // Per-peer voice state (synced via server events):
    // mute is tracked from VAD broadcasts, radio from our RADIO: signal marker.
    private readonly ConcurrentDictionary<int, bool> _clientMuted = new();
    private readonly ConcurrentDictionary<int, bool> _clientRadio = new();
    private bool _localRadio;

    private Timer? _pingTimer;
    private int _pingInterval = 25000;
    private int _lastReceiveTick = Environment.TickCount;
    // Engine.IO v3 (socket.io 2.4.x): the *client* pings and the server answers
    // pong — the forced bcl.server.amongusclub.cn instance works exactly that
    // way (its EIO=3 mode never pings on its own: it only watches for our pings
    // and closes after pingInterval+pingTimeout of silence). A room where
    // nobody speaks carries no other traffic, so that heartbeat is the whole
    // connection's lifeline: ping every 5s from the start, and abort only after
    // the server's announced pingInterval plus a full extra interval of grace —
    // see AbortAfterSilenceMs.
    private const int PingPeriodMs = 5000;
    private const int NoDataAbortMs = 15000;
    // Visible connect flow: attempts are counted per room (reset on a
    // completed handshake), and the pacing constants live in VoiceConnStatus so
    // the loop's delay and the HUD's countdown cannot drift apart.
    private int _attempt;
    // Local mic state, kept here so it can be re-announced after a reconnect
    // (the room restarts with a fresh connection object).
    private bool _lastMute;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public int MyClientId => 0;

    public ServerConnection(IConnectionContext context, string roomCode, string region, string url)
    {
        _context = context; _roomCode = roomCode;
        var u = new Uri(url);
        _httpOrigin = u.Scheme + "://" + u.Host;
        // Always use EIO=3 (Engine.IO v3), matching the official BetterCrewLink
        // client (socket.io-client 2.4.0). HandleOpen actively sends the "40"
        // namespace connect: EIO=3 servers wait for it and never push one
        // themselves, so passively waiting left the socket.io layer dead.
        _wsUrl = (u.Scheme == "https" ? "wss" : "ws") + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + "/socket.io/?EIO=3&transport=websocket";
        // One transport, BCL semantics: direct UDP first, TURN when the punch
        // fails (or from the start under NAT fix). The websocket is control-plane
        // only — media never rides it.
        _p2p = new P2pTransport(OnP2pAudio, SendP2pSignal);
        _p2p.Start();
        StartConnectLoop();
    }

    void StartConnectLoop()
    {
        if (Interlocked.Exchange(ref _connecting, 1) == 1) return;
        _ = RunLoop();
    }

    async Task RunLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (_handshakeDone)
                {
                    // The previous socket reached Connected and was then closed
                    // by the peer. The forced BCL server did exactly that to
                    // every websocket (fixed 30s read deadline — fixed on the
                    // server, but old deployments still run), so treat it as
                    // routine maintenance: reconnect right away without burning
                    // one of the three visible attempts or flashing the retry
                    // countdown. Rejoin + roster restore are sub-second.
                    InterstellarPlugin.Logger.LogInfo(
                        "[Srv] Session closed after a completed connect — reconnecting immediately.");
                }
                else
                {
                    _attempt++;
                    VoiceConnStatus.SetConnecting(_attempt);
                }
                _handshakeDone = false;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var sws = new WebSocket(_wsUrl, _httpOrigin, token);
                _sws = sws;

                try
                {
                    InterstellarPlugin.Logger.LogInfo("[Srv] Connecting " + _wsUrl);
                    await sws.ConnectAsync();
                    _lastReceiveTick = Environment.TickCount;
                    _connected = false;
                    // Watchdog starts with the socket, not with the handshake: a
                    // server that accepts the connection and then never sends the
                    // Engine.IO open packet would otherwise hang ReceiveAsync
                    // forever. Engine pings go out unconditionally: EIO=3 servers
                    // never ping on their own, and gating them on `_connected` —
                    // which this server only flips after we send "40" (see
                    // HandleOpen) — used to leave it with nothing to read, so it
                    // dropped us at its 30s watchdog before join ever went out.
                    _pingTimer?.Dispose();
                    _pingTimer = new Timer(_ =>
                    {
                        SendRaw("2");
                        if (_connected)
                        {
                            SendHeartbeats();
                            CheckHeartbeats();
                        }
                        int silent = unchecked(Environment.TickCount - _lastReceiveTick);
                        if (silent > AbortAfterSilenceMs())
                        {
                            InterstellarPlugin.Logger.LogWarning(
                                $"[Srv] No data for {silent}ms — forcing reconnect.");
                            try { _sws?.Abort(); } catch { }
                        }
                    }, null, PingPeriodMs, PingPeriodMs);
                    await ReadLoopSimple(sws, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    InterstellarPlugin.Logger.LogWarning("[Srv] Err: " + ex.Message);
                }
                finally
                {
                    try { sws.Dispose(); } catch { }
                    _connected = false;
                    _socketReady = false;
                    _joinIssued = false;
                    _joinPending = false;
                    _pingTimer?.Dispose();
                    // The server never broadcasts "leave"/disconnect, so peers
                    // learned on a previous socket would stay half-alive across a
                    // reconnect: VCPlayers and decoders still exist, icons look
                    // connected, but no audio flows in either direction.
                    DropPeers();
                }

                if (_disposed) break;
                if (_handshakeDone)
                {
                    // Healthy session the server closed: loop back to the
                    // immediate-reconnect branch above. The short delay is only
                    // there so a server that accepts-and-drops cannot turn this
                    // into a hot loop.
                    try { await Task.Delay(1000, token); } catch { break; }
                    continue;
                }
                // The connect flow is bounded and visible: a failure parks for
                // VoiceConnStatus.RetryDelayMs (the HUD counts it down) before the
                // next of three tries; the third failure parks on the failed state
                // until a new room starts a fresh cycle.
                if (_attempt >= VoiceConnStatus.MaxAttempts)
                {
                    InterstellarPlugin.Logger.LogWarning(
                        "[Srv] " + VoiceConnStatus.MaxAttempts + " connection attempts failed — giving up until the room restarts.");
                    VoiceConnStatus.SetFailed(_attempt);
                    break;
                }
                VoiceConnStatus.SetRetrying(_attempt);
                try { await Task.Delay(VoiceConnStatus.RetryDelayMs, token); } catch { break; }
            }
        }
        finally
        {
            _connected = false;
            _pingTimer?.Dispose();
            DropPeers();
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>
    /// Liveness budget for the watchdog. Our own 5s pings (and the server's
    /// pongs to them) are what keeps an idle room's socket fed — a room where
    /// nobody speaks carries no other traffic at all. So the budget follows the
    /// announced interval: the old fixed 15s aborted between two heartbeats on
    /// EVERY idle connection (the reconnect churn that kept two local players
    /// from ever pairing up).
    /// </summary>
    int AbortAfterSilenceMs()
    {
        int pi = _pingInterval;
        if (pi <= 0) pi = 25000;
        return Math.Max(NoDataAbortMs, pi + 20000);
    }

    /// <summary>
    /// Forget every peer learned on the previous socket (see RunLoop's finally).
    /// Mirrors what a real "leave" broadcast from the server would have done.
    /// </summary>
    void DropPeers()
    {
        _p2p?.Clear();
        foreach (var v in _peers.Values)
        {
            _decoders.TryRemove(v.clientId, out _);
            _context.OnClientDisconnected(v.clientId);
        }
        _peers.Clear();
        _peerBeat.Clear();
        _lastRecv.Clear();
        _txAccumLen = 0;
        _unknownSignalLogged.Clear();
        _clientMuted.Clear();
        _clientRadio.Clear();
        _decodeErrors.Clear();
    }

    async Task ReadLoopSimple(WebSocket sws, CancellationToken token)
    {
        var sb = new StringBuilder();
        while (sws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var (type, data) = await sws.ReceiveAsync();
            if (type == WebSocketMessageType.Close)
            {
                break;
            }
            sb.Append(Encoding.UTF8.GetString(data));
            HandleEngineIOMessage(sb.ToString());
            sb.Clear();
        }
    }

    void HandleEngineIOMessage(string data)
    {
        _lastReceiveTick = Environment.TickCount;
        if (string.IsNullOrEmpty(data)) return;
        switch (data[0])
        {
            case '0': HandleOpen(data.Substring(1)); break;
            case '2': SendRaw("3"); break;
            case '4': HandleSio(data.Substring(1)); break;
        }
    }

    void HandleOpen(string payload)
    {
        try
        {
            using var d = JsonDocument.Parse(payload);
            var r = d.RootElement;
            _sid = r.TryGetProperty("sid", out var s) ? s.GetString() : "";
            _pingInterval = r.TryGetProperty("pingInterval", out var pi) ? pi.GetInt32() : 25000;
            InterstellarPlugin.Logger.LogInfo($"[Srv] Open sid={_sid} pingInterval={_pingInterval}ms");
            // Attach the default namespace ourselves — the "40" every socket.io
            // 1.x/2.x (protocol v5) client sends right after the handshake.
            // The forced BCL server only ever ANSWERS it ("40" back → HandleSio
            // flips `_connected` → pings flow, join is scheduled); it never
            // pushes a connect of its own. Waiting for one left `_connected`
            // false forever: no engine pings went out (its 30s read watchdog then
            // closed us) and join was never emitted (no roster, so two players
            // could never pair). Servers that do push a connect answer this the
            // same way — HandleSio's '0' branch is idempotent.
            SendRaw("40");
        }
        catch { }
    }

    void HandleSio(string payload)
    {
        if (payload.Length < 1) return;
        // socket.io packet type: 0=connect, 1=disconnect, 2=event, 3=ack, 4=error
        var sioType = payload[0];
        if (sioType == '0')
        {
            _connected = true;
            _socketReady = true;
            _handshakeDone = true;
            _joinIssued = false;
            _joinPending = false;
            // A completed handshake is a successful attempt: any later drop
            // restarts the three-attempt flow from the top, not from where the
            // previous cycle stopped.
            _attempt = 0;
            VoiceConnStatus.SetConnected();
            if (_hasIds) _ = ScheduleJoinAsync();
            return;
        }
        if (sioType == '1')
        {
            _connected = false;
            return;
        }
        if (sioType == '4')
        {
            InterstellarPlugin.Logger.LogWarning("[Srv] Socket.io error packet");
            return;
        }
        // socket.io type 2 = event: 2["eventName",args...]
        if (sioType != '2' || payload.Length < 3) return;
        try
        {
            using var d = JsonDocument.Parse(payload.Substring(1));
            var arr = d.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
            switch (arr[0].GetString() ?? "")
            {
                case "setClient": OnSetClient(arr); break;
                case "setClients": OnSetClients(arr); break;
                case "join": OnJoin(arr); break;
                case "VAD": OnVAD(arr); break;
                case "signal": OnSignal(arr); break;
                case "clientPeerConfig":
                    // BCL parity: pushed the moment the socket connects —
                    // {forceRelayOnly, iceServers:[{urls, username, credential,
                    // region}]}. This is where TURN credentials come from: the
                    // client binary ships none, and nothing here is logged but
                    // counts and region tags.
                    if (arr.GetArrayLength() >= 2 && arr[1].ValueKind == JsonValueKind.Object)
                    {
                        Interstellar.Network.IceConfig.Apply(arr[1].GetRawText());
                        _p2p?.SetStunServers();
                    }
                    break;
                case "new_lobbies": PublicLobbyManager.OnNewLobbies(arr[1].GetRawText()); break;
                case "update_lobby": PublicLobbyManager.OnUpdateLobby(arr[1].GetRawText()); break;
                case "remove_lobby": PublicLobbyManager.OnRemoveLobby(arr[1].GetInt32()); break;
            }
        }
        catch (Exception ex)
        {
            // A silent drop here hid broken setClients payloads entirely.
            // Cap logging — malformed signal packets would arrive at 50/s.
            if (_parseErrors < 10)
            {
                _parseErrors++;
                InterstellarPlugin.Logger.LogWarning("[Srv] Event parse failed: " + ex.Message + " raw=" +
                    (payload.Length > 200 ? payload.Substring(0, 200) : payload));
            }
        }
    }

    void OnSetClient(JsonElement arr)
    {
        if (arr.GetArrayLength() < 3) return;
        var sid = arr[1].GetString(); if (sid == null || sid == _sid) return;
        var c = arr[2];
        int pid = c.TryGetProperty("playerId", out var p) ? p.GetInt32() : 0;
        int cid = c.TryGetProperty("clientId", out var ci) ? ci.GetInt32() : 0;
        if (IsOwnClient(cid))
        {
            InterstellarPlugin.Logger.LogWarning($"[Srv] setClient sid={sid} is our own cid={cid} (stale socket) — ignoring.");
            return;
        }
        AddPeer(sid, pid, cid);
        // The joiner learns pre-existing peers only through setClient(s) — the
        // server never sends a "join" event for members already in the room.
        // Create the audio instance here as well, otherwise a peer who has not
        // spoken yet has no VCPlayer and shows the NoConnect icon until their
        // first audio frame arrives.
        _context.OnClientConnected(cid);
        _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
        SendRadioTo(sid);
    }

    void OnSetClients(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var clients = arr[1];
        if (clients.ValueKind != JsonValueKind.Object)
        {
            InterstellarPlugin.Logger.LogWarning("[Srv] setClients payload not an object: " + clients.GetRawText());
            return;
        }
        // One short line on the console, the full roster only at Debug — a
        // busy room dumps this JSON on every join/leave and buries the rest.
        int peerCount = 0;
        foreach (var _ in clients.EnumerateObject()) peerCount++;
        InterstellarPlugin.Logger.LogInfo("[Srv] setClients: " + peerCount + " peer(s)");
        InterstellarPlugin.Logger.LogDebug("[Srv] setClients: " + clients.GetRawText());
        var seen = new HashSet<string>();
        foreach (var kv in clients.EnumerateObject())
        {
            if (kv.Name == _sid) continue;
            var c = kv.Value;
            if (c.ValueKind != JsonValueKind.Object) continue;
            int pid = c.TryGetProperty("playerId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            int cid = c.TryGetProperty("clientId", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : 0;
            if (IsOwnClient(cid)) continue;
            seen.Add(kv.Name);
            AddPeer(kv.Name, pid, cid);
            _context.OnClientConnected(cid);
            _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
            SendRadioTo(kv.Name);
        }
        // The roster is authoritative for the room as it exists right now.
        // Anything we still hold that is not in it came from a socket the server
        // has already discarded — dropping it stops us sending half our frames
        // into a dead sid and lets the live one be re-learned cleanly.
        foreach (var kv in _peers)
            if (!seen.Contains(kv.Key))
                RemovePeer(kv.Key);
    }

    void RemovePeer(string sid)
    {
        _p2p?.RemovePeer(sid);
        if (!_peers.TryRemove(sid, out var v)) return;
        InterstellarPlugin.Logger.LogInfo($"[Srv] Dropping stale peer sid={sid} cid={v.clientId} (not in roster).");
        foreach (var kv in _peers)
            if (kv.Value.clientId == v.clientId) return; // same client, another sid
        _peerBeat.TryRemove(v.clientId, out _);
        _lastRecv.TryRemove(v.clientId, out _);
        _decoders.TryRemove(v.clientId, out _);
        _context.OnClientDisconnected(v.clientId);
    }

    void OnJoin(JsonElement arr)
    {
        if (arr.GetArrayLength() < 3) return;
        var sid = arr[1].GetString(); if (sid == null || sid == _sid) return;
        var c = arr[2];
        int pid = c.TryGetProperty("playerId", out var p) ? p.GetInt32() : 0;
        int cid = c.TryGetProperty("clientId", out var ci) ? ci.GetInt32() : 0;
        if (IsOwnClient(cid))
        {
            InterstellarPlugin.Logger.LogWarning($"[Srv] join sid={sid} is our own cid={cid} (stale socket) — ignoring.");
            return;
        }
        AddPeer(sid, pid, cid);
        _context.OnClientConnected(cid);
        _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
        // A member who joins now has never heard our VAD — the server only
        // relays VAD packets emitted after they arrived, so announce it here
        // (the broadcast reaches the whole room, which just re-syncs everyone).
        Emit("VAD", !_lastMute);
        SendRadioTo(sid);
    }

    void OnVAD(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var j = arr[1];
        bool a = j.TryGetProperty("activity", out var av) && av.GetBoolean();
        int cid = 0;
        if (j.TryGetProperty("client", out var cl) && cl.TryGetProperty("clientId", out var ci)) cid = ci.GetInt32();
        _clientMuted[cid] = !a;
        _context.OnReceiveMuteStatus(cid, !a, _clientRadio.TryGetValue(cid, out var r) && r);
    }

    void OnSignal(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var j = arr[1];
        if (!j.TryGetProperty("from", out var f)) return;
        var from = f.GetString(); if (from == null || from == _sid) return;
        // Resolve sender via the sid-keyed peer map. The old reverse search
        // through _clientToSocket used 0 as a "not found" sentinel, which
        // silently discarded every frame from a peer whose Among Us clientId
        // is 0 (one-way audio: peer created the room, we joined later).
        if (!_peers.TryGetValue(from, out var peer))
        {
            if (_unknownSignalLogged.Add(from))
                InterstellarPlugin.Logger.LogWarning(
                    $"[Srv] Signal from unknown sid={from} (peers={_peers.Count}) — dropping.");
            TryResync($"unknown sid={from}");
            return;
        }
        int cid = peer.clientId;
        NotePeerAlive(cid);
        if (!j.TryGetProperty("data", out var raw) || raw.ValueKind != JsonValueKind.String) return;
        var dataStr = raw.GetString();
        if (dataStr == null) return;
        // Liveness marker (no audio, no VAD — proves only that this sid relays).
        if (dataStr.StartsWith("HB:", StringComparison.Ordinal)) return;
        // Radio-state marker: "RADIO:1" / "RADIO:0" (not Opus audio)
        if (dataStr.StartsWith("RADIO:", StringComparison.Ordinal))
        {
            bool radio = dataStr.EndsWith("1", StringComparison.Ordinal);
            _clientRadio[cid] = radio;
            _context.OnReceiveMuteStatus(cid, _clientMuted.TryGetValue(cid, out var m) && m, radio);
            return;
        }
        // Host settings marker: "HOST:" + base64
        if (dataStr.StartsWith("HOST:", StringComparison.Ordinal))
        {
            try { _context.OnHostSettingsReceived(Convert.FromBase64String(dataStr.Substring(5))); } catch { }
            return;
        }
        // Custom message marker: "MSG:" + base64
        if (dataStr.StartsWith("MSG:", StringComparison.Ordinal))
        {
            try { _context.OnCustomMessageReceived(Convert.FromBase64String(dataStr.Substring(4))); } catch { }
            return;
        }
        // P2P signalling: candidate lists + ephemeral public keys, relayed by the
        // voice server like any other signal.
        if (dataStr.StartsWith("P2P:", StringComparison.Ordinal))
        {
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(dataStr.Substring(4)));
                _p2p?.HandleSignal(from, json);
            }
            catch { }
            return;
        }
        // Anything else is not ours: BCL mode never carries media on "signal" —
        // the old base64 relay went away with the relay transport — so unknown
        // markers are dropped silently (a warning would flood at marker rate).
    }

    void DecodeOpus(int clientId, byte[] opus, uint counter = 0, bool hasCounter = false)
    {
        try
        {
            var st = _decoders.GetOrAdd(clientId, _ => new VoiceDecoder());
            long now = Environment.TickCount64;

            // Opus reports the packet's own duration — required by PLC/FEC, which
            // refuse to guess how much audio is missing.
            int frame = 0;
            try { frame = Concentus.Structs.OpusPacketInfo.GetNumSamples(opus, 48000); }
            catch { /* unparsable TOC — the plain decode below will reject it too */ }

            // Arrival bookkeeping for both paths: the relay inference below needs it,
            // and it stays warm across an Auto-mode P2P→relay switchover. Zero-length
            // deltas are skipped so a burst cannot drag the median toward 0.
            long elapsed = st.LastArrivalMs == 0 ? 0 : now - st.LastArrivalMs;
            st.LastArrivalMs = now;
            if (elapsed > 0 && frame > 0) st.NoteDelta(elapsed);

            int lost = 0;
            if (hasCounter && frame > 0)
            {
                if (st.HasCounter)
                {
                    if (counter > st.LastCounter + 1)
                    {
                        var gap = counter - st.LastCounter - 1;
                        lost = gap > MaxConcealFrames ? 0 : (int)gap;
                        if (gap > MaxConcealFrames)
                        {
                            // Far more than a stray drop — a stall, a route change or a
                            // counter reset. Skip concealment rather than fabricate.
                        }
                    }
                    else if (st.LastCounter - counter > 64)
                    {
                        // Counter ran backwards by a lot: the peer restarted its stream.
                        st.HasCounter = false;
                    }
                    // counter <= LastCounter within 64 = reordered/duplicate UDP packet:
                    // decode it, but never treat it as a gap.
                }
                st.LastCounter = counter;
                st.HasCounter = true;
            }
            else if (frame > 0 && elapsed > 0 && st.DeltaCount >= 4)
            {
                // ── Relay: the "signal" event carries no counter (the BCL server
                // forwards base64 Opus verbatim and the payload must stay the original
                // scheme), so infer losses from the arrival cadence instead. With a
                // smooth ~frame-time median, k+1 cadences of quiet before the next
                // packet means k frames never arrived (Go-server rate limiter dropping
                // signals, a capture hole, a retransmit stall). A median near zero
                // means the sender is bursty and elapsed time measures batching, not
                // loss — so nothing is fabricated. Past MaxConcealFrames it is a stall
                // or a genuine pause; the counter path makes the same call.
                long med = st.MedianDelta();
                if (med >= 4)
                {
                    int g = (int)Math.Round(elapsed / (double)med) - 1;
                    if (g >= 1 && g <= MaxConcealFrames) lost = g;
                }
            }

            var dec = st.Dec;
            // 5760 samples = Opus' documented minimum safe output buffer (120ms).
            // The old 2048 silently refused 60ms packets from peers whose mic had
            // merged buffers under load.
            var buf = new float[5760];

            if (lost > 0 && frame > 0)
            {
                // Concealment is best effort — it must never cost us the real packet
                // that just arrived, so it gets its own failure boundary.
                try
                {
                    // PLC synthesises the gap frames this packet cannot carry. On the
                    // counter path the last slot belongs to inband FEC — the current
                    // packet carries a real low-rate copy of the frame right before
                    // it — so only lost-1 frames are extrapolated. The relay path has
                    // no FEC to read: relayed frames come from a Relay-mode encoder
                    // with inband FEC off, and Concentus 2.2.2 decodes fec=true on
                    // such packets to garbage (verified: cosine −0.14 against the
                    // true frame, no exception) instead of reporting anything — so
                    // every lost frame there is concealed with PLC alone.
                    int plc = hasCounter ? lost - 1 : lost;
                    for (int i = 0; i < plc; i++)
                    {
                        int p = dec.Decode(default, buf, frame, false);
                        if (p > 0) _context.OnAudioFrameReceived(clientId, Slice(buf, p), p);
                    }
                    string how = "PLC";
                    if (hasCounter)
                    {
                        // Recover the last missing frame for real from this packet's
                        // FEC copy. If that fails for any reason (no FEC data, odd
                        // frame size, decoder complaint), conceal the slot too — a
                        // failed FEC attempt must leave a synthesised frame, never a
                        // hole in the middle of the recovered run.
                        bool fecOk = false;
                        try
                        {
                            int f = dec.Decode(opus, buf, frame, true);
                            if (f > 0)
                            {
                                _context.OnAudioFrameReceived(clientId, Slice(buf, f), f);
                                fecOk = true;
                            }
                        }
                        catch { /* falls through to the PLC below */ }
                        if (!fecOk)
                        {
                            int p = dec.Decode(default, buf, frame, false);
                            if (p > 0) _context.OnAudioFrameReceived(clientId, Slice(buf, p), p);
                        }
                        how = fecOk ? "FEC" : "FEC-fail→PLC";
                    }
                    // First couple of events per peer only — this is how "FEC never
                    // fires / relay never conceals" claims get settled from a log.
                    if (st.ConcealLogged < 2)
                    {
                        st.ConcealLogged++;
                        InterstellarPlugin.Logger.LogInfo(
                            $"[Srv] Conceal cid={clientId} lost={lost} {(hasCounter ? "P2P " : "Relay ")}{how}");
                    }
                }
                catch { /* the plain decode below still runs */ }
            }

            int n = dec.Decode(opus, buf, buf.Length);
            if (n > 0) _context.OnAudioFrameReceived(clientId, buf, n);
        }
        catch { if (_decodeErrors.Add(clientId)) InterstellarPlugin.Logger.LogWarning("[Srv] Dec " + clientId); }
    }

    /// <summary>Copy the first <paramref name="len"/> samples out of a decode buffer:
    /// the mixer keeps what it is handed, so concealed frames must not alias the
    /// buffer the next decode overwrites.</summary>
    static float[] Slice(float[] src, int len)
    {
        var a = new float[len];
        Array.Copy(src, a, len);
        return a;
    }

    /// <summary>Media arrives on the P2P socket — decode it exactly like the old
    /// websocket frames, but refresh the peer's liveness stamp on a channel that
    /// no longer carries their control traffic. The frame counter rides along so a
    /// UDP loss is concealed instead of heard as a click.</summary>
    void OnP2pAudio(int clientId, byte[] opus, uint counter)
    {
        NotePeerAlive(clientId);
        DecodeOpus(clientId, opus, counter, true);
    }

    /// <summary>P2P signalling out. Rides the existing "signal" event, which every
    /// BCL-compatible voice server relays verbatim between two clients — so no
    /// server change is needed for any of this.</summary>
    void SendP2pSignal(string sid, string json)
    {
        var payload = "P2P:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Emit("signal", new { to = sid, data = payload });
    }

    async void SendRaw(string data)
    {
        var sws = _sws; if (sws?.State != WebSocketState.Open) return;
        try
        {
            await _sendLock.WaitAsync();
            await sws.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text);
        }
        catch { }
        finally { try { _sendLock.Release(); } catch { } }
    }

    void Emit(string ev, params object[] args)
    {
        var sb = new StringBuilder();
        sb.Append("42[\"").Append(ev).Append('"');
        foreach (var a in args)
        {
            sb.Append(',');
            if (a is string s) sb.Append(JsonSerializer.Serialize(s));
            else if (a is bool b) sb.Append(b ? "true" : "false");
            else if (a is int i) sb.Append(i);
            else if (a is float f) sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (a == null) sb.Append("null");
            else sb.Append(JsonSerializer.Serialize(a));
        }
        sb.Append(']');
        SendRaw(sb.ToString());
    }

    async Task ScheduleJoinAsync()
    {
        if (_disposed || _joinIssued || _joinPending || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
        _joinPending = true;
        try
        {
            await Task.Delay(300);
            if (_disposed || _joinIssued || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
            DoJoin();
        }
        finally
        {
            _joinPending = false;
        }
    }

    void DoJoin()
    {
        if (_disposed || _joinIssued || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
        _joinIssued = true;
        InterstellarPlugin.Logger.LogInfo($"[Srv] join room={_roomCode} pid={_localPlayerId} cid={_localClientId}");
        Emit("id", _localPlayerId, _localClientId);
        Emit("join", _roomCode, _localPlayerId, _localClientId, false);
        // Re-announce local state. VAD/RADIO packets emitted while the socket was
        // down are dropped (SendRaw no-ops on a closed socket), and room members
        // never hear a VAD that was sent before they joined — so a refresh used
        // to leave the mic state out of sync on both ends.
        Emit("VAD", !_lastMute);
        if (_localRadio) BroadcastRadio(true);
        // Any HELLO that went out while the socket was down is lost for good —
        // re-announce ourselves now that signalling actually works.
        _p2p?.ResendHellos();
    }

    void BroadcastRadio(bool radio)
    {
        var marker = radio ? "RADIO:1" : "RADIO:0";
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data = marker });
    }

    void SendRadioTo(string sid)
    {
        if (!_localRadio) return;
        Emit("signal", new { to = sid, data = "RADIO:1" });
    }

    /// <summary>True when this entry is a stale copy of ourselves (the server keeps
    /// a dead socket's record until it notices the disconnect, so setClients can
    /// hand back our previous sid).</summary>
    bool IsOwnClient(int cid) => _hasIds && cid == _localClientId;

    /// <summary>Register a peer, forgetting any previous sid that carries the same
    /// clientId. The server never broadcasts leave, so a client that reconnects
    /// would otherwise stay registered under its dead socket as well — we would
    /// keep sending half our frames into a socket nobody reads.</summary>
    void AddPeer(string sid, int pid, int cid)
    {
        foreach (var kv in _peers)
            if (kv.Key != sid && kv.Value.clientId == cid)
            {
                _peers.TryRemove(kv.Key, out _);
                // Forgetting only the map entry left the P2P session for the dead socket
                // alive: it kept re-punching candidates nobody answers and SendAll still
                // aimed sealed frames at it (the mixed-transport one-way-audio session).
                _p2p?.RemovePeer(kv.Key);
            }
        _peers[sid] = (pid, cid);
        _p2p?.AddPeer(sid, cid);
        // Keep an existing beat timer: a peer that reconnected under a new sid
        // is the same live client, and resetting would delay staleness detection.
        _peerBeat.TryAdd(cid, (Environment.TickCount, Environment.TickCount));
        // …but reception must restart: a roster entry proves we know *of* them,
        // not that a single packet has come back from them.
        _lastRecv.TryRemove(cid, out _);
    }

    /// <summary>Refresh a peer's liveness stamp — any packet from them (audio,
    /// VAD, radio marker, heartbeat) proves the relay path works.</summary>
    void NotePeerAlive(int cid)
    {
        int now = Environment.TickCount;
        _peerBeat.AddOrUpdate(cid,
            (now, now),
            (_, old) => (old.firstSeen, now));
        _lastRecv[cid] = now;
        _resyncBackoffMs = ResyncMinMs;
    }

    /// <summary>True once at least one packet (audio, VAD, radio marker,
    /// heartbeat) has ever arrived from this peer on the current connection.
    /// A peer who is simply not talking sends no audio but still heartbeats —
    /// so this, not "is audio playing", is what tells a dead relay from a
    /// quiet one.</summary>
    public bool PeerHasReceived(int clientId) => _lastRecv.ContainsKey(clientId);

    void SendHeartbeats()
    {
        if (_peers.IsEmpty) return;
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data = "HB:" });
    }

    /// <summary>If a peer we are sending to has not produced a single packet in
    /// 15s (they are alive — they would be talking or heartbeating), our copy of
    /// their sid is stale. Re-request the roster instead of staying silent.</summary>
    void CheckHeartbeats()
    {
        if (_peers.IsEmpty) return;
        int now = Environment.TickCount;
        foreach (var kv in _peers)
        {
            int cid = kv.Value.clientId;
            if (!_peerBeat.TryGetValue(cid, out var b))
            {
                _peerBeat[cid] = (now, now);
                continue;
            }
            if (unchecked(now - b.lastBeat) > HeartbeatTimeoutMs)
            {
                TryResync($"no heartbeat from cid={cid} sid={kv.Key}");
                return;
            }
        }
    }

    /// <summary>Re-join the room to get a fresh setClients. The server sends each
    /// join/setClient broadcast exactly once, so a missed one otherwise leaves
    /// that stream dropped forever. Rate-limited with a backoff, reset as soon
    /// as any peer answers.</summary>
    void TryResync(string reason)
    {
        if (!_hasIds || !_connected) return;
        int now = Environment.TickCount;
        if (unchecked(now - _lastResyncTick) < _resyncBackoffMs) return;
        _lastResyncTick = now;
        InterstellarPlugin.Logger.LogWarning(
            $"[Srv] Resyncing ({reason}) — peers={_peers.Count}.");
        Emit("id", _localPlayerId, _localClientId);
        Emit("join", _roomCode, _localPlayerId, _localClientId, false);
        _resyncBackoffMs = Math.Min(_resyncBackoffMs * 2, ResyncMaxMs);
    }

    /// <summary>Forced version used as the cheap first response to "no audio is
    /// coming through" — re-request the peer list before tearing the room down.</summary>
    public void ResyncPeers(string reason)
    {
        _resyncBackoffMs = ResyncMinMs;
        TryResync(reason);
    }

    public void UpdateProfile(string name, byte pid, int clientId)
    {
        _localPlayerId = pid;
        _localClientId = clientId;
        _hasIds = true;
        Emit("id", pid, clientId);
        if (_connected && _socketReady) _ = ScheduleJoinAsync();
    }
    public void UpdateMuteStatus(bool mute, bool radio = false)
    {
        _lastMute = mute;
        Emit("VAD", !mute);
        // Broadcast impostor-radio state to all peers via the signal channel.
        // (Official BCL does this over WebRTC data channels; here we mirror it
        // through the server relay so radio mode works cross-player.)
        if (_localRadio != radio)
        {
            _localRadio = radio;
            BroadcastRadio(radio);
        }
    }

    /// <summary>
    /// Opus only accepts specific frame lengths at 48kHz — 480 (10ms), 960
    /// (20ms), 1920 (40ms) or 2880 (60ms). Microphone backends sometimes hand
    /// us anything else (NAudio buffers merging into 80ms under load, etc.) and
    /// Concentus then throws; the old `catch {}` swallowed that as a completely
    /// silent dropped frame, which is one way to lose syllables on the wire.
    /// Input is now normalized into valid frames with the remainder carried
    /// over to the next call, so standard sizes still go out immediately.
    /// </summary>
    public void SendAudio(float[] buf, int len, double ms)
    {
        if (!_connected || _peers.IsEmpty || len <= 0) return;
        try
        {
            EnsureEncoder();
            int i = 0;
            while (i < len)
            {
                int space = _txAccum.Length - _txAccumLen;
                if (space == 0) { EmitTxFrames(); space = _txAccum.Length - _txAccumLen; }
                int n = Math.Min(space, len - i);
                Array.Copy(buf, i, _txAccum, _txAccumLen, n);
                _txAccumLen += n;
                i += n;
                EmitTxFrames();
            }
        }
        catch (Exception ex)
        {
            if (++_txEncodeErrors <= 5)
                InterstellarPlugin.Logger.LogWarning("[Srv] TX failed: " + ex.Message);
        }
    }

    void EmitTxFrames()
    {
        // Fixed 20ms frames. The old ladder emitted whatever the accumulator held
        // (40/60ms under a slow mic callback), which is exactly the granularity a
        // single UDP loss wipes out — one lost packet cost a whole 40ms of speech.
        // At 960 samples a drop is 20ms, Opus' inband FEC is built around 20ms
        // frames, and the receiver's PLC/FEC arithmetic knows the duration exactly.
        // Cost is one extra frame per 40ms of audio (~8% more framing bytes), which
        // the relay server's per-second budget already covers.
        while (_txAccumLen >= 960)
        {
            const int frame = 960;
            try
            {
                int n = _encoder!.Encode(_txAccum, frame, _encBuf, _encBuf.Length);
                if (n > 2)
                {
                    var opus = new byte[n];
                    Buffer.BlockCopy(_encBuf, 0, opus, 0, n);
                    SendFrame(opus);
                    Interlocked.Increment(ref _txFrames);
                }
            }
            catch (Exception ex)
            {
                if (++_txEncodeErrors <= 5)
                    InterstellarPlugin.Logger.LogWarning(
                        $"[Srv] Opus encode failed ({frame} samples): {ex.Message}");
            }
            // Always advance, even after a failure — a wedged accumulator would
            // stop all further voice permanently.
            int rem = _txAccumLen - frame;
            if (rem > 0) Array.Copy(_txAccum, frame, _txAccum, 0, rem);
            _txAccumLen = rem;
        }
    }

    /// <summary>Route one encoded frame: BCL mode has a single path per peer — the
    /// P2P transport sends direct UDP and switches that peer to TURN the moment
    /// its punch fails (always TURN under NAT fix). The server's "signal" event
    /// carries only control messages now, never media.</summary>
    void SendFrame(byte[] opus)
    {
        _p2p?.SendAll(opus);
    }

    Concentus.IOpusEncoder? _encoder; byte[] _encBuf = new byte[2048];
    void EnsureEncoder()
    {
        if (_encoder != null) return;
        _encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.UseVBR = true;
        // Every path is UDP now (direct or TURN relay), so frames can drop: FEC
        // bits are cheap insurance — the receiver rebuilds the lost frame from
        // the next packet's copy instead of playing a hole. The bitrate the old
        // server relay would have carried goes to audio instead.
        _encoder.Bitrate = 96000;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 15;
    }

    /// <summary>True while the control socket can carry emits. SendRaw drops frames
    /// silently before it is up (fresh room, reconnect), so the lobby publisher
    /// gates its one-shot state change on this.</summary>
    public bool IsControlSocketOpen => _sws?.State == WebSocketState.Open;

    public async Task PublishLobby(string code, PublicLobbyManager.LobbyInfo info)
    {
        if (!IsControlSocketOpen)
            InterstellarPlugin.Logger?.LogWarning("[VC] PublishLobby dropped: control socket not open");
        Emit("lobby", code, new { title = info.title, host = info.host, current_players = info.current_players, max_players = info.max_players, language = info.language, mods = info.mods, isPublic = true, isPublic2 = true, server = info.server, gameState = info.gameState });
        await Task.CompletedTask;
    }
    public async Task RemoveLobby(string code) { Emit("remove_lobby", code); await Task.CompletedTask; }
    public async Task JoinLobby(int id, Action<int, string, string> cb) { cb(1, "", ""); await Task.CompletedTask; }
    public async Task WatchLobbyBrowser(bool w)
    { if (w) { Emit("lobbybrowser", true); PublicLobbyManager.StartWatching(); } else { Emit("lobbybrowser", false); PublicLobbyManager.StopWatching(); } await Task.CompletedTask; }

    void IConnectionContext.OnAudioFrameReceived(int c, float[] s, int n) => _context.OnAudioFrameReceived(c, s, n);
    void IConnectionContext.OnClientConnected(int c) => _context.OnClientConnected(c);
    void IConnectionContext.OnClientDisconnected(int c) { _decoders.TryRemove(c, out _); _context.OnClientDisconnected(c); }
    void IConnectionContext.OnClientProfileUpdated(int c, string n, byte p) => _context.OnClientProfileUpdated(c, n, p);
    void IConnectionContext.OnReceiveMuteStatus(int c, bool m, bool r) => _context.OnReceiveMuteStatus(c, m, r);
    void IConnectionContext.OnCustomMessageReceived(byte[] m) => _context.OnCustomMessageReceived(m);
    void IConnectionContext.OnHostSettingsReceived(byte[] s) => _context.OnHostSettingsReceived(s);
    void IConnectionContext.OnServerInfoReceived(int o, int t, string u) => _context.OnServerInfoReceived(o, t, u);

    // Relay host settings / custom messages to every peer via the signal channel,
    // mirroring how official BCL sends them over WebRTC data channels.
    void BroadcastMarker(string prefix, byte[] payload)
    {
        if (!_connected || _peers.IsEmpty || payload == null || payload.Length == 0) return;
        var b64 = Convert.ToBase64String(payload);
        var data = prefix + b64;
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data });
    }

    public void SendCustomMessage(byte[] m) => BroadcastMarker("MSG:", m);
    public void SendHostSettings(byte[] s) => BroadcastMarker("HOST:", s);

    public void Disconnect()
    {
        _disposed = true;
        try { _p2p?.Dispose(); } catch { }
        _p2p = null;
        _pingTimer?.Dispose();
        _cts?.Cancel();
        var sws = _sws; _sws = null;
        if (sws == null) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (sws.State == WebSocketState.Open)
                    sws.SendAsync(Encoding.UTF8.GetBytes("42[\"leave\"]"), WebSocketMessageType.Text).Wait(2000);
            }
            catch { }
            try { sws.CloseAsync().Wait(3000); } catch { }
            try { sws.Dispose(); } catch { }
        });
        InterstellarPlugin.Logger.LogInfo("[Srv] Disconnected");
    }
    public void Dispose() { Disconnect(); }
}