#pragma warning disable CS8618, CS8602, CS8603, CS8604
using NAudio.Utils;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Audio.Provider;

/// <summary>
/// A BufferedProvider that accepts sample writes.
/// Modified NAudio's BufferedWaveProvider for sample usage.
/// </summary>
internal class BufferedSampleProvider : ISampleProvider
{
    private CircularFloatBuffer circularBuffer;

    private readonly WaveFormat waveFormat;

    public bool ReadFully { get; set; }
    public int BufferLength { get; set; }

    public int BufferCutSize { get; set; } = int.MaxValue;
    public int BufferCutToSize { get; set; } = int.MaxValue;

    /// <summary>
    /// Jitter cushion. When enabled, playout holds (outputs silence, keeps
    /// buffering) until the buffer reaches HoldTargetSamples, and re-enters
    /// hold after any underrun. Without a cushion the buffer level sits at ~0,
    /// so every late frame turns into an audible gap (choppy speech on mobile
    /// networks). Combined with BufferCutSize/BufferCutToSize this keeps the
    /// level inside a bounded band instead of drifting to 0 or to the cap.
    /// </summary>
    public bool HoldOnUnderrun { get; set; }
    private int _holdTarget;
    private int _holdTargetMin;
    private int _holdTargetMax = int.MaxValue;
    private int _lastUnderrunTick = Environment.TickCount;
    private int _prevUnderrunTick = Environment.TickCount;
    // int.MinValue would overflow when subtracted from TickCount and read as a
    // *negative* elapsed time, silently blocking the first growth step.
    private int _lastGrowTick = int.MinValue / 2;

    /// <summary>Current cushion depth in samples — the level playout waits for
    /// after every underrun. Adaptive: grows when frames keep arriving late,
    /// shrinks back to HoldTargetMinSamples once the link is quiet.</summary>
    public int HoldTargetSamples
    {
        get => _holdTarget;
        set => _holdTarget = value;
    }
    /// <summary>Deepest the cushion is allowed to grow. Cellular links with
    /// bursty RTT need a deeper delay line than the configured floor.</summary>
    public int HoldTargetMinSamples
    {
        get => _holdTargetMin;
        set => _holdTargetMin = value;
    }
    public int HoldTargetMaxSamples
    {
        get => _holdTargetMax;
        set => _holdTargetMax = value;
    }
    public int HoldTargetCurrent => _holdTarget;
    /// <summary>Same value in ms, for the [VC:Diag] line.</summary>
    public int HoldTargetMs => (int)(1000L * _holdTarget / Math.Max(1, waveFormat.SampleRate));

    /// <summary>Live-retune the cushion band (the settings panel's buffer-delay
    /// slider): playout now waits for this many samples, bursts trim back to it,
    /// and the adaptive growth ceiling follows. Safe while reading — the values
    /// are plain ints consumed by the next Read.</summary>
    public void ApplyHoldTarget(int cushion)
    {
        HoldTargetSamples = cushion;
        HoldTargetMinSamples = cushion;
        HoldTargetMaxSamples = cushion * 2;
        BufferCutSize = cushion * 2;
        BufferCutToSize = cushion;
    }

    /// <summary>No underruns for this long → the link recovered, so give the
    /// latency back by stepping the cushion down. Deliberately slow: a faster
    /// decay makes the level sawtooth through a normal conversation.</summary>
    private const int StableHoldMs = 15000;
    /// <summary>Two underruns inside this window count as real jitter. An
    /// isolated underrun is almost always the end of an utterance (the sender
    /// simply stopped), and must not deepen the delay line.</summary>
    private const int GrowClusterMs = 4000;
    /// <summary>Floor on how often jitter may deepen the cushion.</summary>
    private const int GrowMinIntervalMs = 1000;

    /// <summary>If no new data has arrived for this long, stop waiting on the
    /// cushion and play what is left — otherwise an utterance shorter than
    /// HoldTargetSamples would sit in the buffer forever.</summary>
    public int HoldExitIdleMs { get; set; } = 150;
    private volatile bool _hold = true;
    private int _lastWriteTick;

    /// <summary>True while playout is inside the cushion (outputting silence).</summary>
    public bool IsHolding => _hold;
    /// <summary>Number of underruns seen since the last ClearBuffer — used by
    /// the periodic [VC:Diag] log to tell "buffer starved" from "no frames
    /// arriving at all".</summary>
    public int UnderrunCount;
    public int BufferedSamples => circularBuffer?.Count ?? 0;

    public TimeSpan BufferDuration
    {
        get
        {
            return TimeSpan.FromSeconds((double)BufferLength / (double)WaveFormat.AverageBytesPerSecond);
        }
        set
        {
            BufferLength = (int)(value.TotalSeconds * (double)WaveFormat.AverageBytesPerSecond);
        }
    }

