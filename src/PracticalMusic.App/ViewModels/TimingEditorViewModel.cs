using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Controls;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Packaging;
using PracticalMusic.Core.Timing;

namespace PracticalMusic.App.ViewModels;

public sealed partial class PageTimingItem : ViewModelBase
{
    public int PageOffset { get; init; }
    public int PageNumber => PageOffset + 1;
    [ObservableProperty] private double _timeMs;
    [ObservableProperty] private bool _hasTiming;
    [ObservableProperty] private bool _isCurrent;
    public string TimeText => PageOffset == 0 ? "0:00.0 (start)" : HasTiming ? TimingEngine.FormatTime(TimeMs) : "not set";
    partial void OnTimeMsChanged(double value) => OnPropertyChanged(nameof(TimeText));
    partial void OnHasTimingChanged(bool value) => OnPropertyChanged(nameof(TimeText));
}

public sealed partial class MarkerItem : ViewModelBase
{
    public TimeMarker Marker { get; }
    public MarkerItem(TimeMarker marker) => Marker = marker;
    public string TimeText => TimingEngine.FormatTime(Marker.TimeMs);
    public string PageText => $"p.{Marker.PageOffset + 1}";
    public string PositionText => $"({Marker.X:0.00}, {Marker.Y:0.00})";
    [ObservableProperty] private bool _isCurrent;
    public void Refresh()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PositionText));
    }
}

