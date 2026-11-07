using Android.Media;
using Android.OS;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Android;

/// <summary>IAudioPlayer backed by Android's MediaPlayer.</summary>
public sealed class AndroidAudioPlayer : IAudioPlayer
{
    private readonly PositionClock _clock = new();
    private MediaPlayer? _player;
    private Timer? _poll;
    private PlaybackState _state = PlaybackState.Stopped;
    private double _durationMs;
    private double _volume = 1.0;
    private bool _disposed;

    public event EventHandler? StateChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler? DurationChanged;

    public bool IsLoaded => _player != null;
    public double PositionMs => _clock.PositionMs;
    public double DurationMs => _durationMs;
    public PlaybackState State => _state;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            try { _player?.SetVolume((float)_volume, (float)_volume); } catch { /* ignore */ }
        }
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AndroidAudioPlayer));
        ReleasePlayer();

        var player = new MediaPlayer();
        player.SetAudioAttributes(new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!);
        var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Prepared += (_, _) => prepared.TrySetResult(true);
        player.Error += (_, e) => prepared.TrySetException(new InvalidOperationException($"MediaPlayer error {e.What}/{e.Extra}"));
        player.Completion += (_, _) =>
        {
            _clock.Update(_durationMs > 0 ? _durationMs : _clock.PositionMs, false);
            SetState(PlaybackState.Stopped);
            MediaEnded?.Invoke(this, EventArgs.Empty);
        };
        player.SetDataSource(filePath);
        player.PrepareAsync();
        using (ct.Register(() => prepared.TrySetCanceled()))
            await prepared.Task;

        _player = player;
        _durationMs = player.Duration;
        player.SetVolume((float)_volume, (float)_volume);
        _clock.Update(0, false);
        SetState(PlaybackState.Stopped);
        DurationChanged?.Invoke(this, EventArgs.Empty);
        _poll = new Timer(_ => Poll(), null, 250, 250);
    }

    private void Poll()
    {
        var p = _player;
        if (p == null || _state != PlaybackState.Playing) return;
        try { _clock.Correct(p.CurrentPosition, true, 100); } catch { /* ignore */ }
    }

    public void Play()
    {
        var p = _player;
        if (p == null) return;
        if (_state == PlaybackState.Stopped && _clock.PositionMs > 0)
            SeekInternal(p, _clock.PositionMs);
        p.Start();
        _clock.SetRunning(true);
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        var p = _player;
        if (p == null || _state != PlaybackState.Playing) return;
        p.Pause();
        _clock.SetRunning(false);
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        var p = _player;
        if (p == null) return;
        if (p.IsPlaying) p.Pause();
        SeekInternal(p, 0);
        _clock.Update(0, false);
        SetState(PlaybackState.Stopped);
    }

    public void Seek(double positionMs)
    {
        var p = _player;
        if (p == null) return;
        positionMs = Math.Max(0, positionMs);
        if (_durationMs > 0) positionMs = Math.Min(positionMs, Math.Max(0, _durationMs - 50));
        SeekInternal(p, positionMs);
        _clock.Update(positionMs, _state == PlaybackState.Playing);
    }

    private static void SeekInternal(MediaPlayer p, double ms)
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) p.SeekTo((long)ms, MediaPlayerSeekMode.Closest);
            else p.SeekTo((int)ms);
        }
        catch { /* ignore */ }
    }

    private void ReleasePlayer()
    {
        _poll?.Dispose();
        _poll = null;
        var p = _player;
        _player = null;
        if (p == null) return;
        try { if (p.IsPlaying) p.Stop(); } catch { /* ignore */ }
        try { p.Reset(); p.Release(); } catch { /* ignore */ }
        p.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleasePlayer();
    }
}