    public bool DiscardOnBufferOverflow { get; set; }
    public int BufferedBytes
    {
        get
        {
            if (circularBuffer != null)
            {
                return circularBuffer.Count;
            }

            return 0;
        }
    }
    public TimeSpan BufferedDuration => TimeSpan.FromSeconds((double)BufferedBytes / (double)WaveFormat.AverageBytesPerSecond);
    public WaveFormat WaveFormat => waveFormat;
    public BufferedSampleProvider(WaveFormat waveFormat, int? bufferLength = null)
    {
        this.waveFormat = waveFormat;
        BufferLength = bufferLength ?? waveFormat.AverageBytesPerSecond * 5;
        ReadFully = true;
    }
    public void AddSamples(float[] buffer, int offset, int count)
    {
        _lastWriteTick = Environment.TickCount;
        if (circularBuffer == null)
        {
            circularBuffer = new CircularFloatBuffer(BufferLength);
        }
        if (circularBuffer.Write(buffer, offset, count) < count && !DiscardOnBufferOverflow)
        {
            throw new InvalidOperationException("Buffer full");
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int now = Environment.TickCount;
        AdaptiveHold(now);

        if (HoldOnUnderrun && _hold)
        {
            int need = _holdTarget;
            if (need < count) need = count;
            int have = circularBuffer?.Count ?? 0;
            bool stalled = have > 0 &&
                unchecked(now - _lastWriteTick) > HoldExitIdleMs;
            if (have < need && !stalled)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            _hold = false;
        }

        // Trim only while playing, never during prebuffer — shedding inside the
        // hold threw away the very frames the cushion was waiting for.
        var cb = circularBuffer;
        if (cb != null && BufferCutSize < int.MaxValue && cb.Count > BufferCutSize)
        {
            // Gradual shed, not a one-shot cut. The old hard cut discarded the
            // oldest (Count - BufferCutToSize) samples in a single write — on a
            // bursty uplink that threw away 100ms+ of live speech at once, which
            // is exactly "missing syllables". Shedding at most one read per read
            // brings latency back down in audible-bite-sized steps instead.
            int excess = cb.Count - BufferCutToSize;
            int shed = excess > count ? count : excess;
            if (shed > 0) cb.Discard(shed);
        }

        int num = 0;
        if (circularBuffer != null)
        {
            num = circularBuffer.Read(buffer, offset, count);
        }

        if (num < count)
        {
            // Underrun: pause playout until the resume level is buffered, so a
            // late frame costs one bounded gap instead of permanent chop.
            if (HoldOnUnderrun) { _hold = true; UnderrunCount++; GrowHold(now); }

            if (ReadFully)
            {
                SampleFill.Clear(buffer, offset + num, count - num);
                num = count;
            }
        }

        return num;
    }

    /// <summary>Deepen the cushion — but only when underruns repeat. A single
    /// gap at the end of a sentence is the sender going quiet, not jitter, and
    /// treating it as jitter used to ratchet the delay line up through every
    /// normal conversation pause.</summary>
    private void GrowHold(int now)
    {
        bool jitter = unchecked(now - _prevUnderrunTick) <= GrowClusterMs;
        _prevUnderrunTick = now;
        if (!jitter) return;

        _lastUnderrunTick = now;
        if (_holdTarget <= 0 || _holdTarget >= _holdTargetMax) return;
        if (unchecked(now - _lastGrowTick) < GrowMinIntervalMs) return;
        _lastGrowTick = now;
        _holdTarget = Math.Min(_holdTargetMax, _holdTarget + StepSamples() * 2);
        SyncCutSizes();
    }

    /// <summary>Give the latency back once the link has been quiet for a while.</summary>
    private void AdaptiveHold(int now)
    {
        if (!HoldOnUnderrun || _holdTarget <= _holdTargetMin) return;
        if (unchecked(now - _lastUnderrunTick) < StableHoldMs) return;
        _holdTarget = Math.Max(_holdTargetMin, _holdTarget - StepSamples());
        SyncCutSizes();
        _lastUnderrunTick = now;
    }

    private int StepSamples()
    {
        int sr = Math.Max(1, waveFormat.SampleRate);
        return Math.Max(240, sr / 50); // 20ms
    }

    /// <summary>Keep the overflow shed band tracking the adaptive cushion, so a
    /// deepened target is not immediately trimmed back by the old static value.</summary>
    private void SyncCutSizes()
    {
        if (BufferCutSize < int.MaxValue) BufferCutSize = _holdTarget * 2;
        if (BufferCutToSize < int.MaxValue) BufferCutToSize = _holdTarget;
    }

    public void ClearBuffer()
    {
        if (circularBuffer != null)
        {
            circularBuffer.Reset();
        }
        _hold = true;
        UnderrunCount = 0;
        _lastUnderrunTick = Environment.TickCount;
    }
}
