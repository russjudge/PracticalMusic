using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Controls;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Packaging;
using PracticalMusic.Core.Timing;

namespace PracticalMusic.App.ViewModels;

/// <summary>
/// Performance mode: shows the current song's pages (one in portrait, two in landscape), turns pages
/// with the recording, highlights the segment being played and keeps other devices in sync.
/// </summary>
public sealed partial class PlayerViewModel : ViewModelBase, IActivatable
{
    private const double DriftToleranceMs = 180;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private bool _active;
    private bool _loaded;
    private bool _dragging;
    private int _renderedSpread = -1;
    private string? _renderedRecordingKey;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _scheduledStart;
    private bool _applyingRemote;
    private int _ticks;

    public BookSession Session => AppServices.Session;
    public IAudioPlayer Audio => AppServices.Audio;
    public SyncService Sync => AppServices.Sync;

    public ObservableCollection<Song> Songs { get; } = new();
    public ObservableCollection<Recording> Recordings { get; } = new();
    public ObservableCollection<TimeMarker> LeftMarkers { get; } = new();
    public ObservableCollection<TimeMarker> RightMarkers { get; } = new();

    [ObservableProperty] private Song? _currentSong;
    [ObservableProperty] private Recording? _currentRecording;
    [ObservableProperty] private bool _isLandscape;
    [ObservableProperty] private Bitmap? _leftPage;
    [ObservableProperty] private Bitmap? _rightPage;
    [ObservableProperty] private int _leftPageOffset = -1;
    [ObservableProperty] private int _rightPageOffset = -1;
    [ObservableProperty] private HighlightRegion? _leftHighlight;
    [ObservableProperty] private HighlightRegion? _rightHighlight;
    [ObservableProperty] private double _positionMs;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _currentPageOffset;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _showMarkers;
    [ObservableProperty] private double _viewportWidth = 800;
    [ObservableProperty] private double _viewportHeight = 1000;
    [ObservableProperty] private double _renderScale = 1;
    [ObservableProperty] private string _syncText = "";

    public bool HasSong => CurrentSong != null;
    public bool HasRecording => CurrentRecording != null;
    public bool TwoPages => IsLandscape && CurrentSong != null && CurrentSong.PageCount > 1;
    public bool HasRightPage => RightPageOffset >= 0;
    public string SongTitle => CurrentSong?.Title ?? "No song selected";
    public string PageText => CurrentSong == null ? "" : TwoPages && RightPageOffset >= 0
        ? $"Pages {LeftPageOffset + 1}–{RightPageOffset + 1} of {CurrentSong.PageCount}"
        : $"Page {LeftPageOffset + 1} of {CurrentSong.PageCount}";
    public string PositionText => TimingEngine.FormatTime(PositionMs);
    public string DurationText => TimingEngine.FormatTime(DurationMs);
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";
    public string LeftLabel => CurrentSong == null ? "Select a song in the Library" : $"p.{LeftPageOffset + 1}";
    public string RightLabel => $"p.{RightPageOffset + 1}";
    public bool IsLeading => Sync.Role == SyncRole.Leader;
    public bool IsFollowing => Sync.Role == SyncRole.Follower;
    public int SongIndex => CurrentSong == null ? -1 : Songs.IndexOf(CurrentSong);
    public bool CanPrevSong => SongIndex > 0;
    public bool CanNextSong => SongIndex >= 0 && SongIndex < Songs.Count - 1;
    public bool HasMultipleRecordings => Recordings.Count > 1;

    public double SliderPosition
    {
        get => PositionMs;
        set
        {
            if (_dragging || Math.Abs(value - PositionMs) > 250) SeekTo(value, publish: true);
        }
    }

    public PlayerViewModel()
    {
        _timer.Tick += (_, _) => Tick();
        _heartbeat.Tick += (_, _) => PublishState(scheduled: false);
        Session.BookReplaced += () => { RebuildSongs(); SelectSong(Songs.FirstOrDefault(), null); };
        Session.BookChanged += RebuildSongs;
        Sync.StateReceived += OnSyncState;
        Sync.Changed += () => Dispatcher.UIThread.Post(UpdateSyncText);
        RebuildSongs();
    }

