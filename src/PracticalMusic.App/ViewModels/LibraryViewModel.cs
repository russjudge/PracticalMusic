using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.ViewModels;

/// <summary>One page thumbnail in the book overview.</summary>
public sealed partial class PageItemViewModel : ViewModelBase
{
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private Song? _song;
    [ObservableProperty] private bool _isSongStart;
    [ObservableProperty] private bool _isInSong;
    [ObservableProperty] private bool _isSelectedSong;

    public int GlobalIndex { get; init; }
    public int PageNumber => GlobalIndex + 1;
    public string DocumentName { get; init; } = "";
    public int PageInDocument { get; init; }
    public string Caption => $"Page {PageNumber}";
    public string SubCaption => $"{DocumentName} p.{PageInDocument + 1}";
    public string SongTitle => Song == null ? "not in a song yet" : (IsSongStart ? "▶ " + Song.Title : Song.Title);

    partial void OnSongChanged(Song? value) => OnPropertyChanged(nameof(SongTitle));
    partial void OnIsSongStartChanged(bool value) => OnPropertyChanged(nameof(SongTitle));
}

public sealed partial class RecordingItemViewModel : ViewModelBase
{
    public Recording Recording { get; }
    public SongItemViewModel Owner { get; }

    public RecordingItemViewModel(Recording recording, SongItemViewModel owner)
    {
        Recording = recording;
        Owner = owner;
    }

    public string Name
    {
        get => Recording.Name;
        set
        {
            if (Recording.Name == value) return;
            Recording.Name = value;
            OnPropertyChanged();
            AppServices.Session.MarkDirty();
        }
    }

    public string FileName => Recording.Source.DisplayName;
    public bool IsMissing => !Recording.Source.IsResolved && Recording.Source.Kind != MediaSourceKind.Url;
    public bool IsOnline => Recording.Source.Kind == MediaSourceKind.Url && !Recording.Source.IsResolved;

    /// <summary>How many page turns still need a time (0 for single-page songs).</summary>
    public int PageTurnsNeeded => Math.Max(0, Owner.Song.PageCount - 1);
    public int PageTurnsSet => Recording.PageTimings.Count(t => t.PageOffset > 0 && t.PageOffset < Owner.Song.PageCount);
    public bool IsTimed => PageTurnsSet >= PageTurnsNeeded;

    public string Status => IsMissing ? "⚠ file missing – press Locate file…" :
        IsOnline ? "online (downloaded when played)" :
        PageTurnsNeeded == 0
            ? (Recording.Markers.Count == 0 ? "single page · no markers yet (optional)" : $"single page · {Recording.Markers.Count} markers")
            : $"{PageTurnsSet} of {PageTurnsNeeded} page turns set · {Recording.Markers.Count} markers" + (IsTimed ? "" : " – press Edit timing…");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsTimed));
        OnPropertyChanged(nameof(Status));
    }
}

public sealed partial class SongItemViewModel : ViewModelBase
{
    public Song Song { get; }
    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = new();

    public SongItemViewModel(Song song)
    {
        Song = song;
        foreach (var r in song.Recordings) Recordings.Add(new RecordingItemViewModel(r, this));
    }

    public string Title
    {
        get => Song.Title;
        set
        {
            if (Song.Title == value) return;
            Song.Title = value;
            OnPropertyChanged();
            AppServices.Session.MarkDirty();
        }
    }

    public int StartPageNumber => Song.StartPage + 1;
    public int EndPageNumber => Song.EndPage + 1;
    public int PageCount => Song.PageCount;
    public string PagesText => Song.PageCount == 1 ? $"Page {StartPageNumber}" : $"Pages {StartPageNumber}–{EndPageNumber}";
    public bool HasRecordings => Recordings.Count > 0;
    public bool HasMissing => Recordings.Any(r => r.IsMissing);
    public bool IsTimed => Recordings.Count > 0 && Recordings.Any(r => r.IsTimed);
    public string RecordingCountText => Recordings.Count switch
    {
        0 => "no recording yet",
        1 => "1 recording",
        var n => $"{n} recordings"
    };
    public string Summary => PagesText + " · " + RecordingCountText + (HasMissing ? " · ⚠ file missing" : "");

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StartPageNumber));
        OnPropertyChanged(nameof(EndPageNumber));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PagesText));
        foreach (var r in Recordings) r.Refresh();
        OnPropertyChanged(nameof(HasRecordings));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(IsTimed));
        OnPropertyChanged(nameof(RecordingCountText));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>
/// Book editor, organised as a guided flow: 1 add the PDF, 2 identify the songs (which pages belong to
/// which song), 3 add recordings to each song, 4 set the timing. Playing needs steps 1–3.
/// </summary>
/// <summary>One entry of the Recent ▾ menu.</summary>
public sealed class RecentBookItem
{
    public RecentBookItem(string path, IRelayCommand<string?> open)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        OpenCommand = new RelayCommand(() => open.Execute(path));
    }

    public string Path { get; }
    public string Name { get; }
    public IRelayCommand OpenCommand { get; }
}

public sealed partial class LibraryViewModel : ViewModelBase, IActivatable
{
    private CancellationTokenSource? _thumbCts;

    public BookSession Session => AppServices.Session;
    public ObservableCollection<PageItemViewModel> Pages { get; } = new();
    public ObservableCollection<SongItemViewModel> Songs { get; } = new();
    public ObservableCollection<PdfDocumentRef> Documents { get; } = new();

