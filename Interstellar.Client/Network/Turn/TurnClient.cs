using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Interstellar.Network.Turn;

internal sealed class TurnClient : IDisposable
{
    private const ushort TAllocate = 0x0003;
    private const ushort TAllocateOk = 0x0103;
    private const ushort TRefresh = 0x0004;
    private const ushort TRefreshOk = 0x0104;
    private const ushort TCreatePermission = 0x0008;
    private const ushort TCreatePermissionOk = 0x0108;
    private const ushort TChannelBind = 0x0009;
    private const ushort TChannelBindOk = 0x0109;
    private const ushort TDataIndication = 0x0117;
    private const ushort TSendIndication = 0x0016;

    private const ushort AUserName = 0x0006;
    private const ushort AMessageIntegrity = 0x0008;
    private const ushort AErrorCode = 0x0009;
    private const ushort AChannelNumber = 0x000C;
    private const ushort ALifetime = 0x000D;
    private const ushort AXorPeerAddress = 0x0012;
    private const ushort AData = 0x0013;
    private const ushort ARealm = 0x0014;
    private const ushort ANonce = 0x0015;
    private const ushort AXorRelayedAddress = 0x0016;
    private const ushort ARequestedTransport = 0x0019;
    private const ushort AXorMappedAddress = 0x0020;

    private const int HeaderLen = 20;
    private const int TxIdLen = 12;
    private static readonly byte[] Magic = { 0x21, 0x12, 0xA4, 0x42 };

    private const ushort ChannelBase = 0x4000;
    private const int TxRetryMs = 500;
    private const int DefaultLifetimeSec = 600;
    private const int PermissionLifetimeSec = 300;
    private const int ChannelLifetimeSec = 600;
    private const int RefreshSafetySec = 60;
    private const int MaxRefreshFailures = 3;

    // Inbound payload + observed source, delivered like a P2P datagram.
    public Action<byte[], IPEndPoint>? OnPayload;
    // Allocation is dead — transport re-selects.
    public Action? OnDead;

    public readonly string Spec;
    private readonly IPEndPoint _server;
    private readonly string _user;
    private readonly string _pass;
    private readonly object _lock = new();

    private UdpClient? _udp;
    private volatile bool _running;
    private bool _disposed;

    private string _realm = "";
    private string _nonce = "";
    private byte[]? _key;

    private IPEndPoint? _relayed;
    private int _lifetimeSec = DefaultLifetimeSec;
    private volatile bool _ready;
    private long _rttMs;
    private int _refreshFailures;
    private int _chanBindRejectedLogged;
    private readonly HashSet<string> _bindPending = new();
    private int _sentLogged, _chanMissLogged, _rxLogged, _sendFailLogged;

    private sealed class PendingTx
    {
        public TaskCompletionSource<TurnMsg> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Raw = Array.Empty<byte>();
        public long DeadlineAt;
    }

    private readonly Dictionary<string, PendingTx> _pending = new(StringComparer.Ordinal);

    private sealed class PeerState
    {
        public IPEndPoint Ep = null!;
        public ushort Channel;
        public long PermExpiresAt;
        public long ChanExpiresAt;
    }

    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private ushort _nextChannel = ChannelBase;
    private Timer? _maintain;
    private Task? _receiveTask;

    private TurnClient(string spec, IPEndPoint server, string user, string pass)
    {
        Spec = spec;
        _server = server;
        _user = user;
        _pass = pass;
    }

