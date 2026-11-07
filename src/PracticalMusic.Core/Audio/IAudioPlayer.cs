using System.Diagnostics;

namespace PracticalMusic.Core.Audio;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// Platform-neutral audio player. Implemented with LibVLC on desktop and MediaPlayer on Android.
/// All members are expected to be called from the UI thread; events may arrive on any thread.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Loads a local file. Resolves once the duration is known (or best effort).</summary>
    Task LoadAsync(string filePath, CancellationToken ct = default);

    void Play();
    void Pause();
    void Stop();

    /// <summary>Seeks to an absolute position in milliseconds.</summary>
    void Seek(double positionMs);

    /// <summary>Current playback position in milliseconds (interpolated between native updates).</summary>
    double PositionMs { get; }

    double DurationMs { get; }

    PlaybackState State { get; }

    /// <summary>0..1</summary>
    double Volume { get; set; }

    bool IsLoaded { get; }

    event EventHandler? StateChanged;
    event EventHandler? MediaEnded;
    event EventHandler? DurationChanged;
}

/// <summary>Monotonic clock helpers shared by audio and network code.</summary>
public static class MonoClock
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    /// <summary>Milliseconds since an arbitrary fixed point; never goes backwards.</summary>
    public static double NowMs => Stopwatch.GetTimestamp() / TicksPerMs;
}

/// <summary>
/// Smooths coarse position updates (e.g. every 250 ms from LibVLC) into a continuous
/// position by extrapolating with a monotonic clock while playing.
/// </summary>
public sealed class PositionClock
{
    private readonly object _gate = new();
    private double _basePositionMs;
    private double _baseClockMs;
    private bool _running;
    private double _rate = 1.0;

    public double Rate
    {
        get => _rate;
        set { lock (_gate) { Snapshot(); _rate = value; } }
    }

    public bool IsRunning { get { lock (_gate) return _running; } }

    public double PositionMs
    {
        get
        {
            lock (_gate)
            {
                if (!_running) return _basePositionMs;
                return _basePositionMs + (MonoClock.NowMs - _baseClockMs) * _rate;
            }
        }
    }

    /// <summary>Reports a fresh position from the backend.</summary>
    public void Update(double positionMs, bool running)
    {
        lock (_gate)
        {
            _basePositionMs = Math.Max(0, positionMs);
            _baseClockMs = MonoClock.NowMs;
            _running = running;
        }
    }

    /// <summary>Reports a fresh position but ignores it when it is within tolerance of the extrapolated value.</summary>
    public void Correct(double positionMs, bool running, double toleranceMs = 60)
    {
        lock (_gate)
        {
            if (_running == running && Math.Abs(PositionMs - positionMs) <= toleranceMs) return;
            _basePositionMs = Math.Max(0, positionMs);
            _baseClockMs = MonoClock.NowMs;
            _running = running;
        }
    }

    public void SetRunning(bool running)
    {
        lock (_gate)
        {
            Snapshot();
            _running = running;
        }
    }

    private void Snapshot()
    {
        _basePositionMs = PositionMsUnlocked();
        _baseClockMs = MonoClock.NowMs;
    }

    private double PositionMsUnlocked() =>
        _running ? _basePositionMs + (MonoClock.NowMs - _baseClockMs) * _rate : _basePositionMs;
}