    [ObservableProperty] private SongItemViewModel? _selectedSong;
    [ObservableProperty] private RecordingItemViewModel? _selectedRecording;
    [ObservableProperty] private PageItemViewModel? _selectedPage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public bool LastSaveEmbedded { get; private set; } = true;
    public bool HasPages => Pages.Count > 0;
    public bool HasSongs => Songs.Count > 0;
    public bool HasSelectedSong => SelectedSong != null;
    public bool HasSelectedRecording => SelectedRecording != null;
    public bool SelectedSongHasRecordings => SelectedSong is { HasRecordings: true };
    public bool SelectedRecordingIsMissing => SelectedRecording is { IsMissing: true };
    public int UnassignedPageCount => Pages.Count(p => !p.IsInSong);
    public string PageCountText => Pages.Count == 0 ? "No PDF yet." : $"{Pages.Count} pages in {Documents.Count} PDF file(s)";

    // ---- guided flow
    public bool Step1Done => Pages.Count > 0;
    public bool Step2Done => Songs.Count > 0;
    public bool Step3Done => Songs.Any(s => s.HasRecordings);
    public bool Step4Done => Step3Done && Songs.Where(s => s.HasRecordings).All(s => s.IsTimed);
    public int CurrentStep => !Step1Done ? 1 : !Step2Done ? 2 : !Step3Done ? 3 : !Step4Done ? 4 : 5;
    public bool IsStep1 => CurrentStep == 1;
    public bool IsStep2 => CurrentStep == 2;
    public bool IsStep3 => CurrentStep == 3;
    public bool IsStep4 => CurrentStep == 4;
    /// <summary>0 = pending, 1 = current, 2 = done (for the step chips).</summary>
    public int Step1State => Step1Done ? 2 : CurrentStep == 1 ? 1 : 0;
    public int Step2State => Step2Done ? 2 : CurrentStep == 2 ? 1 : 0;
    public int Step3State => Step3Done ? 2 : CurrentStep == 3 ? 1 : 0;
    public int Step4State => Step4Done ? 2 : CurrentStep == 4 ? 1 : 0;

    public string StepHint => CurrentStep switch
    {
        1 => "Step 1 of 4 · Add the PDF that holds your sheet music. Press “Add PDF…” or drop a PDF file here.",
        2 => "Step 2 of 4 · Tell Practical Music which pages make up each song: press “Add song” and enter the title and the first and last page. " +
             "Repeat for every song in the PDF. Pages that belong to no song cannot be played.",
        3 => "Step 3 of 4 · Add the recordings: select a song, then press “Add MP3…” or drop an MP3 file onto the song.",
        4 => "Step 4 of 4 · Set the page turns: select a song and press “Edit timing…”. When that is done, press ▶ Play.",
        _ => UnassignedPageCount > 0
            ? $"Ready to play. {UnassignedPageCount} page(s) are not part of any song yet – add a song for them if they are needed."
            : "Ready to play. You can add more recordings, refine the timing, or save the book."
    };

    public string SongsHelpText => Songs.Count == 0
        ? "No songs yet. Each song is a range of pages; press “Add song” to define the first one."
        : UnassignedPageCount > 0
            ? $"{UnassignedPageCount} page(s) do not belong to a song yet. Select a song to play it, rename it, change its pages, or add recordings."
            : "Select a song to play it, rename it, change its pages, or add recordings.";

    // ---- selected page toolbar
    public bool HasSelectedPage => SelectedPage != null;
    public string SelectedPageInfo => SelectedPage == null ? "" :
        SelectedPage.Song == null ? $"Page {SelectedPage.PageNumber} · not in a song" : $"Page {SelectedPage.PageNumber} · “{SelectedPage.Song.Title}”";
    public bool CanStartSongAtSelectedPage => SelectedPage != null && Session.Book.SongStartingAt(SelectedPage.GlobalIndex) == null;
    public bool CanSetSelectedSongStart => SelectedSong != null && SelectedPage != null && SelectedPage.GlobalIndex != SelectedSong.Song.StartPage;
    public bool CanSetSelectedSongEnd => SelectedSong != null && SelectedPage != null && SelectedPage.GlobalIndex >= SelectedSong.Song.StartPage && SelectedPage.GlobalIndex != SelectedSong.Song.EndPage;
    public string SetStartText => SelectedSong == null ? "" : $"“{SelectedSong.Title}” starts here";
    public string SetEndText => SelectedSong == null ? "" : $"“{SelectedSong.Title}” ends here";

    public string BookTitle
    {
        get => Session.Book.Title;
        set
        {
            if (Session.Book.Title == value) return;
            Session.Book.Title = value;
            OnPropertyChanged();
            Session.MarkDirty();
        }
    }

    public LibraryViewModel()
    {
        Session.BookReplaced += Rebuild;
        Session.BookChanged += RefreshItems;
    }

    public void Activate() => RefreshItems();

    public void Deactivate() { }

