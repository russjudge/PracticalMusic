using LibVLCSharp.Shared;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Desktop;

/// <summary>
/// IAudioPlayer backed by LibVLC. On Windows the libvlc binaries ship with the app
/// (VideoLAN.LibVLC.Windows); on Linux the distribution's vlc package provides them.
/// </summary>
public sealed class LibVlcAudioPlayer : IAudioPlayer
{
    private static LibVLC? _libVlc;
    private readonly MediaPlayer _player;
    private readonly PositionClock _clock = new();
    private Media? _media;
    private PlaybackState _state = PlaybackState.Stopped;
    private double _durationMs;
    private double _pendingSeekMs = -1;
    private double _volume = 1.0;
    private bool _disposed;

    public event EventHandler? StateChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler? DurationChanged;

    public LibVlcAudioPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc ??= new LibVLC("--no-video", "--quiet", "--no-metadata-network-access");
        _player = new MediaPlayer(_libVlc);

        _player.TimeChanged += (_, e) => _clock.Correct(e.Time, _state == PlaybackState.Playing, 80);
        _player.LengthChanged += (_, e) =>
        {
            if (e.Length > 0 && Math.Abs(e.Length - _durationMs) > 1)
            {
                _durationMs = e.Length;
                DurationChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _player.Playing += (_, _) =>
        {
            if (_pendingSeekMs >= 0)
            {
                var seek = _pendingSeekMs;
                _pendingSeekMs = -1;
                // must not call libvlc synchronously from its own callback thread
                ThreadPool.QueueUserWorkItem(_ => { try { _player.Time = (long)seek; } catch { /* ignore */ } });
                _clock.Update(seek, true);
            }
            else
            {
                _clock.SetRunning(true);
            }
            SetState(PlaybackState.Playing);
        };
        _player.Paused += (_, _) =>
        {
            _clock.SetRunning(false);
            SetState(PlaybackState.Paused);
        };
        _player.Stopped += (_, _) =>
        {
            _clock.Update(0, false);
            SetState(PlaybackState.Stopped);
        };
        _player.EndReached += (_, _) =>
        {
            _clock.Update(_durationMs > 0 ? _durationMs : _clock.PositionMs, false);
            SetState(PlaybackState.Stopped);
            MediaEnded?.Invoke(this, EventArgs.Empty);
        };
        _player.EncounteredError += (_, _) =>
        {
            _clock.SetRunning(false);
            SetState(PlaybackState.Stopped);
        };
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsLoaded => _media != null;
    public double PositionMs => _clock.PositionMs;
    public double DurationMs => _durationMs;
    public PlaybackState State => _state;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _player.Volume = (int)Math.Round(_volume * 100);
        }
    }

    public async Task LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LibVlcAudioPlayer));
        await Task.Run(() => { if (_player.IsPlaying || _state != PlaybackState.Stopped) _player.Stop(); }, ct);
        _media?.Dispose();
        _media = new Media(_libVlc!, new Uri(filePath));
        _pendingSeekMs = -1;
        _durationMs = 0;
        try
        {
            await _media.Parse(MediaParseOptions.ParseLocal, timeout: 10000, cancellationToken: ct);
            if (_media.Duration > 0) _durationMs = _media.Duration;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* duration will arrive through LengthChanged */ }
        _player.Media = _media;
        _player.Volume = (int)Math.Round(_volume * 100);
        _clock.Update(0, false);
        SetState(PlaybackState.Stopped);
        DurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        if (_media == null) return;
        if (_state == PlaybackState.Paused)
        {
            _player.SetPause(false);
            _clock.SetRunning(true);
        }
        else
        {
            _player.Play();
        }
    }

    public void Pause()
    {
        if (_media == null || _state != PlaybackState.Playing) return;
        _player.SetPause(true);
        _clock.SetRunning(false);
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        if (_media == null) return;
        _pendingSeekMs = 0;
        Task.Run(() => _player.Stop());
        _clock.Update(0, false);
        SetState(PlaybackState.Stopped);
    }

    public void Seek(double positionMs)
    {
        if (_media == null) return;
        positionMs = Math.Max(0, positionMs);
        if (_durationMs > 0) positionMs = Math.Min(positionMs, Math.Max(0, _durationMs - 50));
        if (_state == PlaybackState.Stopped)
        {
            _pendingSeekMs = positionMs;
            _clock.Update(positionMs, false);
            return;
        }
        _player.Time = (long)positionMs;
        _clock.Update(positionMs, _state == PlaybackState.Playing);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _player.Stop(); } catch { /* ignore */ }
        _player.Dispose();
        _media?.Dispose();
    }
}