    // "turn:host:port[?transport=udp]". TLS and TCP are rejected.
    public static bool TryParseSpec(string spec, out string host, out int port)
    {
        host = "";
        port = 3478;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var s = spec.Trim();

        int q = s.IndexOf('?');
        if (q >= 0)
        {
            var query = s.Substring(q + 1);
            s = s.Substring(0, q);
            if (query.IndexOf("transport=tcp", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        foreach (var p in new[] { "turn:", "TURN:" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(p.Length);
                goto haveHost;
            }
        return false;

    haveHost:
        if (s.StartsWith("stun", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length == 0) return false;

        if (s.StartsWith('[')) // [2001:db8::1]:3478
        {
            int close = s.IndexOf(']');
            if (close <= 1) return false;
            host = s.Substring(1, close - 1);
            if (close + 1 < s.Length && s[close + 1] == ':' &&
                int.TryParse(s.Substring(close + 2), out var p6) && p6 > 0 && p6 <= 65535)
                port = p6;
            return host.Length > 0;
        }

        int colon = s.LastIndexOf(':');
        if (colon > 0 && colon == s.IndexOf(':') && colon < s.Length - 1)
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

    // Resolve + Allocate with an overall deadline. Returns null on any failure.
    public static async Task<TurnClient?> AllocateAsync(
        string spec, string user, string pass, int timeoutMs)
    {
        if (!TryParseSpec(spec, out var host, out var port)) return null;

        IPAddress? addr = null;
        if (IPAddress.TryParse(host, out var literal))
            addr = literal;
        else
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(
                    TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 3000)));
                foreach (var a in addrs)
                    if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
                if (addr == null)
                    foreach (var a in addrs)
                        if (a.AddressFamily == AddressFamily.InterNetworkV6) { addr = a; break; }
            }
            catch { return null; }
        }
        if (addr == null) return null;

        var c = new TurnClient(spec, new IPEndPoint(addr, port), user ?? "", pass ?? "");
        if (await c.RunAllocateAsync(timeoutMs)) return c;
        c.Dispose();
        return null;
    }

    private async Task<bool> RunAllocateAsync(int timeoutMs)
    {
        try
        {
            _udp = new UdpClient(0);
            _udp.Client.Blocking = true;
            _udp.Client.ReceiveTimeout = 500;
            try
            {
                const int SioUdpConnreset = -1744830452;
                _udp.Client.IOControl(SioUdpConnreset, new byte[] { 0 }, null!);
            }
            catch { /* non-Windows */ }
            _running = true;
        }
        catch { return false; }

        _receiveTask = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);

        var sw = Stopwatch.StartNew();
        var deadline = Environment.TickCount64 + timeoutMs;

        // Anonymous allocate — grab REALM/NONCE from a 401 challenge.
        // Servers with no auth answer 200 directly.
        var resp = await TransactionAsync(BuildAllocate(auth: false), deadline);
        if (resp == null) return false;
        if (resp.Type != TAllocateOk)
        {
            if (resp.Type != (TAllocate | 0x0110)
                || (resp.ErrorCode != 401 && resp.ErrorCode != 438)
                || !TakeAuthAttrs(resp))
            {
                if (resp.Type == (TAllocate | 0x0110))
                    Log("[TURN] " + Spec + " allocate rejected: " + resp.ErrorCode);
                return false;
            }

            // Authenticated allocate. Stale nonce gets one fresh retry.
            resp = await TransactionAsync(BuildAllocate(auth: true), deadline);
            if (resp != null && resp.Type == (TAllocate | 0x0110) && resp.ErrorCode == 438
                && TakeAuthAttrs(resp))
                resp = await TransactionAsync(BuildAllocate(auth: true), deadline);
            if (resp == null || resp.Type != TAllocateOk)
            {
                if (resp != null && resp.Type == (TAllocate | 0x0110))
                    Log("[TURN] " + Spec + " allocate rejected: " + resp.ErrorCode);
                return false;
            }
        }

        var relayed = ParseXorAddress(resp, AXorRelayedAddress);
        if (relayed == null)
        {
            Log("[TURN] " + Spec + " answer has no XOR-RELAYED-ADDRESS");
            return false;
        }
        if (resp.Alts.TryGetValue(ALifetime, out var lt) && lt.Length >= 4)
            _lifetimeSec = Math.Clamp((lt[0] << 24) | (lt[1] << 16) | (lt[2] << 8) | lt[3],
                60, 3600);

        _relayed = relayed;
        _rttMs = sw.ElapsedMilliseconds;
        _ready = true;
        _refreshFailures = 0;

        _maintain = new Timer(_ => SafeMaintain(), null,
            TimeSpan.FromSeconds(Math.Max(5, _lifetimeSec / 2)),
            TimeSpan.FromSeconds(30));

        Dbg("[TURN] ready " + Spec + " relayed=" + relayed + " rtt=" + _rttMs + "ms" +
            " lifetime=" + _lifetimeSec + "s");
        return true;
    }

    public IPEndPoint? Relayed => _relayed;
    public bool Ready => _ready && _relayed != null;
    public long RttMs => _rttMs;

    // CreatePermission + ChannelBind for a peer. Idempotent, fire-and-forget.
    public void PreparePeer(IPEndPoint peer)
    {
        if (peer == null || !Ready) return;
        _ = Task.Run(async () =>
        {
            if (!await CreatePermissionAsync(peer)) return;
            await ChannelBindAsync(peer);
        });
    }

    // Send one payload through the relay. False if no channel is bound yet —
    // caller should fall back to the direct path for this frame.
    public bool Send(byte[] data, IPEndPoint dest)
    {
        if (data == null || data.Length == 0 || dest == null) return false;
        byte[]? frame = null;
        ushort boundCh = 0;
        lock (_lock)
        {
            if (!_ready || _udp == null) return false;
            if (_peers.TryGetValue(dest.ToString(), out var st) && st.Channel != 0
                && st.ChanExpiresAt > Environment.TickCount64)
            {
                boundCh = st.Channel;
                frame = new byte[4 + data.Length];
                frame[0] = (byte)(0x40 | ((st.Channel >> 8) & 0x3F));
                frame[1] = (byte)(st.Channel & 0xFF);
                frame[2] = (byte)(data.Length >> 8);
                frame[3] = (byte)data.Length;
                Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            }
        }
        if (frame != null)
        {
            if (Interlocked.Exchange(ref _sentLogged, 1) == 0)
                Log("[TURN] " + Spec + " first ChannelData ch=0x" + boundCh.ToString("X4")
                    + " -> " + dest + " len=" + data.Length);
            // ChannelData always goes to the TURN server; the channel number
            // inside the frame does the demux. Sending it to the peer's
            // relayed port silently drops every packet.
            try { _udp!.Send(frame, frame.Length, _server); return true; }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _sendFailLogged) <= 5)
                    Log("[TURN] " + Spec + " ChannelData send failed: " + ex.Message);
                return false;
            }
        }
        if (Interlocked.Exchange(ref _chanMissLogged, 1) == 0)
            Log("[TURN] " + Spec + " no live channel for " + dest
                + " (peers=" + _peers.Count + ") — Send indication fallback");

        // No bound channel — fall back to Send indication (RFC 5766 §6).
        // Some servers refuse ChannelBind with 400 while CreatePermission
        // succeeds; without this fallback every packet is dropped silently.
        var key = _key;
        if (key == null || _nonce.Length == 0) return false;
        byte[] ind;
        try
        {
            ind = Build(TSendIndication, new List<(ushort, byte[])>
            {
                (AXorPeerAddress, XorAddressValue(dest, EmptyTx)),
                (AData, data),
            }, key);
        }
        catch { return false; }
        try { _udp!.Send(ind, ind.Length, _server); return true; }
        catch { return false; }
    }

    public async Task<bool> CreatePermissionAsync(IPEndPoint peer)
    {
        if (!Ready || peer == null) return false;
        var attrs = new List<(ushort, byte[])> { (AXorPeerAddress, XorAddressValue(peer, EmptyTx)) };
        var resp = await SendAuthedAsync(TCreatePermission, attrs);
        if (resp == null || resp.Type != TCreatePermissionOk)
        {
            if (resp != null && resp.Type == (TCreatePermission | 0x0110))
                Log("[TURN] " + Spec + " permission " + peer + " rejected: " + resp.ErrorCode);
            return false;
        }
        lock (_lock)
        {
            var key = peer.ToString();
            if (!_peers.TryGetValue(key, out var st)) _peers[key] = st = new PeerState { Ep = peer };
            st.PermExpiresAt = Environment.TickCount64 + (long)PermissionLifetimeSec * 1000;
        }
        return true;
    }

    public async Task<bool> ChannelBindAsync(IPEndPoint peer)
    {
        if (!Ready || peer == null) return false;
        ushort channel;
        string key;
        lock (_lock)
        {
            key = peer.ToString();
            if (_peers.TryGetValue(key, out var existing) && existing.Channel != 0
                && existing.ChanExpiresAt > Environment.TickCount64)
                return true;
            // A bind for this peer is already in flight — a second one would
            // race with a fresh channel number.
            if (!_bindPending.Add(key)) return true;
            channel = _nextChannel++;
            if (_nextChannel > 0x7FFF) _nextChannel = ChannelBase;
        }

        try
        {
            var attrs = new List<(ushort, byte[])>
            {
                (AChannelNumber, ChannelNumberValue(channel)),
                (AXorPeerAddress, XorAddressValue(peer, EmptyTx)),
            };
            var resp = await SendAuthedAsync(TChannelBind, attrs);
            if (resp == null || resp.Type != TChannelBindOk)
            {
                // Only log the first failure — otherwise every peer on every
                // tick spams the log. Send indications keep working.
                if (Interlocked.Exchange(ref _chanBindRejectedLogged, 1) == 0)
                {
                    Log("[TURN] " + Spec + " channelbind " + peer + " rejected: " +
                        (resp != null ? resp.ErrorCode.ToString() : "timeout") +
                        " — using Send indications instead");
                    Log("[TURN] chbind-diag channel=0x" + channel.ToString("X4") +
                        " cn-value=" + Hex(ChannelNumberValue(channel)) +
                        " peer-value=" + Hex(XorAddressValue(peer, EmptyTx)) +
                        " req=" + Hex(BuildAuthenticated(TChannelBind, attrs)) +
                        " resp=" + (resp != null ? Hex(resp.Raw) : "null"));
                }
                return false;
            }
            lock (_lock)
            {
                if (!_peers.TryGetValue(key, out var st)) _peers[key] = st = new PeerState { Ep = peer };
                st.Channel = channel;
                st.ChanExpiresAt = Environment.TickCount64 + (long)ChannelLifetimeSec * 1000;
                if (st.PermExpiresAt == 0)
                    st.PermExpiresAt = Environment.TickCount64 + (long)PermissionLifetimeSec * 1000;
            }
            Dbg("[TURN] " + Spec + " bound ch=0x" + channel.ToString("X4") + " <-> " + peer);
            return true;
        }
        finally
        {
            lock (_lock) _bindPending.Remove(key);
        }
    }

    private void SafeMaintain()
    {
        try { Maintain(); }
        catch { /* never let a timer exception kill the loop */ }
    }

    private void Maintain()
    {
        if (!_ready || _disposed) return;

        // Refresh the allocation.
        _ = Task.Run(async () =>
        {
            var resp = await SendAuthedAsync(TRefresh,
                new List<(ushort, byte[])> { (ALifetime, LifetimeValue((uint)_lifetimeSec)) });
            if (resp != null && resp.Type == TRefreshOk)
            {
                if (resp.Alts.TryGetValue(ALifetime, out var lt) && lt.Length >= 4)
                    _lifetimeSec = Math.Clamp((lt[0] << 24) | (lt[1] << 16) | (lt[2] << 8) | lt[3],
                        60, 3600);
                Interlocked.Exchange(ref _refreshFailures, 0);
            }
            else if (Interlocked.Increment(ref _refreshFailures) >= MaxRefreshFailures)
            {
                Log("[TURN] " + Spec + " refresh failing — allocation considered dead");
                _ready = false;
                try { OnDead?.Invoke(); } catch { }
            }
        });

        // Re-arm permissions/channels close to expiry.
        List<PeerState>? due = null;
        long now = Environment.TickCount64;
        lock (_lock)
        {
            foreach (var st in _peers.Values)
                if (st.PermExpiresAt - now < RefreshSafetySec * 1000
                    || (st.Channel != 0 && st.ChanExpiresAt - now < RefreshSafetySec * 1000))
                    (due ??= new List<PeerState>()).Add(st);
        }
        if (due == null) return;
        foreach (var st in due)
        {
            _ = Task.Run(async () =>
            {
                await CreatePermissionAsync(st.Ep);
                if (st.Channel != 0) await ChannelBindAsync(st.Ep);
            });
        }
    }

    private byte[] BuildAllocate(bool auth)
    {
        var attrs = new List<(ushort, byte[])>
        {
            (ARequestedTransport, new byte[] { 17, 0, 0, 0 }), // UDP
        };
        return auth ? BuildAuthenticated(TAllocate, attrs) : Build(TAllocate, attrs, null);
    }

    private async Task<TurnMsg?> SendAuthedAsync(ushort type, List<(ushort, byte[])> attrs)
    {
        var deadline = Environment.TickCount64 + 4000;
        TurnMsg? resp;
        if (_key == null || _nonce.Length == 0)
        {
            resp = await TransactionAsync(Build(type, attrs, null), deadline);
            if (resp == null) return null;
            if (resp.Type == (type | 0x0110) && TakeAuthAttrs(resp)) goto authenticated;
            return resp;
        }

    authenticated:
        resp = await TransactionAsync(BuildAuthenticated(type, attrs), deadline);
        if (resp != null && resp.Type == (type | 0x0110)
            && (resp.ErrorCode == 438 || resp.ErrorCode == 401) && TakeAuthAttrs(resp))
            resp = await TransactionAsync(BuildAuthenticated(type, attrs), deadline);
        return resp;
    }

    private byte[] BuildAuthenticated(ushort type, List<(ushort, byte[])> attrs)
    {
        var full = new List<(ushort, byte[])>
        {
            (AUserName, Encoding.UTF8.GetBytes(_user)),
            (ARealm, Encoding.UTF8.GetBytes(_realm)),
            (ANonce, Encoding.UTF8.GetBytes(_nonce)),
        };
        full.AddRange(attrs);
        return Build(type, full, _key);
    }

    private async Task<TurnMsg?> TransactionAsync(byte[] msg, long deadlineAt)
    {
        string txKey = Hex(msg.AsSpan(8, TxIdLen));
        PendingTx? tx = null;
        try
        {
            tx = new PendingTx { Raw = msg, DeadlineAt = deadlineAt };
            lock (_lock) _pending[txKey] = tx;

            while (Environment.TickCount64 < deadlineAt && !_disposed)
            {
                try { _udp!.Send(msg, msg.Length, _server); }
                catch { return null; }

                var wait = Math.Min(TxRetryMs,
                    (int)Math.Max(1, deadlineAt - Environment.TickCount64));
                try
                {
                    var got = await tx.Tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(wait));
                    return got;
                }
                catch (TimeoutException) { /* retransmit, same txid */ }
                if (tx.Tcs.Task.IsCompleted) return await tx.Tcs.Task;
            }
            return null;
        }
        finally
        {
            lock (_lock) _pending.Remove(txKey);
        }
    }

    // Pull REALM/NONCE off a 401/438 and re-derive the key.
    private bool TakeAuthAttrs(TurnMsg resp)
    {
        if (resp.Alts.TryGetValue(ARealm, out var realm) && realm.Length > 0
            && realm.Length <= 256)
            _realm = Encoding.UTF8.GetString(realm);
        if (resp.Alts.TryGetValue(ANonce, out var nonce) && nonce.Length > 0
            && nonce.Length <= 512)
            _nonce = Encoding.UTF8.GetString(nonce);
        if (_realm.Length == 0 || _nonce.Length == 0) return false;
        _key = MD5.HashData(Encoding.UTF8.GetBytes(_user + ":" + _realm + ":" + _pass));
        return true;
    }

    private void ReceiveLoop()
    {
        while (_running && !_disposed)
        {
            UdpClient? udp;
            lock (_lock) udp = _udp;
            if (udp == null) break;

            byte[] data;
            IPEndPoint from = new(IPAddress.Any, 0);
            try { data = udp.Receive(ref from); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; continue; }
            catch { if (!_running) break; continue; }

            try { HandleDatagram(data); }
            catch { /* one bad packet must not kill the loop */ }
        }
    }

    private void HandleDatagram(byte[] d)
    {
        if (d.Length < 4) return;

        // ChannelData: 0b01xxxxxx. Channel number is 0x4000..0x7FFF on the wire.
        if ((d[0] & 0xC0) == 0x40)
        {
            if (d.Length < 4) return;
            ushort channel = (ushort)((d[0] << 8) | d[1]);
            int len = (d[2] << 8) | d[3];
            if (len <= 0 || 4 + len > d.Length) return;
            IPEndPoint? peer = null;
            lock (_lock)
            {
                foreach (var st in _peers.Values)
                    if (st.Channel == channel) { peer = st.Ep; break; }
            }
            if (Interlocked.Exchange(ref _rxLogged, 1) == 0)
                Log("[TURN] " + Spec + " first inbound ChannelData ch=0x" + channel.ToString("X4")
                    + " len=" + len + (peer == null ? " (no bound peer — dropped)" : " from " + peer));
            if (peer == null) return;
            var payload = new byte[len];
            Buffer.BlockCopy(d, 4, payload, 0, len);
            Deliver(payload, peer);
            return;
        }

        if (!StunLike(d)) return;
        var type = (ushort)((d[0] << 8) | d[1]);
        var txKey = Hex(d.AsSpan(8, TxIdLen));

        PendingTx? tx;
        lock (_lock) _pending.TryGetValue(txKey, out tx);
        if (tx != null)
        {
            var msg = ParseMsg(d);
            if (msg != null) tx.Tcs.TrySetResult(msg);
            return;
        }

        if (type != TDataIndication) return;

        var m = ParseMsg(d);
        if (m == null) return;
        var peerAddr = ParseXorAddress(m, AXorPeerAddress);
        if (peerAddr == null) return;
        if (!m.Alts.TryGetValue(AData, out var indPayload) || indPayload.Length == 0) return;
        if (Interlocked.Exchange(ref _rxLogged, 1) == 0)
            Log("[TURN] " + Spec + " first inbound Data Indication from " + peerAddr
                + " len=" + indPayload.Length);
        Deliver(indPayload, peerAddr);
    }

    private void Deliver(byte[] payload, IPEndPoint from)
    {
        try { OnPayload?.Invoke(payload, from); }
        catch { /* handler must not take the loop down */ }
    }

    private static bool StunLike(byte[] d)
        => d.Length >= HeaderLen && (d[0] & 0xC0) == 0
           && d[4] == Magic[0] && d[5] == Magic[1] && d[6] == Magic[2] && d[7] == Magic[3];

    private sealed class TurnMsg
    {
        public ushort Type;
        public int ErrorCode;
        public Dictionary<ushort, byte[]> Alts = new();
        public byte[] Raw = Array.Empty<byte>();
    }

    private static TurnMsg? ParseMsg(byte[] d)
    {
        if (!StunLike(d)) return null;
        int bodyLen = (d[2] << 8) | d[3];
        if (HeaderLen + bodyLen > d.Length) return null;
        var m = new TurnMsg
        {
            Type = (ushort)((d[0] << 8) | d[1]),
            Raw = d,
        };
        int off = HeaderLen;
        int end = HeaderLen + bodyLen;
        while (off + 4 <= end)
        {
            ushort at = (ushort)((d[off] << 8) | d[off + 1]);
            int len = (d[off + 2] << 8) | d[off + 3];
            int vOff = off + 4;
            if (len < 0 || vOff + len > end) break;
            var val = new byte[len];
            Buffer.BlockCopy(d, vOff, val, 0, len);
            if (!m.Alts.ContainsKey(at)) m.Alts[at] = val;
            if (at == AErrorCode && len >= 4)
                // ERROR-CODE: class * 100 + number. 401 = class 4 / number 1.
                m.ErrorCode = ((val[2] & 0x07) * 100) + val[3];
            off = (vOff + len + 3) & ~3;
        }
        return m;
    }

    // Build a STUN message. If miKey is given, MESSAGE-INTEGRITY is appended
    // last (length field patched to cover it, HMAC over everything before it).
    private static byte[] Build(ushort type, List<(ushort, byte[])> attrs, byte[]? miKey)
    {
        var txId = new byte[TxIdLen];
        RandomNumberGenerator.Fill(txId);
        return BuildWithTx(type, txId, attrs, miKey);
    }

    private static byte[] BuildWithTx(ushort type, byte[] txId,
        List<(ushort, byte[])> attrs, byte[]? miKey)
    {
        int body = 0;
        foreach (var (at, val) in attrs) body += 4 + ((val.Length + 3) & ~3);
        int total = HeaderLen + body;
        if (miKey != null) total += 4 + 20;

        var m = new byte[total];
        m[0] = (byte)(type >> 8);
        m[1] = (byte)type;
        int len = total - HeaderLen;
        m[2] = (byte)(len >> 8);
        m[3] = (byte)len;
        Buffer.BlockCopy(Magic, 0, m, 4, 4);
        Buffer.BlockCopy(txId, 0, m, 8, TxIdLen);

        int off = HeaderLen;
        foreach (var (at, val) in attrs)
        {
            m[off] = (byte)(at >> 8);
            m[off + 1] = (byte)at;
            m[off + 2] = (byte)(val.Length >> 8);
            m[off + 3] = (byte)val.Length;
            Buffer.BlockCopy(val, 0, m, off + 4, val.Length);
            off += 4 + ((val.Length + 3) & ~3);
        }

        if (miKey != null)
        {
            // HMAC over header+body, where header already covers the MI attr.
            using var hmac = new HMACSHA1(miKey);
            var digest = hmac.ComputeHash(m.AsSpan(0, off).ToArray());
            m[off] = (byte)(AMessageIntegrity >> 8);
            m[off + 1] = (byte)AMessageIntegrity;
            m[off + 2] = 0;
            m[off + 3] = 20;
            Buffer.BlockCopy(digest, 0, m, off + 4, 20);
        }
        return m;
    }

    private static readonly byte[] EmptyTx = new byte[TxIdLen];

    // CHANNEL-NUMBER: [2-byte channel][2-byte RFFU=0].
    private static byte[] ChannelNumberValue(ushort channel)
        => new[] { (byte)(channel >> 8), (byte)channel, (byte)0, (byte)0 };

    private static byte[] LifetimeValue(uint sec)
        => new[] { (byte)(sec >> 24), (byte)(sec >> 16), (byte)(sec >> 8), (byte)sec };

    private static byte[] XorAddressValue(IPEndPoint ep, byte[] txId)
    {
        bool v4 = ep.Address.AddressFamily == AddressFamily.InterNetwork;
        int len = v4 ? 8 : 20;
        var val = new byte[len];
        val[1] = v4 ? (byte)1 : (byte)2;
        int port = ep.Port ^ ((Magic[0] << 8) | Magic[1]);
        val[2] = (byte)(port >> 8);
        val[3] = (byte)port;
        var raw = ep.Address.GetAddressBytes();
        if (v4)
        {
            for (int i = 0; i < 4; i++) val[4 + i] = (byte)(raw[i] ^ Magic[i]);
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                byte key = i < 4 ? Magic[i] : (i - 4 < txId.Length ? txId[i - 4] : (byte)0);
                val[4 + i] = (byte)(raw[i] ^ key);
            }
        }
        return val;
    }

    private IPEndPoint? ParseXorAddress(TurnMsg m, ushort attr)
    {
        if (!m.Alts.TryGetValue(attr, out var val) || val.Length < 8) return null;
        var txId = m.Raw.AsSpan(8, TxIdLen).ToArray();
        byte family = val[1];
        int port = ((val[2] << 8) | val[3]) ^ ((Magic[0] << 8) | Magic[1]);

        if (family == 0x01)
        {
            if (val.Length < 8) return null;
            var raw = new byte[4];
            for (int i = 0; i < 4; i++) raw[i] = (byte)(val[4 + i] ^ Magic[i]);
            return new IPEndPoint(new IPAddress(raw), port);
        }
        if (family == 0x02)
        {
            if (val.Length < 20) return null;
            var raw = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte key = i < 4 ? Magic[i] : (i - 4 < txId.Length ? txId[i - 4] : (byte)0);
                raw[i] = (byte)(val[4 + i] ^ key);
            }
            return new IPEndPoint(new IPAddress(raw), port);
        }
        return null;
    }

    private static string Hex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static void Log(string msg)
    {
        try { InterstellarPlugin.Logger?.LogInfo(msg); }
        catch { /* teardown */ }
    }

    // Chatty per-allocation detail; goes to BepInEx/LogOutput.log on demand.
    private static void Dbg(string msg)
    {
        try { InterstellarPlugin.Logger?.LogDebug(msg); }
        catch { /* teardown */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }
        // Best-effort deallocate — Refresh with LIFETIME=0, no wait.
        try
        {
            if (_ready && _udp != null)
            {
                var msg = BuildAuthenticated(TRefresh,
                    new List<(ushort, byte[])> { (ALifetime, LifetimeValue(0)) });
                _udp.Send(msg, msg.Length, _server);
            }
        }
        catch { /* going away anyway */ }

        try { _maintain?.Dispose(); } catch { }
        _maintain = null;

        UdpClient? udp;
        lock (_lock)
        {
            udp = _udp;
            _udp = null;
            _ready = false;
        }
        try { udp?.Close(); } catch { }
        try { udp?.Dispose(); } catch { }
    }
}