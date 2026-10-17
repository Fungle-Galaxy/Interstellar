using System;

namespace Interstellar.Network;

// Voice-server connect state, published for the HUD chip.
//
// Entering a room starts the 3-attempt connect loop. Each failure parks for
// RetryDelayMs (the chip counts it down) before the next try; the third
// failure is terminal for that room. Written from the connection thread,
// read from the Unity main thread.
public static class VoiceConnStatus
{
    public enum Phase : byte { Idle, Connecting, Connected, Retrying, Failed }

    public const int MaxAttempts = 3;
    public const int RetryDelayMs = 3000;

    private static readonly object _lock = new();
    private static Phase _phase = Phase.Idle;
    private static int _attempt;
    // TickCount64 when the next attempt starts; 0 = not waiting.
    private static long _retryAtTick;

    public static void SetConnecting(int attempt)
    {
        lock (_lock) { _phase = Phase.Connecting; _attempt = attempt; _retryAtTick = 0; }
    }

    public static void SetConnected()
    {
        lock (_lock) { _phase = Phase.Connected; _retryAtTick = 0; }
    }

    public static void SetRetrying(int failedAttempt)
    {
        lock (_lock)
        {
            _phase = Phase.Retrying;
            _attempt = failedAttempt;
            _retryAtTick = Environment.TickCount64 + RetryDelayMs;
        }
    }

    public static void SetFailed(int attempts)
    {
        lock (_lock) { _phase = Phase.Failed; _attempt = attempts; _retryAtTick = 0; }
    }

    public static void GetState(out Phase phase, out int attempt, out int retryInSeconds)
    {
        lock (_lock)
        {
            phase = _phase;
            attempt = _attempt;
            long left = _retryAtTick == 0 ? 0 : _retryAtTick - Environment.TickCount64;
            retryInSeconds = left <= 0 ? 0 : (int)((left + 999) / 1000);
        }
    }
}