    partial void OnSelectedSongChanged(SongItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedSong));
        OnPropertyChanged(nameof(SelectedSongHasRecordings));
        foreach (var p in Pages) p.IsSelectedSong = value != null && value.Song.ContainsPage(p.GlobalIndex);
        if (value != null && (SelectedRecording == null || SelectedRecording.Owner != value))
            SelectedRecording = value.Recordings.FirstOrDefault();
        NotifyPageToolbar();
    }

    partial void OnSelectedRecordingChanged(RecordingItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedRecording));
        OnPropertyChanged(nameof(SelectedRecordingIsMissing));
        if (value != null && SelectedSong != value.Owner) SelectedSong = value.Owner;
    }

    partial void OnSelectedPageChanged(PageItemViewModel? value)
    {
        // clicking a page selects the song it belongs to
        if (value?.Song != null)
        {
            var owner = Songs.FirstOrDefault(s => s.Song == value.Song);
            if (owner != null && owner != SelectedSong) SelectedSong = owner;
        }
        NotifyPageToolbar();
    }

    private void NotifyPageToolbar()
    {
        OnPropertyChanged(nameof(HasSelectedPage));
        OnPropertyChanged(nameof(SelectedPageInfo));
        OnPropertyChanged(nameof(CanStartSongAtSelectedPage));
        OnPropertyChanged(nameof(CanSetSelectedSongStart));
        OnPropertyChanged(nameof(CanSetSelectedSongEnd));
        OnPropertyChanged(nameof(SetStartText));
        OnPropertyChanged(nameof(SetEndText));
    }

    private void NotifyFlow()
    {
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(UnassignedPageCount));
        OnPropertyChanged(nameof(Step1Done));
        OnPropertyChanged(nameof(Step2Done));
        OnPropertyChanged(nameof(Step3Done));
        OnPropertyChanged(nameof(Step4Done));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        OnPropertyChanged(nameof(Step1State));
        OnPropertyChanged(nameof(Step2State));
        OnPropertyChanged(nameof(Step3State));
        OnPropertyChanged(nameof(Step4State));
        OnPropertyChanged(nameof(StepHint));
        OnPropertyChanged(nameof(SongsHelpText));
        OnPropertyChanged(nameof(SelectedSongHasRecordings));
        OnPropertyChanged(nameof(SelectedRecordingIsMissing));
        NotifyPageToolbar();
    }

    // ---------------------------------------------------------------- building the lists

    private void Rebuild()
    {
        OnPropertyChanged(nameof(BookTitle));
        var book = Session.Book;
        var keepSongId = SelectedSong?.Song.Id;
        var keepRecId = SelectedRecording?.Recording.Id;

        Documents.Clear();
        foreach (var d in book.Documents) Documents.Add(d);

        Songs.Clear();
        foreach (var s in book.SongsInPageOrder) Songs.Add(new SongItemViewModel(s));

        SelectedPage = null;
        Pages.Clear();
        var global = 0;
        foreach (var d in book.Documents)
        {
            for (var i = 0; i < d.PageCount; i++, global++)
                Pages.Add(new PageItemViewModel { GlobalIndex = global, DocumentName = d.Name, PageInDocument = i });
        }
        ApplySongsToPages();

        SelectedSong = Songs.FirstOrDefault(s => s.Song.Id == keepSongId) ?? Songs.FirstOrDefault();
        SelectedRecording = SelectedSong?.Recordings.FirstOrDefault(r => r.Recording.Id == keepRecId) ?? SelectedSong?.Recordings.FirstOrDefault();

        NotifyFlow();
        _ = LoadThumbnailsAsync();
    }

    /// <summary>Lighter refresh when songs/recordings changed but documents did not.</summary>
    private void RefreshItems()
    {
        var book = Session.Book;
        OnPropertyChanged(nameof(BookTitle));
        if (Documents.Count != book.Documents.Count || !Documents.SequenceEqual(book.Documents) || Pages.Count != book.TotalPages)
        {
            Rebuild();
            return;
        }
        // sync song list
        var desired = book.SongsInPageOrder.ToList();
        if (Songs.Count != desired.Count || Songs.Where((s, i) => s.Song != desired[i]).Any())
        {
            var keepSongId = SelectedSong?.Song.Id;
            var keepRecId = SelectedRecording?.Recording.Id;
            Songs.Clear();
            foreach (var s in desired) Songs.Add(new SongItemViewModel(s));
            SelectedSong = Songs.FirstOrDefault(s => s.Song.Id == keepSongId) ?? Songs.FirstOrDefault();
            SelectedRecording = SelectedSong?.Recordings.FirstOrDefault(r => r.Recording.Id == keepRecId) ?? SelectedSong?.Recordings.FirstOrDefault();
        }
        else
        {
            foreach (var s in Songs)
            {
                if (s.Recordings.Count != s.Song.Recordings.Count || s.Recordings.Where((r, i) => r.Recording != s.Song.Recordings[i]).Any())
                {
                    var keepRecId = SelectedRecording?.Recording.Id;
                    s.Recordings.Clear();
                    foreach (var r in s.Song.Recordings) s.Recordings.Add(new RecordingItemViewModel(r, s));
                    if (SelectedSong == s) SelectedRecording = s.Recordings.FirstOrDefault(r => r.Recording.Id == keepRecId) ?? s.Recordings.FirstOrDefault();
                }
                s.Refresh();
            }
        }
        ApplySongsToPages();
        NotifyFlow();
    }

    private void ApplySongsToPages()
    {
        var book = Session.Book;
        foreach (var p in Pages)
        {
            var song = book.SongAtPage(p.GlobalIndex);
            p.Song = song;
            p.IsInSong = song != null;
            p.IsSongStart = song != null && song.StartPage == p.GlobalIndex;
            p.IsSelectedSong = SelectedSong != null && SelectedSong.Song.ContainsPage(p.GlobalIndex);
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        _thumbCts?.Cancel();
        var cts = _thumbCts = new CancellationTokenSource();
        var book = Session.Book;
        foreach (var page in Pages.ToList())
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                var bmp = await AppServices.Pages.RenderAsync(book, page.GlobalIndex, 200, 280, cts.Token);
                if (!cts.IsCancellationRequested) page.Thumbnail = bmp;
            }
            catch (OperationCanceledException) { return; }
            catch { /* page unreadable: leave the placeholder */ }
        }
    }

    // ---------------------------------------------------------------- book commands

    [RelayCommand]
    private async Task NewBookAsync()
    {
        if (!await AppServices.Main!.ConfirmDiscardChangesAsync()) return;
        var title = await AppServices.Dialogs.PromptAsync("New book", "Name for the new book (for example the date of the service or the set list):", "My Songs");
        if (title == null) return;
        AppServices.Pages.ClearCache();
        Session.Replace(new Book { Title = string.IsNullOrWhiteSpace(title) ? "My Songs" : title.Trim() }, null);
    }

    [RelayCommand]
    private async Task OpenBookAsync()
    {
        if (!await AppServices.Main!.ConfirmDiscardChangesAsync()) return;
        var files = await FileImporter.PickAsync("Open book", false, FileImporter.BookType);
        var file = files.FirstOrDefault();
        if (file == null) return;
        try
        {
            IsBusy = true;
            BusyText = "Opening book…";
            var path = await FileImporter.ToLocalPathAsync(file);
            await LoadBookFromPathAsync(path);
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Could not open book", ex.Message);
        }
        finally { IsBusy = false; }
    }

    public async Task LoadBookFromPathAsync(string path)
    {
        var book = await BookPackage.LoadAsync(path, AppServices.Platform.CacheDir);
        // make sure page counts match the actual PDFs (files may have been replaced)
        foreach (var d in book.Documents)
        {
            if (!d.Source.IsResolved) continue;
            try
            {
                var count = AppServices.Pages.GetPageCount(d.Source.ResolvedPath!);
                if (count > 0) d.PageCount = count;
            }
            catch { /* keep stored count */ }
        }
        book.NormalizeSongs();
        AppServices.Pages.ClearCache();
        Session.Replace(book, path);
        AppServices.Settings.AddRecent(path);
        RefreshRecent();
        _ = AppServices.Lan.AnnounceAsync();
        var missing = book.AllMediaSources().Count(s => !s.IsResolved && s.Kind != MediaSourceKind.Url);
        AppServices.Main?.SetStatus(missing > 0
            ? $"Opened {book.Title}. {missing} media file(s) could not be found."
            : $"Opened {book.Title}.");
    }

    // ---------------------------------------------------------------- recent books

    public ObservableCollection<RecentBookItem> RecentBooks { get; } = new();
    public bool HasRecent => RecentBooks.Count > 0;

    public void RefreshRecent()
    {
        RecentBooks.Clear();
        foreach (var p in AppServices.Settings.RecentBooks) RecentBooks.Add(new RecentBookItem(p, OpenRecentCommand));
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path))
        {
            await AppServices.Dialogs.MessageAsync("File not found", $"{path}\n\nThe book was moved or deleted. It is removed from the recent list.");
            AppServices.Settings.RemoveRecent(path);
            RefreshRecent();
            return;
        }
        if (string.Equals(path, Session.BookPath, StringComparison.OrdinalIgnoreCase) && !Session.IsDirty) return;
        if (!await AppServices.Main!.ConfirmDiscardChangesAsync()) return;
        try
        {
            IsBusy = true;
            BusyText = "Opening book…";
            await LoadBookFromPathAsync(path);
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Could not open book", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task SaveEmbeddedAsync() => SaveAsync(embedMedia: true, forcePicker: false);

    [RelayCommand]
    private Task SaveLinksAsync() => SaveAsync(embedMedia: false, forcePicker: false);

    [RelayCommand]
    private Task SaveEmbeddedAsAsync() => SaveAsync(embedMedia: true, forcePicker: true);

    [RelayCommand]
    private Task SaveLinksAsAsync() => SaveAsync(embedMedia: false, forcePicker: true);

    /// <summary>Saves the book. Returns true when the file was written.</summary>
    public async Task<bool> SaveAsync(bool embedMedia, bool forcePicker)
    {
        var book = Session.Book;
        if (book.Documents.Count == 0 && book.Songs.Count == 0)
        {
            await AppServices.Dialogs.MessageAsync("Nothing to save", "Add a PDF first.");
            return false;
        }

        string? targetPath = forcePicker ? null : Session.BookPath;
        IStorageFile? pickedFile = null;
        if (targetPath == null)
        {
            var suggested = BookPackage.Sanitize(book.Title) + BookPackage.Extension;
            pickedFile = await FileImporter.PickSaveAsync(embedMedia ? "Save book with media" : "Save book (links only)", suggested, FileImporter.BookType);
            if (pickedFile == null) return false;
            targetPath = pickedFile.TryGetLocalPath();
            if (!string.IsNullOrEmpty(targetPath)) AppServices.Settings.RememberSaveFolder(targetPath);
        }

        if (embedMedia)
        {
            var unresolved = book.AllMediaSources().Where(s => !s.IsResolved).ToList();
            foreach (var s in unresolved.Where(s => s.Kind == MediaSourceKind.Url))
            {
                try { await MediaResolver.EnsureResolvedAsync(s, AppServices.Platform.CacheDir); } catch { /* stays as URL */ }
            }
            var stillMissing = book.AllMediaSources().Count(s => !s.IsResolved);
            if (stillMissing > 0 &&
                !await AppServices.Dialogs.ConfirmAsync("Missing media", $"{stillMissing} file(s) are not available and will be saved as links only. Continue?"))
                return false;
        }

        using var progress = AppServices.Dialogs.Progress("Saving", embedMedia ? "Packaging media…" : "Writing book…");
        try
        {
            var reporter = new Progress<(string Item, double Fraction)>(p =>
            {
                progress.Report(p.Fraction);
                progress.SetMessage(p.Item == "done" ? "Finishing…" : $"Adding {p.Item}");
            });
            if (!string.IsNullOrEmpty(targetPath))
            {
                await BookPackage.SaveAsync(book, targetPath, embedMedia, reporter, tempDir: AppServices.TempDir);
            }
            else
            {
                // content:// target (Android): write to a temp file then stream it into the picked file
                var temp = Path.Combine(AppServices.TempDir, "save_" + Guid.NewGuid().ToString("N") + BookPackage.Extension);
                await BookPackage.SaveAsync(book, temp, embedMedia, reporter, tempDir: AppServices.TempDir);
                await using (var output = await pickedFile!.OpenWriteAsync())
                await using (var input = File.OpenRead(temp))
                    await input.CopyToAsync(output);
                try { File.Delete(temp); } catch { /* ignore */ }
                targetPath = pickedFile.Path.ToString();
            }
            LastSaveEmbedded = embedMedia;
            Session.MarkSaved(targetPath);
            if (File.Exists(targetPath)) { AppServices.Settings.AddRecent(targetPath); RefreshRecent(); }
            AppServices.Main?.SetStatus($"Saved {Path.GetFileName(targetPath)}" + (embedMedia ? " (media embedded)" : " (links only)"));
            return true;
        }
        catch (Exception ex)
        {
            progress.Dispose();
            await AppServices.Dialogs.MessageAsync("Save failed", ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- step 1: PDFs

    [RelayCommand]
    private async Task AddPdfAsync()
    {
        var files = await FileImporter.PickAsync("Add PDF sheet music", true, FileImporter.PdfType);
        foreach (var f in files) await AddPdfFileAsync(f);
    }

    public async Task AddPdfFileAsync(IStorageFile file)
    {
        try
        {
            IsBusy = true;
            BusyText = $"Importing {file.Name}…";
            var src = await FileImporter.ImportAsync(file);
            await AddPdfSourceAsync(src);
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Could not add PDF", ex.Message);
        }
        finally { IsBusy = false; }
    }

    public async Task AddPdfSourceAsync(MediaSource src)
    {
        if (!await MediaResolver.EnsureResolvedAsync(src, AppServices.Platform.CacheDir))
            throw new FileNotFoundException("The PDF could not be read.", src.DisplayName);
        var count = await Task.Run(() => AppServices.Pages.GetPageCount(src.ResolvedPath!));
        if (count <= 0) throw new InvalidDataException("The file is not a readable PDF.");
        var doc = new PdfDocumentRef
        {
            Name = Path.GetFileNameWithoutExtension(src.FileName),
            Source = src,
            PageCount = count
        };
        var book = Session.Book;
        book.Documents.Add(doc);
        if (book.Songs.Count == 0 && book.Documents.Count == 1 &&
            (string.IsNullOrWhiteSpace(book.Title) || book.Title == "Untitled Book"))
            book.Title = doc.Name;
        // Songs are deliberately NOT created automatically: step 2 asks the user which pages form which song.
        book.NormalizeSongs();
        Session.MarkDirty();
        AppServices.Main?.SetStatus($"Added {doc.Name} ({count} pages). Next: identify the songs.");
    }

    [RelayCommand]
    private async Task RemoveDocumentAsync(PdfDocumentRef? doc)
    {
        if (doc == null) return;
        if (!await AppServices.Dialogs.ConfirmAsync("Remove PDF", $"Remove “{doc.Name}” and its {doc.PageCount} pages from the book? Songs on those pages are removed too.", "Remove", "Keep"))
            return;
        var book = Session.Book;
        var first = book.FirstPageOf(doc);
        if (first < 0) return;
        var last = first + doc.PageCount - 1;
        book.Songs.RemoveAll(s => s.StartPage >= first && s.StartPage <= last);
        foreach (var s in book.Songs.Where(s => s.StartPage > last)) s.StartPage -= doc.PageCount;
        foreach (var s in book.Songs.Where(s => s.StartPage < first && s.EndPage >= first)) s.PageCount = first - s.StartPage;
        book.Documents.Remove(doc);
        book.NormalizeSongs();
        if (!string.IsNullOrEmpty(doc.Source.ResolvedPath)) AppServices.Pages.Forget(doc.Source.ResolvedPath);
        Session.MarkDirty();
    }

    [RelayCommand]
    private void MoveDocumentUp(PdfDocumentRef? doc) => MoveDocument(doc, -1);

    [RelayCommand]
    private void MoveDocumentDown(PdfDocumentRef? doc) => MoveDocument(doc, +1);

    private void MoveDocument(PdfDocumentRef? doc, int delta)
    {
        if (doc == null) return;
        var book = Session.Book;
        var index = book.Documents.IndexOf(doc);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= book.Documents.Count) return;

        // remember songs by (document, offset inside document) so they travel with their pages
        var songPositions = book.Songs.Select(s =>
        {
            var located = book.ResolvePage(s.StartPage)!.Value;
            return (Song: s, Doc: located.Document, Offset: located.PageIndex);
        }).ToList();

        book.Documents.RemoveAt(index);
        book.Documents.Insert(target, doc);
        foreach (var (song, d, offset) in songPositions)
            song.StartPage = book.FirstPageOf(d) + offset;
        book.NormalizeSongs();
        Session.MarkDirty();
    }

    // ---------------------------------------------------------------- step 2: songs

    /// <summary>First run of pages that belong to no song, as 0-based (start, end); null when every page is assigned.</summary>
    private (int Start, int End)? FirstUnassignedRun()
    {
        var book = Session.Book;
        var total = book.TotalPages;
        for (var p = 0; p < total; p++)
        {
            if (book.SongAtPage(p) != null) continue;
            var end = p;
            while (end + 1 < total && book.SongAtPage(end + 1) == null) end++;
            return (p, end);
        }
        return null;
    }

    /// <summary>
    /// Asks for title + page range and creates the song. The song that comes before the new range is
    /// trimmed so it ends just before it; a song that *starts* inside the range is reported as a conflict.
    /// Returns the new song or null when cancelled.
    /// </summary>
    private async Task<Song?> AddSongWithDialogAsync(string dialogTitle, string initialTitle, int start, int end)
    {
        var book = Session.Book;
        var total = book.TotalPages;
        if (total == 0) return null;
        var title = initialTitle;
        var hint = UnassignedPageCount == 0
            ? "Every page already belongs to a song. The song before the new range is shortened to end just before it."
            : BuildUnassignedHint();

        while (true)
        {
            var r = await AppServices.Dialogs.PromptRangeAsync(dialogTitle,
                "Enter the title of the song and the pages (of the whole book) that belong to it.",
                "Song title", title, "First page", "Last page", start + 1, end + 1, 1, total, "Add song", hint);
            if (r == null) return null;
            title = string.IsNullOrWhiteSpace(r.Text) ? $"Song {book.Songs.Count + 1}" : r.Text.Trim();
            start = r.From - 1;
            end = r.To - 1;

            var conflict = book.Songs.FirstOrDefault(s => s.StartPage >= start && s.StartPage <= end);
            if (conflict != null)
            {
                await AppServices.Dialogs.MessageAsync("Pages already taken",
                    $"Page {conflict.StartPage + 1} is the first page of “{conflict.Title}” (pages {conflict.StartPage + 1}–{conflict.EndPage + 1}). " +
                    "Choose a different range, or select that song and change its pages first.");
                continue;
            }

            var song = new Song { Title = title, StartPage = start, PageCount = end - start + 1 };
            foreach (var prev in book.Songs.Where(s => s.StartPage < start && s.EndPage >= start))
                prev.PageCount = start - prev.StartPage;
            book.Songs.Add(song);
            book.NormalizeSongs();
            Session.MarkDirty();
            SelectedSong = Songs.FirstOrDefault(s => s.Song == song);
            AppServices.Main?.SetStatus($"Added “{song.Title}” (pages {song.StartPage + 1}–{song.EndPage + 1}). Next: add its recording.");
            return song;
        }
    }

    private string BuildUnassignedHint()
    {
        var runs = new List<string>();
        var book = Session.Book;
        var total = book.TotalPages;
        for (var p = 0; p < total; p++)
        {
            if (book.SongAtPage(p) != null) continue;
            var end = p;
            while (end + 1 < total && book.SongAtPage(end + 1) == null) end++;
            runs.Add(end == p ? $"{p + 1}" : $"{p + 1}–{end + 1}");
            p = end;
        }
        return runs.Count == 0 ? "" : "Pages not in any song yet: " + string.Join(", ", runs) + ".";
    }

    [RelayCommand]
    private async Task AddSongAsync()
    {
        var book = Session.Book;
        if (book.TotalPages == 0)
        {
            await AppServices.Dialogs.MessageAsync("Add a PDF first", "Songs are ranges of pages, so the book needs a PDF before songs can be added.");
            return;
        }
        var run = FirstUnassignedRun();
        var start = run?.Start ?? book.TotalPages - 1;
        var end = run?.End ?? book.TotalPages - 1;
        await AddSongWithDialogAsync("Add song", $"Song {book.Songs.Count + 1}", start, end);
    }

    /// <summary>Page toolbar: a new song begins on the selected page (the song before it ends just before).</summary>
    [RelayCommand]
    private async Task AddSongAtPageAsync(PageItemViewModel? page)
    {
        if (page == null) return;
        var book = Session.Book;
        var existing = book.SongStartingAt(page.GlobalIndex);
        if (existing != null)
        {
            SelectedSong = Songs.FirstOrDefault(s => s.Song == existing);
            return;
        }
        var start = page.GlobalIndex;
        var next = book.Songs.Where(s => s.StartPage > start).OrderBy(s => s.StartPage).FirstOrDefault();
        var end = next?.StartPage - 1 ?? book.TotalPages - 1;
        await AddSongWithDialogAsync($"New song starting on page {page.PageNumber}", $"Song {book.Songs.Count + 1}", start, end);
    }

    [RelayCommand]
    private async Task EditSongPagesAsync()
    {
        if (SelectedSong == null) return;
        var book = Session.Book;
        var song = SelectedSong.Song;
        var total = book.TotalPages;
        var title = song.Title;
        var start = song.StartPage;
        var end = song.EndPage;
        while (true)
        {
            var r = await AppServices.Dialogs.PromptRangeAsync("Song pages",
                "Change the title or the pages (of the whole book) that belong to this song.",
                "Song title", title, "First page", "Last page", start + 1, end + 1, 1, total, "Apply", BuildUnassignedHint());
            if (r == null) return;
            title = string.IsNullOrWhiteSpace(r.Text) ? song.Title : r.Text.Trim();
            start = r.From - 1;
            end = r.To - 1;

            var conflict = book.Songs.FirstOrDefault(s => s != song && s.StartPage >= start && s.StartPage <= end);
            if (conflict != null)
            {
                await AppServices.Dialogs.MessageAsync("Pages already taken",
                    $"Page {conflict.StartPage + 1} is the first page of “{conflict.Title}” (pages {conflict.StartPage + 1}–{conflict.EndPage + 1}). " +
                    "Choose a different range, or change that song's pages first.");
                continue;
            }
            var hasTiming = song.Recordings.Any(rec => rec.Markers.Count > 0 || rec.PageTimings.Any(t => t.PageOffset > 0));
            if (start != song.StartPage && hasTiming &&
                !await AppServices.Dialogs.ConfirmAsync("First page changes",
                    "Page times and markers are counted from the first page of the song. Moving the first page shifts them; you may need to redo the timing. Continue?",
                    "Continue", "Cancel"))
                return;

            song.Title = title;
            song.StartPage = start;
            song.PageCount = end - start + 1;
            foreach (var prev in book.Songs.Where(s => s != song && s.StartPage < start && s.EndPage >= start))
                prev.PageCount = start - prev.StartPage;
            book.NormalizeSongs();
            Session.MarkDirty();
            SelectedSong = Songs.FirstOrDefault(s => s.Song == song);
            return;
        }
    }

    /// <summary>Page toolbar: the selected song now starts on the given page.</summary>
    [RelayCommand]
    private void SetSongStart(PageItemViewModel? page)
    {
        if (page == null || SelectedSong == null) return;
        var book = Session.Book;
        var song = SelectedSong.Song;
        if (book.Songs.Any(s => s != song && s.StartPage == page.GlobalIndex)) return;
        var end = Math.Max(song.EndPage, page.GlobalIndex);
        song.StartPage = page.GlobalIndex;
        song.PageCount = Math.Max(1, end - song.StartPage + 1);
        // trim neighbours
        foreach (var other in book.Songs.Where(s => s != song))
        {
            if (other.StartPage < song.StartPage && other.EndPage >= song.StartPage) other.PageCount = Math.Max(1, song.StartPage - other.StartPage);
            if (other.StartPage > song.StartPage && song.EndPage >= other.StartPage) song.PageCount = Math.Max(1, other.StartPage - song.StartPage);
        }
        book.NormalizeSongs();
        Session.MarkDirty();
        SelectedSong = Songs.FirstOrDefault(s => s.Song == song);
    }

    /// <summary>Page toolbar: the selected song now ends on the given page.</summary>
    [RelayCommand]
    private void SetSongEnd(PageItemViewModel? page)
    {
        if (page == null || SelectedSong == null) return;
        var song = SelectedSong.Song;
        if (page.GlobalIndex < song.StartPage) return;
        var book = Session.Book;
        var next = book.Songs.Where(s => s != song && s.StartPage > song.StartPage).OrderBy(s => s.StartPage).FirstOrDefault();
        var maxEnd = next?.StartPage - 1 ?? book.TotalPages - 1;
        song.PageCount = Math.Clamp(page.GlobalIndex, song.StartPage, maxEnd) - song.StartPage + 1;
        book.NormalizeSongs();
        Session.MarkDirty();
        SelectedSong = Songs.FirstOrDefault(s => s.Song == song);
    }

    [RelayCommand]
    private async Task RenameSongAsync()
    {
        if (SelectedSong == null) return;
        var title = await AppServices.Dialogs.PromptAsync("Rename song", "Title:", SelectedSong.Title);
        if (!string.IsNullOrWhiteSpace(title)) SelectedSong.Title = title.Trim();
    }

    [RelayCommand]
    private async Task RemoveSongAsync()
    {
        if (SelectedSong == null) return;
        var song = SelectedSong.Song;
        if (!await AppServices.Dialogs.ConfirmAsync("Remove song", $"Remove “{song.Title}” and its {song.Recordings.Count} recording link(s)? The PDF pages stay in the book.", "Remove", "Keep"))
            return;
        Session.Book.Songs.Remove(song);
        Session.MarkDirty();
    }

    // ---------------------------------------------------------------- step 3: recordings

    [RelayCommand]
    private async Task AddRecordingAsync()
    {
        if (SelectedSong == null)
        {
            await AppServices.Dialogs.MessageAsync("Select a song", "Select the song in the Songs list first, then add its recording.");
            return;
        }
        var files = await FileImporter.PickAsync($"Add recording to “{SelectedSong.Title}”", true, FileImporter.AudioType);
        foreach (var f in files) await AddRecordingFileAsync(SelectedSong.Song, f);
    }

    public async Task AddRecordingFileAsync(Song song, IStorageFile file)
    {
        try
        {
            IsBusy = true;
            BusyText = $"Importing {file.Name}…";
            var src = await FileImporter.ImportAsync(file);
            AddRecordingSource(song, src);
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Could not add recording", ex.Message);
        }
        finally { IsBusy = false; }
    }

    public void AddRecordingSource(Song song, MediaSource src)
    {
        var rec = new Recording
        {
            Name = Path.GetFileNameWithoutExtension(src.FileName),
            Source = src,
            PageTimings = new List<PageTiming> { new() { PageOffset = 0, TimeMs = 0 } }
        };
        song.Recordings.Add(rec);
        Session.MarkDirty();
        SelectedSong = Songs.FirstOrDefault(s => s.Song == song);
        SelectedRecording = SelectedSong?.Recordings.FirstOrDefault(r => r.Recording == rec);
        AppServices.Main?.SetStatus(song.PageCount > 1
            ? $"Added “{rec.Name}” to “{song.Title}”. Next: press Edit timing… to set the page turns."
            : $"Added “{rec.Name}” to “{song.Title}”.");
    }

    [RelayCommand]
    private async Task AddRecordingUrlAsync()
    {
        if (SelectedSong == null) return;
        var song = SelectedSong.Song;
        var url = await AppServices.Dialogs.PromptAsync("Add recording from a web address",
            "Enter a direct link to an audio file (for example https://example.com/song.mp3). " +
            "Links to web pages such as Soundslice or YouTube cannot be played.", "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();

        MediaResolver.UrlProbe probe;
        using (var progress = AppServices.Dialogs.Progress("Checking address", url))
        {
            try { probe = await MediaResolver.ProbeUrlAsync(url); }
            catch (Exception ex) { probe = new MediaResolver.UrlProbe(false, null, "", 0, ex.Message); }
        }
        if (!probe.IsAudio)
        {
            await AppServices.Dialogs.MessageAsync("Not an audio file", probe.Problem ?? "The address does not point at an audio file.");
            return;
        }
        var src = MediaSource.FromUrl(url);
        src.FileName = probe.FileName;
        src.SizeBytes = probe.SizeBytes;
        AddRecordingSource(song, src);
    }

    [RelayCommand]
    private async Task RenameRecordingAsync()
    {
        if (SelectedRecording == null) return;
        var name = await AppServices.Dialogs.PromptAsync("Rename recording", "Name (e.g. Vocals, Piano, Click track):", SelectedRecording.Name);
        if (!string.IsNullOrWhiteSpace(name)) SelectedRecording.Name = name.Trim();
    }

    [RelayCommand]
    private async Task RemoveRecordingAsync()
    {
        if (SelectedRecording == null) return;
        var rec = SelectedRecording;
        if (!await AppServices.Dialogs.ConfirmAsync("Remove recording", $"Remove “{rec.Name}” from “{rec.Owner.Title}”? Its page times and markers are lost.", "Remove", "Keep"))
            return;
        rec.Owner.Song.Recordings.Remove(rec.Recording);
        Session.MarkDirty();
    }

    /// <summary>For a links-only book whose audio file was moved: point the recording at the file again.</summary>
    [RelayCommand]
    private async Task LocateRecordingAsync()
    {
        if (SelectedRecording == null) return;
        var files = await FileImporter.PickAsync($"Locate the audio file for “{SelectedRecording.Name}”", false, FileImporter.AudioType);
        var f = files.FirstOrDefault();
        if (f == null) return;
        var src = await FileImporter.ImportAsync(f);
        SelectedRecording.Recording.Source = src;
        Session.MarkDirty();
    }

    // ---------------------------------------------------------------- step 4: timing

    [RelayCommand]
    private void EditTiming()
    {
        if (SelectedSong == null) return;
        var rec = SelectedRecording?.Owner == SelectedSong ? SelectedRecording : SelectedSong.Recordings.FirstOrDefault();
        if (rec == null)
        {
            _ = AppServices.Dialogs.MessageAsync("No recording yet", "Add an MP3 to this song first; the timing links the recording to the pages.");
            return;
        }
        if (rec.IsMissing)
        {
            _ = AppServices.Dialogs.MessageAsync("File missing", $"The audio file for “{rec.Name}” cannot be found. Press “Locate file…” to point at it.");
            return;
        }
        AppServices.Main?.OpenTimingEditor(new TimingEditorViewModel(SelectedSong.Song, rec.Recording));
    }

    [RelayCommand]
    private void PlaySong()
    {
        if (SelectedSong == null) return;
        AppServices.Main?.Player.SelectSong(SelectedSong.Song, SelectedRecording?.Recording);
        AppServices.Main?.ShowPlayerCommand.Execute(null);
    }

    // ---------------------------------------------------------------- drag & drop

    /// <summary>
    /// Files dropped from the OS. PDFs are appended. Audio goes to the song it was dropped on, or to the song
    /// that owns the page it was dropped on; a drop on a page without a song offers to create one there.
    /// </summary>
    public async Task HandleDroppedFilesAsync(IEnumerable<IStorageItem> items, PageItemViewModel? targetPage, SongItemViewModel? targetSong)
    {
        var files = items.OfType<IStorageFile>().ToList();
        foreach (var f in files.Where(f => FileImporter.IsBook(f.Name)))
        {
            await AppServices.Main!.OpenReceivedBookAsync(await FileImporter.ToLocalPathAsync(f));
            return;
        }
        foreach (var f in files.Where(f => FileImporter.IsPdf(f.Name)))
            await AddPdfFileAsync(f);

        var audio = files.Where(f => FileImporter.IsAudio(f.Name)).ToList();
        if (audio.Count == 0) return;

        if (Pages.Count == 0)
        {
            await AppServices.Dialogs.MessageAsync("Add the PDF first", "Step 1 is the PDF with the sheet music. Recordings are added to songs in step 3.");
            return;
        }

        Song? song = targetSong?.Song;
        if (song == null && targetPage != null)
        {
            song = Session.Book.SongAtPage(targetPage.GlobalIndex);
            if (song == null)
            {
                // the dropped recording starts on this page: define the song here
                var book = Session.Book;
                var next = book.Songs.Where(s => s.StartPage > targetPage.GlobalIndex).OrderBy(s => s.StartPage).FirstOrDefault();
                var end = next?.StartPage - 1 ?? book.TotalPages - 1;
                song = await AddSongWithDialogAsync($"New song starting on page {targetPage.PageNumber}",
                    Path.GetFileNameWithoutExtension(audio[0].Name), targetPage.GlobalIndex, end);
                if (song == null) return;
            }
        }
        if (song == null)
        {
            if (Songs.Count == 0)
            {
                await AppServices.Dialogs.MessageAsync("Identify the songs first",
                    "Step 2 is to define the songs (which pages belong to which song). Press “Add song”, then drop the MP3 onto the song.");
                return;
            }
            if (SelectedSong == null)
            {
                await AppServices.Dialogs.MessageAsync("Drop onto a song", "Drop the MP3 onto a song in the Songs list (or onto one of its pages).");
                return;
            }
            song = SelectedSong.Song;
        }
        foreach (var f in audio) await AddRecordingFileAsync(song, f);
    }
}