/// <summary>
/// Links a recording to its pages: play the audio, press "next page" when the music reaches a new
/// page and tap on the page where the music is to drop time markers. Songs with more than one page
/// show two pages side by side (when the view is wide enough), like the player does.
/// </summary>
public sealed partial class TimingEditorViewModel : ViewModelBase, IActivatable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _renderedSpread = -1;
    private bool _renderedTwo;
    private bool _active;
    private bool _loaded;
    private bool _dragging;

    public Song Song { get; }
    public Recording Recording { get; }
    public IAudioPlayer Audio => AppServices.Audio;

    public ObservableCollection<PageTimingItem> PageTimings { get; } = new();
    public ObservableCollection<MarkerItem> Markers { get; } = new();
    public ObservableCollection<TimeMarker> LeftMarkers { get; } = new();
    public ObservableCollection<TimeMarker> RightMarkers { get; } = new();

    /// <summary>The page the music is on (0-based offset inside the song).</summary>
    [ObservableProperty] private int _pageOffset;
    [ObservableProperty] private Bitmap? _leftPage;
    [ObservableProperty] private Bitmap? _rightPage;
    [ObservableProperty] private int _leftPageOffset;
    [ObservableProperty] private int _rightPageOffset = -1;
    [ObservableProperty] private HighlightRegion? _leftHighlight;
    [ObservableProperty] private HighlightRegion? _rightHighlight;
    [ObservableProperty] private bool _isLandscape = true;
    [ObservableProperty] private double _positionMs;
    [ObservableProperty] private double _durationMs;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _tapAddsMarker = true;
    [ObservableProperty] private bool _followPlayback = true;
    [ObservableProperty] private MarkerItem? _selectedMarker;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _viewportWidth = 800;
    [ObservableProperty] private double _viewportHeight = 1000;
    [ObservableProperty] private double _renderScale = 1;

    public string SongTitle => Song.Title;
    public string RecordingName => Recording.Name;
    public int PageCount => Song.PageCount;
    public bool TwoPages => IsLandscape && Song.PageCount > 1;
    public bool HasRightPage => RightPageOffset >= 0;
    public string PageText => $"Page {PageOffset + 1} of {PageCount}";
    public string LeftLabel => LeftPageOffset == PageOffset ? $"▶ p.{LeftPageOffset + 1}" : $"p.{LeftPageOffset + 1}";
    public string RightLabel => RightPageOffset == PageOffset ? $"▶ p.{RightPageOffset + 1}" : $"p.{RightPageOffset + 1}";
    public string PositionText => TimingEngine.FormatTime(PositionMs);
    public string DurationText => TimingEngine.FormatTime(DurationMs);
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";
    public string? SelectedMarkerId => SelectedMarker?.Marker.Id;
    public bool CanGoPrevPage => PageOffset > 0;
    public bool CanGoNextPage => PageOffset < PageCount - 1;
    public string NextPageNowText => PageOffset < PageCount - 1 ? $"Page {PageOffset + 2} starts now" : "Last page";
    public string IntroText => PageCount > 1
        ? "Play the recording. When the music reaches the top of the next page, press the big button (or key N). Then tap the page where the music is to add markers."
        : "This song has a single page, so no page turns are needed. Tap the page where the music is to add markers (optional); they drive the highlight and tap-to-seek.";

    public double HighlightHeight
    {
        get => Song.HighlightHeight;
        set
        {
            var v = Math.Clamp(value, 0.02, 0.3);
            if (Math.Abs(Song.HighlightHeight - v) < 0.0001) return;
            Song.HighlightHeight = v;
            OnPropertyChanged();
            UpdateHighlight();
            AppServices.Session.MarkDirty();
        }
    }

    public double SliderPosition
    {
        get => PositionMs;
        set
        {
            if (_dragging || Math.Abs(value - PositionMs) > 250)
                Seek(value);
        }
    }

    public TimingEditorViewModel(Song song, Recording recording)
    {
        Song = song;
        Recording = recording;
        _timer.Tick += (_, _) => Tick();
        RebuildPageTimings();
        RebuildMarkers();
    }

    public void Activate()
    {
        _active = true;
        Audio.StateChanged += OnAudioStateChanged;
        Audio.MediaEnded += OnMediaEnded;
        Audio.DurationChanged += OnDurationChanged;
        _timer.Start();
        _ = LoadAudioAsync();
        _renderedSpread = -1;
        _ = RenderSpreadAsync();
        AppServices.Platform.KeepScreenOn(true);
    }

    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        Audio.StateChanged -= OnAudioStateChanged;
        Audio.MediaEnded -= OnMediaEnded;
        Audio.DurationChanged -= OnDurationChanged;
        try { Audio.Pause(); } catch { /* ignore */ }
        AppServices.Platform.KeepScreenOn(false);
        if (Recording.DurationMs <= 0 && DurationMs > 0) Recording.DurationMs = DurationMs;
    }

    private async Task LoadAudioAsync()
    {
        try
        {
            StatusText = "Loading audio…";
            if (!await MediaResolver.EnsureResolvedAsync(Recording.Source, AppServices.Platform.CacheDir))
            {
                StatusText = "Audio file not found.";
                return;
            }
            var path = Recording.Source.ResolvedPath!;
            if (!(Audio.IsLoaded && string.Equals(AppServices.LoadedAudioPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                await Audio.LoadAsync(path);
                AppServices.LoadedAudioPath = path;
            }
            _loaded = true;
            PageOffset = Math.Clamp(TimingEngine.PageOffsetAt(Recording, Audio.PositionMs), 0, PageCount - 1);
            DurationMs = Audio.DurationMs;
            if (DurationMs > 0) Recording.DurationMs = DurationMs;
            StatusText = IntroText;
        }
        catch (Exception ex)
        {
            StatusText = "Could not load audio: " + ex.Message;
        }
    }

    private void OnAudioStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        IsPlaying = Audio.State == PlaybackState.Playing;
        OnPropertyChanged(nameof(PlayPauseGlyph));
    });

    private void OnDurationChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        DurationMs = Audio.DurationMs;
        if (DurationMs > 0) Recording.DurationMs = DurationMs;
        OnPropertyChanged(nameof(DurationText));
    });

    private void OnMediaEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        IsPlaying = false;
        OnPropertyChanged(nameof(PlayPauseGlyph));
    });

    private void Tick()
    {
        if (!_active) return;
        PositionMs = Audio.PositionMs;
        OnPropertyChanged(nameof(PositionText));
        if (!_dragging) OnPropertyChanged(nameof(SliderPosition));
        if (FollowPlayback && IsPlaying)
        {
            var p = TimingEngine.PageOffsetAt(Recording, PositionMs);
            if (p != PageOffset) PageOffset = Math.Clamp(p, 0, PageCount - 1);
        }
        UpdateHighlight();
        var current = TimingEngine.CurrentMarker(Recording, PositionMs);
        foreach (var m in Markers) m.IsCurrent = current != null && m.Marker.Id == current.Id;
        var page = TimingEngine.PageOffsetAt(Recording, PositionMs);
        foreach (var t in PageTimings) t.IsCurrent = t.PageOffset == page;
    }

    private void UpdateHighlight()
    {
        var hl = TimingEngine.HighlightAt(Song, Recording, PositionMs);
        LeftHighlight = hl != null && hl.Value.PageOffset == LeftPageOffset ? hl : null;
        RightHighlight = hl != null && RightPageOffset >= 0 && hl.Value.PageOffset == RightPageOffset ? hl : null;
    }

    partial void OnPageOffsetChanged(int value)
    {
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanGoPrevPage));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(NextPageNowText));
        OnPropertyChanged(nameof(LeftLabel));
        OnPropertyChanged(nameof(RightLabel));
        UpdateHighlight();
        _ = RenderSpreadAsync();
    }

    partial void OnIsLandscapeChanged(bool value)
    {
        OnPropertyChanged(nameof(TwoPages));
        _renderedSpread = -1;
        _ = RenderSpreadAsync();
    }

    partial void OnSelectedMarkerChanged(MarkerItem? value) => OnPropertyChanged(nameof(SelectedMarkerId));

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseGlyph));

    partial void OnTapAddsMarkerChanged(bool value) =>
        StatusText = value ? "Tap the page where the music currently is to add a marker." : "Tap the page to seek to that spot.";

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
        var two = TwoPages;
        var pages = TimingEngine.VisiblePages(PageOffset, PageCount, two);
        if (pages.Length == 0) return;
        var spread = two ? TimingEngine.SpreadIndexForPage(PageOffset) : PageOffset;
        if (spread == _renderedSpread && two == _renderedTwo && LeftPage != null) return;
        _renderedSpread = spread;
        _renderedTwo = two;

        var maxW = (int)(ViewportWidth * RenderScale / (two ? 2 : 1));
        var maxH = (int)(ViewportHeight * RenderScale);
        var book = AppServices.Session.Book;
        try
        {
            var left = await AppServices.Pages.RenderAsync(book, Song.StartPage + pages[0], maxW, maxH);
            Bitmap? right = null;
            if (pages.Length > 1) right = await AppServices.Pages.RenderAsync(book, Song.StartPage + pages[1], maxW, maxH);
            if (_renderedSpread != spread || _renderedTwo != two) return;
            LeftPage = left;
            RightPage = right;
            LeftPageOffset = pages[0];
            RightPageOffset = pages.Length > 1 ? pages[1] : -1;
            RebuildPageMarkers();
            UpdateHighlight();
            OnPropertyChanged(nameof(HasRightPage));
            OnPropertyChanged(nameof(LeftLabel));
            OnPropertyChanged(nameof(RightLabel));

            // pre-render the following spread so page turns are instant
            var nextFirst = two ? (spread + 1) * 2 : PageOffset + 1;
            for (var i = 0; i < (two ? 2 : 1); i++)
            {
                var p = nextFirst + i;
                if (p < PageCount) _ = AppServices.Pages.RenderAsync(book, Song.StartPage + p, maxW, maxH);
            }
        }
        catch { /* leave previous */ }
    }

    private void RebuildPageTimings()
    {
        PageTimings.Clear();
        for (var i = 0; i < PageCount; i++)
        {
            var t = Recording.PageTimings.FirstOrDefault(x => x.PageOffset == i);
            PageTimings.Add(new PageTimingItem
            {
                PageOffset = i,
                HasTiming = i == 0 || t != null,
                TimeMs = i == 0 ? 0 : t?.TimeMs ?? 0
            });
        }
    }

    private void RefreshPageTimings()
    {
        foreach (var item in PageTimings)
        {
            var t = Recording.PageTimings.FirstOrDefault(x => x.PageOffset == item.PageOffset);
            item.HasTiming = item.PageOffset == 0 || t != null;
            item.TimeMs = item.PageOffset == 0 ? 0 : t?.TimeMs ?? 0;
        }
    }

    private void RebuildMarkers()
    {
        var keep = SelectedMarker?.Marker.Id;
        Markers.Clear();
        foreach (var m in Recording.Markers.OrderBy(m => m.TimeMs)) Markers.Add(new MarkerItem(m));
        SelectedMarker = Markers.FirstOrDefault(m => m.Marker.Id == keep);
        RebuildPageMarkers();
        OnPropertyChanged(nameof(MarkerCountText));
    }

    private void RebuildPageMarkers()
    {
        LeftMarkers.Clear();
        RightMarkers.Clear();
        foreach (var m in Recording.Markers)
        {
            if (m.PageOffset == LeftPageOffset) LeftMarkers.Add(m);
            else if (RightPageOffset >= 0 && m.PageOffset == RightPageOffset) RightMarkers.Add(m);
        }
    }

    public string MarkerCountText => $"{Recording.Markers.Count} marker(s)";

    // ---------------------------------------------------------------- transport

    [RelayCommand]
    private void PlayPause()
    {
        if (!_loaded) return;
        if (Audio.State == PlaybackState.Playing) Audio.Pause();
        else Audio.Play();
    }

    [RelayCommand]
    private void Stop()
    {
        if (!_loaded) return;
        Audio.Pause();
        Audio.Seek(0);
        if (FollowPlayback) PageOffset = 0;
        Tick();
    }

    [RelayCommand]
    private void Nudge(string ms)
    {
        if (!_loaded || !double.TryParse(ms, out var delta)) return;
        Seek(Audio.PositionMs + delta);
    }

    public void Seek(double ms)
    {
        if (!_loaded) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, DurationMs > 0 ? DurationMs : ms));
        Audio.Seek(ms);
        PositionMs = ms;
        if (FollowPlayback) PageOffset = Math.Clamp(TimingEngine.PageOffsetAt(Recording, ms), 0, PageCount - 1);
        Tick();
    }

    public void BeginSliderDrag() => _dragging = true;

    public void EndSliderDrag(double value)
    {
        _dragging = false;
        Seek(value);
    }

    [RelayCommand] private void PrevPage() { if (PageOffset > 0) PageOffset--; }
    [RelayCommand] private void NextPage() { if (PageOffset < PageCount - 1) PageOffset++; }

    /// <summary>The music just reached the top of the next page: stamp the time and show that page.</summary>
    [RelayCommand]
    private void NextPageNow()
    {
        if (PageOffset >= PageCount - 1) return;
        var next = PageOffset + 1;
        TimingEngine.SetPageStart(Recording, next, Audio.PositionMs);
        RefreshPageTimings();
        PageOffset = next;
        AppServices.Session.MarkDirty();
        StatusText = next < PageCount - 1
            ? $"Page {next + 1} starts at {TimingEngine.FormatTime(Audio.PositionMs)}. Keep playing; press again at the top of page {next + 2}."
            : $"Page {next + 1} starts at {TimingEngine.FormatTime(Audio.PositionMs)}. All page turns are set – press Done when you are finished.";
    }

    [RelayCommand]
    private void SetPageStartNow(PageTimingItem? item)
    {
        if (item == null || item.PageOffset == 0) return;
        TimingEngine.SetPageStart(Recording, item.PageOffset, Audio.PositionMs);
        RefreshPageTimings();
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private void ClearPageStart(PageTimingItem? item)
    {
        if (item == null || item.PageOffset == 0) return;
        TimingEngine.ClearPageStart(Recording, item.PageOffset);
        RefreshPageTimings();
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private void GoToPage(PageTimingItem? item)
    {
        if (item == null) return;
        if (item.HasTiming) Seek(TimingEngine.PageStartTime(Recording, item.PageOffset));
        PageOffset = item.PageOffset;
    }

    // ---------------------------------------------------------------- markers

    [RelayCommand]
    private void PageTap(PageTap? tap)
    {
        if (tap == null) return;
        var offset = Equals(tap.Tag, "R") ? RightPageOffset : LeftPageOffset;
        if (offset < 0) return;
        if (tap.MarkerId != null)
        {
            var existing = Markers.FirstOrDefault(m => m.Marker.Id == tap.MarkerId);
            if (existing != null)
            {
                SelectedMarker = existing;
                Seek(existing.Marker.TimeMs);
            }
            return;
        }
        if (TapAddsMarker)
        {
            var m = TimingEngine.AddMarker(Recording, offset, tap.X, tap.Y, Audio.PositionMs);
            // a marker on a later page implies that page starts no later than this time
            if (offset > 0 && !TimingEngine.HasPageTiming(Recording, offset))
            {
                TimingEngine.SetPageStart(Recording, offset, Audio.PositionMs);
                RefreshPageTimings();
            }
            if (offset != PageOffset) PageOffset = offset;
            RebuildMarkers();
            SelectedMarker = Markers.FirstOrDefault(x => x.Marker.Id == m.Id);
            UpdateHighlight();
            AppServices.Session.MarkDirty();
            StatusText = $"Marker added on page {offset + 1} at {TimingEngine.FormatTime(m.TimeMs)}.";
        }
        else
        {
            Seek(TimingEngine.TimeForTap(Song, Recording, offset, tap.X, tap.Y));
        }
    }

    [RelayCommand]
    private void MarkerMoved(PageTap? tap)
    {
        if (tap?.MarkerId == null) return;
        var m = Recording.Markers.FirstOrDefault(x => x.Id == tap.MarkerId);
        if (m == null) return;
        m.X = tap.X;
        m.Y = tap.Y;
        Markers.FirstOrDefault(x => x.Marker.Id == m.Id)?.Refresh();
        RebuildPageMarkers();
        UpdateHighlight();
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private void SetMarkerTimeNow(MarkerItem? item)
    {
        if (item == null) return;
        item.Marker.TimeMs = Audio.PositionMs;
        TimingEngine.SortMarkers(Recording);
        RebuildMarkers();
        SelectedMarker = Markers.FirstOrDefault(x => x.Marker.Id == item.Marker.Id);
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private void GoToMarker(MarkerItem? item)
    {
        if (item == null) return;
        SelectedMarker = item;
        PageOffset = item.Marker.PageOffset;
        Seek(item.Marker.TimeMs);
    }

    [RelayCommand]
    private void DeleteMarker(MarkerItem? item)
    {
        item ??= SelectedMarker;
        if (item == null) return;
        Recording.Markers.Remove(item.Marker);
        if (SelectedMarker == item) SelectedMarker = null;
        RebuildMarkers();
        UpdateHighlight();
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private async Task ClearMarkersAsync()
    {
        if (Recording.Markers.Count == 0) return;
        if (!await AppServices.Dialogs.ConfirmAsync("Clear markers", $"Delete all {Recording.Markers.Count} markers of this recording?", "Delete", "Keep")) return;
        Recording.Markers.Clear();
        SelectedMarker = null;
        RebuildMarkers();
        UpdateHighlight();
        AppServices.Session.MarkDirty();
    }

    [RelayCommand]
    private void Back() => AppServices.Main?.CloseTimingEditor();
}
