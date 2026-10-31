using Concentus;
using Interstellar.Audio.Provider;
using Interstellar.Routing.Node;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Routing;

public class AudioRoutingInstance : IHasAudioPropertyNode
{
    private List<AudioBuffer> buffers = [];
    private AudioRoutingInstanceNode[] nodes;
    private BufferedSampleProvider sourceProvider;
    AudioRoutingInstanceNode IHasAudioPropertyNode.GetProperty(int propertyId) => nodes[propertyId];
    public int ClientId { get; private init; }
    
    internal AudioRoutingInstance(List<AudioBuffer> buffers, AudioRoutingInstanceNode[] nodes, BufferedSampleProvider sourceProvider, int clientId)
    {
        this.ClientId = clientId;
        this.buffers = buffers;
        this.nodes = nodes;
        this.sourceProvider = sourceProvider;
    }

    public void AddSamples(float[] samples, int offset, int count)
    {
        sourceProvider.AddSamples(samples, offset, count);
        LastReceiptTime = System.DateTime.Now.Ticks;
        System.Threading.Interlocked.Increment(ref _rxFrames);
    }

    /// <summary>Live-retune this peer's jitter cushion (settings panel buffer delay).</summary>
    internal void ApplyBuffer(int cushion) => sourceProvider.ApplyHoldTarget(cushion);

    private long LastReceiptTime = System.DateTime.Now.Ticks;
    public int ElapsedSinceLastReceipt => (int)((System.DateTime.Now.Ticks - LastReceiptTime) / 10000); // milliseconds

    // --- diagnostics (read by the periodic [VC:Diag] log in VoiceRoom) ---
    private int _rxFrames;
    public int RxFrames => System.Threading.Interlocked.CompareExchange(ref _rxFrames, 0, 0);
    public int BufferedSamples => sourceProvider.BufferedSamples;
    public bool Holding => sourceProvider.IsHolding;
    public int Underruns => sourceProvider.UnderrunCount;
    /// <summary>Adaptive cushion depth in ms (grows on jitter, shrinks when calm).</summary>
    public int HoldTargetMs => sourceProvider.HoldTargetMs;
    public int IdleMs => ElapsedSinceLastReceipt;
}
