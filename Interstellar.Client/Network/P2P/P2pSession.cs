using System;
using System.Collections.Generic;
using System.Net;

namespace Interstellar.Network.P2P;

internal sealed class P2pSession : IDisposable
{
    public readonly string Sid;
    public readonly int ClientId;
    public readonly P2pCrypto Crypto = new();
    public volatile bool Disposed;

    // Peer identity, learned from their "hi".
    public bool HasPeerHello;
    public byte[]? PeerDirId;
    public string? PeerDirHex;
    public byte[]? PeerPublicKey;
    public int Generation;

    // Remote candidates to fire connectivity checks at.
    public readonly List<IPEndPoint> Targets = new();
    private readonly HashSet<string> _targetKeys = new(StringComparer.Ordinal);

    // The peer's latest advertised list, kept raw so targets can be rebuilt
    // from it instead of accumulating stale addresses across a network change.
    public string[]? PeerCandidates;

    // Addresses we saw an authenticated probe arrive from.
    public readonly List<IPEndPoint> Learned = new();

    // Path state.
    public bool Connected;
    public IPEndPoint? Remote;
    // Peer's TURN relayed address, announced over the "turn" signal. Stored
    // outside PeerCandidates (it is out-of-band) and re-added on every rebuild.
    public IPEndPoint? PeerRelayed;
    // True when our traffic for this session rides our TURN allocation.
    public bool ViaRelay;
    // True once we have enqueued a "turn" announce for this peer. The other
    // side answers every announce with its own, so without this guard the two
    // sides ping-pong forever.
    public bool TurnAnnounced;

    // Punch scheduling — all absolute Environment.TickCount64 stamps.
    public long PunchRoundEndsAt;
    public long NextProbeAt;
    public int PunchRound;

    // Liveness.
    public long LastInbound;
    public long LastOutbound;
    public long LastHelloAt;
    public int HelloCount;

    public long CreatedAt = Environment.TickCount64;
    // First frame on each direction is logged once, so "did P2P audio flow"
    // settles from a log instead of from perception.
    public int MediaFrames;
    public int MediaTx;
    // Probes attributed to this session and probes we sent: the rx/tx
    // counters in the punch log distinguish "packets never arrived" from
    // "app-level drop".
    public int StunRx;
    public int Probes;

    public P2pSession(string sid, int clientId)
    {
        Sid = sid;
        ClientId = clientId;
    }

    public bool AddTarget(IPEndPoint ep)
    {
        var key = ep.ToString();
        if (!_targetKeys.Add(key)) return false;
        Targets.Add(ep);
        return true;
    }

    public void ClearTargets()
    {
        Targets.Clear();
        _targetKeys.Clear();
    }

    // Peer restarted under a new ephemeral key: forget everything about them
    // but keep our own key material (re-derived on the next HELLO).
    public void ResetPeerIdentity()
    {
        HasPeerHello = false;
        PeerDirId = null;
        PeerDirHex = null;
        PeerPublicKey = null;
        PeerCandidates = null;
        Learned.Clear();
        ClearTargets();
        Connected = false;
        Remote = null;
        PeerRelayed = null; // they will re-announce
        ViaRelay = false;
        // Announce/punch state belongs to the old identity. Leaving
        // TurnAnnounced set deadlocks reconnects: the once-per-peer guard
        // stays armed and the fresh announce is never answered.
        TurnAnnounced = false;
        PunchRoundEndsAt = 0;
        PunchRound = 0;
    }

    // Rebuild the target list from the peer's latest advertisement plus
    // anything we learned from inbound probes. Replacing (rather than
    // appending) keeps addresses from a left network out of the way.
    public void RebuildTargets(Func<string, IPEndPoint?> parse, int maxTargets)
    {
        ClearTargets();
        if (PeerCandidates != null)
        {
            foreach (var raw in PeerCandidates)
            {
                if (Targets.Count >= maxTargets) break;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var ep = parse(raw);
                if (ep != null) AddTarget(ep);
            }
        }
        foreach (var ep in Learned)
        {
            if (Targets.Count >= maxTargets) break;
            AddTarget(ep);
        }
        // Relayed address is announced out-of-band, so re-add it after every
        // rebuild — last, so direct candidates keep their natural priority.
        if (PeerRelayed != null && Targets.Count < maxTargets)
            AddTarget(PeerRelayed);
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try { Crypto.Dispose(); } catch { }
    }
}