    public void Activate()
    {
        _active = true;
        Audio.StateChanged += OnAudioStateChanged;
        Audio.MediaEnded += OnMediaEnded;
        Audio.DurationChanged += OnDurationChanged;
        _timer.Start();
        RebuildSongs();
        if (CurrentSong == null) SelectSong(Songs.FirstOrDefault(), null);
        else _ = LoadRecordingAsync(CurrentRecording);
        _renderedSpread = -1;
        _ = RenderSpreadAsync();
        UpdateSyncText();
    }

    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        _heartbeat.Stop();
        Audio.StateChanged -= OnAudioStateChanged;
        Audio.MediaEnded -= OnMediaEnded;
        Audio.DurationChanged -= OnDurationChanged;
        _scheduledStart?.Cancel();
        try { if (Audio.State == PlaybackState.Playing) Audio.Pause(); } catch { /* ignore */ }
        AppServices.Platform.KeepScreenOn(false);
    }

    // ---------------------------------------------------------------- song / recording selection

    private void RebuildSongs()
    {
        var keep = CurrentSong?.Id;
        Songs.Clear();
        foreach (var s in Session.Book.SongsInPageOrder) Songs.Add(s);
        var found = Songs.FirstOrDefault(s => s.Id == keep);
        if (found == null && CurrentSong != null) CurrentSong = null;
        else if (found != null && !ReferenceEquals(found, CurrentSong)) CurrentSong = found;
        OnPropertyChanged(nameof(CanPrevSong));
        OnPropertyChanged(nameof(CanNextSong));
    }

    public void SelectSong(Song? song, Recording? recording)
    {
        var sameSong = ReferenceEquals(song, CurrentSong);
        CurrentSong = song;
        Recordings.Clear();
        if (song != null) foreach (var r in song.Recordings) Recordings.Add(r);
        OnPropertyChanged(nameof(HasMultipleRecordings));
        var rec = recording != null && song != null && song.Recordings.Contains(recording) ? recording : song?.Recordings.FirstOrDefault();
        if (!sameSong)
        {
            _scheduledStart?.Cancel();
            try { Audio.Pause(); } catch { /* ignore */ }
            CurrentPageOffset = 0;
            PositionMs = 0;
        }
        if (!ReferenceEquals(rec, CurrentRecording)) CurrentRecording = rec; // triggers the load
        else if (_active && !_loaded) _ = LoadRecordingAsync(rec);
        _renderedSpread = -1;
        OnPropertyChanged(nameof(HasSong));
        OnPropertyChanged(nameof(SongTitle));
        OnPropertyChanged(nameof(TwoPages));
        OnPropertyChanged(nameof(SongIndex));
        OnPropertyChanged(nameof(CanPrevSong));
        OnPropertyChanged(nameof(CanNextSong));
        if (_active) _ = RenderSpreadAsync();
    }

    partial void OnCurrentRecordingChanged(Recording? value)
    {
        OnPropertyChanged(nameof(HasRecording));
        if (_active) _ = LoadRecordingAsync(value);
    }

    partial void OnIsLandscapeChanged(bool value)
    {
        OnPropertyChanged(nameof(TwoPages));
        _renderedSpread = -1;
        _ = RenderSpreadAsync();
    }

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private async Task LoadRecordingAsync(Recording? rec)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _loaded = false;
        var wasPlaying = Audio.State == PlaybackState.Playing;
        try { Audio.Pause(); } catch { /* ignore */ }
        IsPlaying = false;
        if (rec == null)
        {
            DurationMs = 0;
            StatusText = CurrentSong == null ? "" : "This song has no recording yet.";
            return;
        }
        // loads are serialized: a newer request cancels the older one, which exits at its next check
        await _loadGate.WaitAsync();
        try
        {
            if (cts.IsCancellationRequested) return;
            StatusText = "Loading " + rec.Name + "…";
            if (!await MediaResolver.EnsureResolvedAsync(rec.Source, AppServices.Platform.CacheDir, null, cts.Token))
            {
                StatusText = "Audio file not found: " + rec.Source.DisplayName;
                return;
            }
            if (cts.IsCancellationRequested) return;
            var path = rec.Source.ResolvedPath!;
            if (!(Audio.IsLoaded && string.Equals(AppServices.LoadedAudioPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                await Audio.LoadAsync(path, cts.Token);
                AppServices.LoadedAudioPath = path;
            }
            if (cts.IsCancellationRequested) return;
            _loaded = true;
            DurationMs = Audio.DurationMs > 0 ? Audio.DurationMs : rec.DurationMs;
            if (Audio.DurationMs > 0) rec.DurationMs = Audio.DurationMs;
            StatusText = "";
            // show the page that matches the current audio position (0 for a freshly loaded file)
            CurrentPageOffset = Math.Clamp(TimingEngine.PageOffsetAt(rec, Audio.PositionMs), 0, Math.Max(0, (CurrentSong?.PageCount ?? 1) - 1));
            _renderedSpread = -1;
            _ = RenderSpreadAsync();
            if (wasPlaying && ReferenceEquals(rec, CurrentRecording)) Audio.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Could not load audio: " + ex.Message;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    [RelayCommand]
    private void NextSong()
    {
        var i = SongIndex;
        if (i >= 0 && i < Songs.Count - 1) { SelectSong(Songs[i + 1], null); PublishState(scheduled: false); }
    }

    [RelayCommand]
    private void PrevSong()
    {
        var i = SongIndex;
        if (i > 0) { SelectSong(Songs[i - 1], null); PublishState(scheduled: false); }
    }

    [RelayCommand]
    private void SelectRecording(Recording? rec)
    {
        if (rec == null || CurrentSong == null) return;
        SelectSong(CurrentSong, rec);
        PublishState(scheduled: false);
    }

    // ---------------------------------------------------------------- transport

    [RelayCommand]
    private void PlayPause()
    {
        if (!_loaded) return;
        if (Audio.State == PlaybackState.Playing)
        {
            _scheduledStart?.Cancel();
            Audio.Pause();
            PublishState(scheduled: false);
        }
        else
        {
            if (IsLeading)
            {
                // scheduled start so every follower begins at the same instant
                var at = MonoClock.NowMs + SyncService.StartLeadMs;
                PublishState(scheduled: true, atLeaderTime: at, playing: true);
                ScheduleLocalPlay(at - MonoClock.NowMs);
            }
            else
            {
                Audio.Play();
            }
        }
    }

    private void ScheduleLocalPlay(double delayMs)
    {
        _scheduledStart?.Cancel();
        var cts = _scheduledStart = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, delayMs)), cts.Token); }
            catch (OperationCanceledException) { return; }
            await Dispatcher.UIThread.InvokeAsync(() => { if (!cts.IsCancellationRequested && _loaded) Audio.Play(); });
        });
    }

    [RelayCommand]
    private void Stop()
    {
        if (!_loaded) return;
        _scheduledStart?.Cancel();
        Audio.Pause();
        Audio.Seek(0);
        PositionMs = 0;
        Tick();
        PublishState(scheduled: false);
    }

    [RelayCommand]
    private void Restart() => SeekTo(0, publish: true);

    [RelayCommand]
    private void Nudge(string ms)
    {
        if (double.TryParse(ms, out var delta)) SeekTo(Audio.PositionMs + delta, publish: true);
    }

    public void SeekTo(double ms, bool publish)
    {
        if (!_loaded) return;
        ms = Math.Clamp(ms, 0, DurationMs > 0 ? DurationMs : Math.Max(0, ms));
        Audio.Seek(ms);
        PositionMs = ms;
        Tick();
        if (publish) PublishState(scheduled: false);
    }

    public void BeginSliderDrag() => _dragging = true;

    public void EndSliderDrag(double value)
    {
        _dragging = false;
        SeekTo(value, publish: true);
    }

    /// <summary>Tap on a page: jump to the matching time (markers on that page or the page start).</summary>
    [RelayCommand]
    private void PageTap(PageTap? tap)
    {
        if (tap == null || CurrentSong == null || CurrentRecording == null || !_loaded) return;
        if (!AppServices.Settings.TapToSeekInPlayer) { PlayPause(); return; }
        var offset = Equals(tap.Tag, "R") ? RightPageOffset : LeftPageOffset;
        if (offset < 0) return;
        var t = TimingEngine.TimeForTap(CurrentSong, CurrentRecording, offset, tap.X, tap.Y);
        SeekTo(t, publish: true);
    }

    /// <summary>Manual page turn while paused (or to peek ahead).</summary>
    [RelayCommand]
    private void TurnPage(string direction)
    {
        if (CurrentSong == null) return;
        var step = TwoPages ? 2 : 1;
        var target = Math.Clamp(CurrentPageOffset + (direction == "next" ? step : -step), 0, CurrentSong.PageCount - 1);
        if (CurrentRecording != null && _loaded && TimingEngine.HasPageTiming(CurrentRecording, target))
            SeekTo(TimingEngine.PageStartTime(CurrentRecording, target), publish: true);
        else
        {
            CurrentPageOffset = target;
            _ = RenderSpreadAsync();
        }
    }

    // ---------------------------------------------------------------- timer / rendering

    private void OnAudioStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        IsPlaying = Audio.State == PlaybackState.Playing;
        OnPropertyChanged(nameof(PlayPauseGlyph));
        AppServices.Platform.KeepScreenOn(IsPlaying);
        if (IsLeading) { if (IsPlaying) _heartbeat.Start(); else _heartbeat.Stop(); }
    });

    private void OnDurationChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (Audio.DurationMs > 0)
        {
            DurationMs = Audio.DurationMs;
            if (CurrentRecording != null) CurrentRecording.DurationMs = DurationMs;
            OnPropertyChanged(nameof(DurationText));
        }
    });

    private void OnMediaEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        IsPlaying = false;
        OnPropertyChanged(nameof(PlayPauseGlyph));
        _heartbeat.Stop();
        PublishState(scheduled: false); // followers learn that the leader stopped at the end
    });

    private void Tick()
    {
        if (!_active) return;
        if (_loaded) PositionMs = Audio.PositionMs;
        if (++_ticks % 25 == 0 && IsFollowing && !Sync.ClockSynced) UpdateSyncText(); // about once a second
        OnPropertyChanged(nameof(PositionText));
        if (!_dragging) OnPropertyChanged(nameof(SliderPosition));
        if (CurrentSong == null || CurrentRecording == null) return;

        if (_loaded && IsPlaying)
        {
            var p = Math.Clamp(TimingEngine.PageOffsetAt(CurrentRecording, PositionMs), 0, CurrentSong.PageCount - 1);
            if (p != CurrentPageOffset) CurrentPageOffset = p;
        }

        var spread = TwoPages ? TimingEngine.SpreadIndexForPage(CurrentPageOffset) : CurrentPageOffset;
        var key = CurrentSong.Id + "/" + (TwoPages ? "2" : "1");
        if (spread != _renderedSpread || key != _renderedRecordingKey) _ = RenderSpreadAsync();

        var hl = TimingEngine.HighlightAt(CurrentSong, CurrentRecording, PositionMs);
        LeftHighlight = hl != null && hl.Value.PageOffset == LeftPageOffset ? hl : null;
        RightHighlight = hl != null && hl.Value.PageOffset == RightPageOffset ? hl : null;
    }

    partial void OnCurrentPageOffsetChanged(int value) => OnPropertyChanged(nameof(PageText));

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseGlyph));

    public void SetViewport(double width, double height, double scale)
    {
        if (width <= 0 || height <= 0) return;
        var changed = Math.Abs(width - ViewportWidth) > 32 || Math.Abs(height - ViewportHeight) > 32 || Math.Abs(scale - RenderScale) > 0.01;
        ViewportWidth = width;
        ViewportHeight = height;
        RenderScale = scale;
        IsLandscape = width > height;
        if (changed) { _renderedSpread = -1; _ = RenderSpreadAsync(); }
    }

    private async Task RenderSpreadAsync()
    {
        var song = CurrentSong;
        if (song == null)
        {
            LeftPage = null; RightPage = null; LeftPageOffset = -1; RightPageOffset = -1;
            OnPropertyChanged(nameof(HasRightPage));
            OnPropertyChanged(nameof(LeftLabel));
            return;
        }
        var two = TwoPages;
        var pages = TimingEngine.VisiblePages(CurrentPageOffset, song.PageCount, two);
        var spread = two ? TimingEngine.SpreadIndexForPage(CurrentPageOffset) : CurrentPageOffset;
        _renderedSpread = spread;
        _renderedRecordingKey = song.Id + "/" + (two ? "2" : "1");

        var maxW = (int)(ViewportWidth * RenderScale / (two ? 2 : 1));
        var maxH = (int)(ViewportHeight * RenderScale);
        var book = Session.Book;
        try
        {
            var left = await AppServices.Pages.RenderAsync(book, song.StartPage + pages[0], maxW, maxH);
            Bitmap? right = null;
            if (pages.Length > 1) right = await AppServices.Pages.RenderAsync(book, song.StartPage + pages[1], maxW, maxH);
            if (_renderedSpread != spread || !ReferenceEquals(song, CurrentSong)) return;
            LeftPage = left;
            RightPage = right;
            LeftPageOffset = pages[0];
            RightPageOffset = pages.Length > 1 ? pages[1] : -1;
            RefreshMarkers();
            OnPropertyChanged(nameof(HasRightPage));
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(LeftLabel));
            OnPropertyChanged(nameof(RightLabel));

            // pre-render the following spread so page turns are instant
            var nextFirst = two ? (spread + 1) * 2 : CurrentPageOffset + 1;
            for (var i = 0; i < (two ? 2 : 1); i++)
            {
                var p = nextFirst + i;
                if (p < song.PageCount) _ = AppServices.Pages.RenderAsync(book, song.StartPage + p, maxW, maxH);
            }
        }
        catch { /* keep what we have */ }
    }

    private void RefreshMarkers()
    {
        LeftMarkers.Clear();
        RightMarkers.Clear();
        if (CurrentRecording == null) return;
        foreach (var m in CurrentRecording.Markers)
        {
            if (m.PageOffset == LeftPageOffset) LeftMarkers.Add(m);
            else if (m.PageOffset == RightPageOffset) RightMarkers.Add(m);
        }
    }

    // ---------------------------------------------------------------- sync

    private void UpdateSyncText()
    {
        OnPropertyChanged(nameof(IsLeading));
        OnPropertyChanged(nameof(IsFollowing));
        SyncText = Sync.Role switch
        {
            SyncRole.Leader => "Leading sync",
            SyncRole.Follower => Sync.ClockSynced
                ? $"Following {Sync.Leader?.Name ?? "leader"} (±{Sync.RttMs / 2:0} ms)"
                : (DateTime.UtcNow - Sync.FollowingSince).TotalSeconds > 6
                    ? $"Following {Sync.Leader?.Name ?? "leader"} – no clock reply yet; both firewalls must allow inbound UDP {LanBus.DiscoveryPort}"
                    : $"Following {Sync.Leader?.Name ?? "leader"} (syncing clock…)",
            _ => ""
        };
        if (IsLeading && IsPlaying) _heartbeat.Start(); else _heartbeat.Stop();
    }

    private void PublishState(bool scheduled, double atLeaderTime = 0, bool? playing = null)
    {
        if (!IsLeading || _applyingRemote) return;
        var state = new SyncState
        {
            BookId = Session.Book.Id,
            BookTitle = Session.Book.Title,
            SongId = CurrentSong?.Id,
            SongTitle = CurrentSong?.Title,
            SongIndex = SongIndex,
            RecordingId = CurrentRecording?.Id,
            RecordingName = CurrentRecording?.Name,
            IsPlaying = playing ?? Audio.State == PlaybackState.Playing,
            PositionMs = _loaded ? Audio.PositionMs : 0,
            AtLeaderTimeMs = scheduled ? atLeaderTime : MonoClock.NowMs
        };
        _ = Sync.PublishAsync(state);
    }

    private void OnSyncState(SyncState state, double localAtMs) => Dispatcher.UIThread.Post(() =>
    {
        if (Sync.Role != SyncRole.Follower) return;
        var main = AppServices.Main;
        if (main != null && !ReferenceEquals(main.CurrentView, this)) main.NavigateTo(this);
        _ = ApplyRemoteStateAsync(state, localAtMs);
    });

    private async Task ApplyRemoteStateAsync(SyncState state, double localAtMs)
    {
        _applyingRemote = true;
        try
        {
            // find the song: by id, then by title, then by index
            var song = Songs.FirstOrDefault(s => s.Id == state.SongId)
                       ?? Songs.FirstOrDefault(s => string.Equals(s.Title, state.SongTitle, StringComparison.OrdinalIgnoreCase))
                       ?? (state.SongIndex >= 0 && state.SongIndex < Songs.Count ? Songs[state.SongIndex] : null);
            if (song == null)
            {
                StatusText = $"Leader is playing \"{state.SongTitle}\" which is not in this book.";
                return;
            }
            var rec = song.Recordings.FirstOrDefault(r => r.Id == state.RecordingId)
                      ?? song.Recordings.FirstOrDefault(r => string.Equals(r.Name, state.RecordingName, StringComparison.OrdinalIgnoreCase))
                      ?? song.Recordings.FirstOrDefault();

            if (!ReferenceEquals(song, CurrentSong) || !ReferenceEquals(rec, CurrentRecording) || !_loaded)
            {
                SelectSong(song, rec);
                // wait for the audio to load (bounded)
                var waited = 0;
                while (!_loaded && waited < 8000 && ReferenceEquals(CurrentRecording, rec))
                {
                    await Task.Delay(50);
                    waited += 50;
                }
                if (!_loaded) return;
            }

            _scheduledStart?.Cancel();
            // SyncMath bounds the result: an implausible time difference (clocks not comparable) can
            // never turn into a seek to the end of the song.
            var plan = SyncMath.Plan(state, localAtMs, MonoClock.NowMs, DurationMs);
            switch (plan.Action)
            {
                case SyncAction.Pause:
                    if (Audio.State == PlaybackState.Playing) Audio.Pause();
                    if (Math.Abs(Audio.PositionMs - plan.PositionMs) > DriftToleranceMs) SeekTo(plan.PositionMs, publish: false);
                    break;
                case SyncAction.StartAt:
                    // scheduled start: cue the position now, start when the leader's instant arrives
                    if (Audio.State == PlaybackState.Playing) Audio.Pause();
                    SeekTo(plan.PositionMs, publish: false);
                    ScheduleLocalPlay(plan.DelayMs);
                    break;
                case SyncAction.PlayFrom:
                    if (Audio.State != PlaybackState.Playing)
                    {
                        SeekTo(plan.PositionMs + 30, publish: false); // small allowance for the play call latency
                        Audio.Play();
                    }
                    else if (Math.Abs(Audio.PositionMs - plan.PositionMs) > DriftToleranceMs)
                    {
                        SeekTo(plan.PositionMs + 30, publish: false);
                    }
                    break;
            }
        }
        finally
        {
            _applyingRemote = false;
        }
    }
}
