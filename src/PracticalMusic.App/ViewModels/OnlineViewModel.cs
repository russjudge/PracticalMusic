using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.ViewModels;

public sealed partial class OnlineBookItem : ViewModelBase
{
    public OnlineBookItem(BookSummary summary) => Summary = summary;
    public BookSummary Summary { get; }
    public string Title => Summary.Title;
    public string Details =>
        $"{Summary.SongCount} song(s) · {FormatBytes(Summary.MediaBytes)} · revision {Summary.Revision} · {Summary.UpdatedAt.LocalDateTime:g}" +
        (string.IsNullOrEmpty(Summary.UpdatedBy) ? "" : " · " + Summary.UpdatedBy);
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _actionText = "Download & open";
    [ObservableProperty] private bool _isBusy;

    public static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";
}

public sealed class OnlinePeerItem
{
    public OnlinePeerItem(OnlinePeer peer, bool followed)
    {
        Peer = peer;
        IsFollowed = followed;
    }

    public OnlinePeer Peer { get; }
    public string Name => Peer.Name;
    public string Details => $"{Peer.UserName} · {Peer.Platform}" + (string.IsNullOrEmpty(Peer.BookTitle) ? "" : $" · \"{Peer.BookTitle}\"");
    public bool Leading => Peer.Leading;
    public bool IsFollowed { get; }
    public bool CanFollow => Peer.Leading && !IsFollowed;
}

/// <summary>
/// The Online page: sign in to a Practical Music website, download the organization's books (and their updates),
/// upload changes (managers), and play in sync with everyone connected to the website - browsers included.
/// </summary>
public sealed partial class OnlineViewModel : ViewModelBase, IActivatable
{
    private List<OrgMembershipInfo> _orgs = new();
    private bool _applyingOrgs;
    private bool _userLoaded;
    private string? _autoOpenUid;

    public ObservableCollection<OrgMembershipInfo> Organizations { get; } = new();
    public ObservableCollection<OnlineBookItem> Books { get; } = new();
    public ObservableCollection<OnlinePeerItem> Peers { get; } = new();

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _userText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private OrgMembershipInfo? _selectedOrg;
    [ObservableProperty] private string _currentBookText = "";
    [ObservableProperty] private bool _currentHasUpdate;
    [ObservableProperty] private bool _onlineSyncEnabled;
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private string _connectionText = "";

    private static AppSettings Settings => AppServices.Settings;
    private static SyncCoordinator Sync => AppServices.Sync;
    private static BookSession Session => AppServices.Session;

    public bool IsSignedOut => !IsSignedIn;
    public bool HasBooks => Books.Count > 0;
    public bool NoBooks => IsSignedIn && Books.Count == 0;
    public bool HasPeers => Peers.Count > 0;
    public bool NoPeers => Peers.Count == 0;
    public bool CanUpload => IsSignedIn && TargetOrgForUpload() != null;
    public bool IsLeading => Sync.Role == SyncRole.Leader;
    public bool IsFollowingOnline => Sync.FollowTransport == SyncTransport.Online;
    public string BooksHeading => SelectedOrg == null ? "Books" : $"Books of {SelectedOrg.Name}";

    public OnlineViewModel()
    {
        _serverUrl = Settings.ServerUrl;
        _email = Settings.OnlineUserEmail ?? "";
        _onlineSyncEnabled = Settings.OnlineSyncEnabled;
        AppServices.OnlineSync.PeersChanged += () => Dispatcher.UIThread.Post(RefreshPeers);
        AppServices.OnlineSync.Changed += () => Dispatcher.UIThread.Post(UpdateSyncStatus);
        AppServices.OnlineSync.Log += m => Dispatcher.UIThread.Post(() => AppServices.Main?.SetStatus(m));
        AppServices.OnlineSync.StateReceived += (state, localAt) => Dispatcher.UIThread.Post(() => _ = OpenLeadersBookAsync(state));
        Sync.Changed += () => Dispatcher.UIThread.Post(UpdateSyncStatus);
        Session.BookReplaced += () => Dispatcher.UIThread.Post(() => { UpdateCurrentBook(); RefreshBookStates(); });
        Session.BookChanged += () => Dispatcher.UIThread.Post(UpdateCurrentBook);
    }

    public void Activate()
    {
        UpdateCurrentBook();
        RefreshPeers();
        UpdateSyncStatus();
        if (IsSignedIn && Books.Count == 0) _ = RefreshBooksAsync(); // also retries a sign-in that could not be checked at start-up
    }

    public void Deactivate() { }

    private OnlineClient NewClient() => new(Settings.ServerUrl, Settings.OnlineToken);

    /// <summary>At start-up: resume the saved sign-in (checks the token) and connect for synchronized playback.</summary>
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(Settings.OnlineToken)) return;
        try
        {
            using var client = NewClient();
            ApplyUser(await client.GetMeAsync());
            AppServices.StartOnlineSync();
            await RefreshBooksAsync();
        }
        catch (OnlineException ex) when (ex.IsUnauthorized)
        {
            ForgetSignIn();
            Message = "The sign-in on the website expired. Sign in again.";
        }
        catch (Exception ex)
        {
            // offline right now: keep the sign-in; the sync connection retries by itself, the page retries when opened/refreshed
            IsSignedIn = true;
            UserText = $"Signed in as {Settings.OnlineUserName} ({Settings.OnlineUserEmail}) - the website cannot be reached right now.";
            Message = ex.Message;
            AppServices.StartOnlineSync();
        }
    }

    // ------------------------------------------------------------------ account

    [RelayCommand]
    private async Task SignInAsync()
    {
        Message = "";
        if (!OnlineClient.TryNormalizeServer(ServerUrl, out var server, out var problem)) { Message = problem!; return; }
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password)) { Message = "Enter your e-mail address and password."; return; }
        IsBusy = true;
        try
        {
            using var client = new OnlineClient(server);
            var login = await client.LoginAsync(new LoginRequest
            {
                Email = Email.Trim(), Password = Password, DeviceName = Settings.DeviceName, Platform = AppServices.Platform.PlatformName
            });
            ServerUrl = server;
            Settings.ServerUrl = server;
            Settings.OnlineToken = login.Token;
            Settings.OnlineUserEmail = login.User.Email;
            Settings.OnlineUserName = login.User.FullName;
            Settings.Save();
            Password = "";
            ApplyUser(login.User);
            AppServices.StartOnlineSync();
            await RefreshBooksAsync();
            AppServices.Main?.SetStatus($"Signed in to {server} as {login.User.FullName}.");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try { using var client = NewClient(); await client.LogoutAsync(); } catch { /* the token is forgotten anyway */ }
        ForgetSignIn();
        AppServices.Main?.SetStatus("Signed out of the website.");
    }

    private void ForgetSignIn()
    {
        Sync.Online.Stop();
        Settings.OnlineToken = null;
        Settings.Save();
        IsSignedIn = false;
        _userLoaded = false;
        UserText = "";
        _orgs.Clear();
        Organizations.Clear();
        Books.Clear();
        Peers.Clear();
        NotifyLists();
    }

    private void ApplyUser(UserInfo user)
    {
        _userLoaded = true;
        IsSignedIn = true;
        UserText = $"Signed in as {user.FullName} ({user.Email})" + (user.IsSiteAdmin ? " · website administrator" : "");
        _orgs = user.Organizations;
        _applyingOrgs = true;
        Organizations.Clear();
        foreach (var o in _orgs) Organizations.Add(o);
        SelectedOrg = _orgs.FirstOrDefault(o => o.Id == Settings.OnlineOrgId) ?? _orgs.FirstOrDefault();
        _applyingOrgs = false;
        if (SelectedOrg != null && Settings.OnlineOrgId != SelectedOrg.Id)
        {
            Settings.OnlineOrgId = SelectedOrg.Id;
            Settings.Save();
        }
        if (_orgs.Count == 0) Message = "Your account does not belong to an organization yet. Ask to join one on the website (Account → My organizations).";
        NotifyLists();
    }

    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(NoBooks));
        OnPropertyChanged(nameof(CanUpload));
    }

    partial void OnSelectedOrgChanged(OrgMembershipInfo? value)
    {
        OnPropertyChanged(nameof(BooksHeading));
        OnPropertyChanged(nameof(CanUpload));
        if (_applyingOrgs || value == null) return;
        Settings.OnlineOrgId = value.Id;
        Settings.Save();
        Sync.Online.StopFollowing();
        AppServices.StartOnlineSync();
        _ = RefreshBooksAsync();
    }

    partial void OnOnlineSyncEnabledChanged(bool value)
    {
        Settings.OnlineSyncEnabled = value;
        Settings.Save();
        AppServices.StartOnlineSync();
        UpdateSyncStatus();
    }

    [RelayCommand]
    private Task OpenWebsiteAsync() => LaunchAsync("/");

    [RelayCommand]
    private Task CreateAccountAsync() => LaunchAsync("/Account/Register");

    private async Task LaunchAsync(string path)
    {
        if (!OnlineClient.TryNormalizeServer(ServerUrl, out var server, out var problem)) { Message = problem!; return; }
        if (AppServices.Launcher != null) await AppServices.Launcher.LaunchUriAsync(new Uri(server + path));
    }

    // ------------------------------------------------------------------ books

    [RelayCommand]
    private async Task RefreshBooksAsync()
    {
        if (IsSignedIn && !_userLoaded && !string.IsNullOrEmpty(Settings.OnlineToken))
        {
            await InitializeAsync(); // the start-up check failed (offline): try again now
            return;
        }
        if (!IsSignedIn || SelectedOrg == null) { Books.Clear(); NotifyLists(); return; }
        try
        {
            using var client = NewClient();
            var list = await client.GetBooksAsync(SelectedOrg.Id);
            Books.Clear();
            foreach (var b in list) Books.Add(new OnlineBookItem(b));
            RefreshBookStates();
            Message = "";
        }
        catch (OnlineException ex) when (ex.IsUnauthorized)
        {
            ForgetSignIn();
            Message = "The sign-in on the website expired. Sign in again.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        NotifyLists();
    }

    private bool IsOpen(long serverBookId) =>
        Session.Book.Origin is { } o && o.BookId == serverBookId && o.IsServer(Settings.ServerUrl);

    private void RefreshBookStates()
    {
        foreach (var item in Books)
        {
            var b = item.Summary;
            if (IsOpen(b.Id))
            {
                var o = Session.Book.Origin!;
                var changed = Session.IsDirty || BookFingerprint.HasLocalChanges(Session.Book);
                item.Status = o.Revision < b.Revision
                    ? $"Open now (revision {o.Revision}) · revision {b.Revision} is on the website" + (changed ? " · you changed this copy" : "")
                    : changed ? "Open now · changed on this device" : "Open now · up to date";
                item.ActionText = o.Revision < b.Revision ? "Update" : "Show";
                continue;
            }
            var local = AppServices.OnlineBooks.Find(Settings.ServerUrl, b.Id);
            if (local == null)
            {
                item.Status = "Not on this device yet";
                item.ActionText = "Download & open";
            }
            else if (local.Revision < b.Revision)
            {
                item.Status = $"On this device (revision {local.Revision}) · revision {b.Revision} is available";
                item.ActionText = "Update & open";
            }
            else
            {
                item.Status = OnlineLibrary.HasLocalChanges(local.Path) ? "On this device · changed here" : "On this device · up to date";
                item.ActionText = "Open";
            }
        }
    }

    [RelayCommand]
    private async Task OpenBookAsync(OnlineBookItem? item)
    {
        if (item == null) return;
        item.IsBusy = true;
        try { await OpenServerBookAsync(item.Summary); }
        finally { item.IsBusy = false; }
    }

    /// <summary>Opens a book of the website: the local copy when it is current, otherwise downloads it first.</summary>
    public async Task<bool> OpenServerBookAsync(BookSummary b)
    {
        var main = AppServices.Main!;
        if (IsOpen(b.Id) && Session.Book.Origin!.Revision >= b.Revision)
        {
            main.NavigateTo(main.Library);
            return true;
        }
        var local = AppServices.OnlineBooks.Find(Settings.ServerUrl, b.Id);
        if (local != null && local.Revision >= b.Revision && !IsOpen(b.Id))
        {
            await main.OpenReceivedBookAsync(local.Path);
            return IsOpen(b.Id);
        }

        // a download replaces what is on this device: say so when that copy was changed here
        var openChanged = IsOpen(b.Id) && (Session.IsDirty || BookFingerprint.HasLocalChanges(Session.Book));
        var localChanged = local != null && OnlineLibrary.HasLocalChanges(local.Path);
        if (openChanged || localChanged)
        {
            var r = await AppServices.Dialogs.ShowAsync("Changes on this device",
                $"Your copy of \"{b.Title}\" was changed on this device. Downloading revision {b.Revision} from the website replaces those changes." +
                (SelectedOrg?.CanEdit == true ? " Upload your copy first to keep them (Upload this book)." : ""),
                ("Download anyway", "download"), ("Cancel", "cancel"));
            if (r != "download") return false;
            if (IsOpen(b.Id)) Session.IsDirty = false;
        }
        else if (!await main.ConfirmDiscardChangesAsync())
        {
            return false;
        }

        var path = local?.Path ?? OnlineLibrary.PathFor(AppServices.OnlineDir, Settings.ServerUrl, b.OrgId, b.Id, b.Title);
        using (var progress = AppServices.Dialogs.Progress("Downloading", $"\"{b.Title}\" from the website…"))
        {
            try
            {
                using var client = NewClient();
                await client.DownloadPackageAsync(b.Id, path, progress);
            }
            catch (Exception ex)
            {
                progress.Dispose();
                await AppServices.Dialogs.MessageAsync("Download failed", ex.Message);
                return false;
            }
        }
        try
        {
            Session.IsDirty = false;
            await main.Library.LoadBookFromPathAsync(path);
            if (Session.Book.Origin is { } origin) AppServices.OnlineBooks.Remember(origin, b.Title, path);
            main.NavigateTo(main.Library);
            main.SetStatus($"Opened \"{b.Title}\" (revision {b.Revision}) from {SelectedOrg?.Name ?? "the website"}.");
            RefreshBookStates();
            return true;
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Could not open the book", ex.Message);
            return false;
        }
    }

    /// <summary>Downloads newer revisions of every book on this device that was not changed here.</summary>
    [RelayCommand]
    private async Task DownloadAllUpdatesAsync()
    {
        var updated = 0;
        var skipped = new List<string>();
        foreach (var item in Books.ToList())
        {
            var b = item.Summary;
            var local = AppServices.OnlineBooks.Find(Settings.ServerUrl, b.Id);
            if (local == null || local.Revision >= b.Revision) continue;
            var isOpen = IsOpen(b.Id);
            if (OnlineLibrary.HasLocalChanges(local.Path) || (isOpen && (Session.IsDirty || BookFingerprint.HasLocalChanges(Session.Book))))
            {
                skipped.Add(b.Title);
                continue;
            }
            item.IsBusy = true;
            try
            {
                using var client = NewClient();
                await client.DownloadPackageAsync(b.Id, local.Path);
                var manifest = BookPackage.ReadManifest(local.Path);
                if (manifest.Origin != null) AppServices.OnlineBooks.Remember(manifest.Origin, b.Title, local.Path);
                if (isOpen) await AppServices.Main!.Library.LoadBookFromPathAsync(local.Path);
                updated++;
            }
            catch (Exception ex) { Message = $"{b.Title}: {ex.Message}"; }
            finally { item.IsBusy = false; }
        }
        RefreshBookStates();
        AppServices.Main?.SetStatus(updated == 0 && skipped.Count == 0 ? "Every downloaded book is up to date."
            : $"Updated {updated} book(s)." + (skipped.Count > 0 ? $" Not updated because they were changed on this device: {string.Join(", ", skipped)}." : ""));
    }

    // ------------------------------------------------------------------ the open book

    private void UpdateCurrentBook()
    {
        var book = Session.Book;
        OnPropertyChanged(nameof(CanUpload));
        if (book.Documents.Count == 0) { CurrentBookText = "No book with music is open."; CurrentHasUpdate = false; return; }
        if (book.Origin is { } o && o.IsServer(Settings.ServerUrl))
        {
            var changed = Session.IsDirty || BookFingerprint.HasLocalChanges(book);
            CurrentBookText = $"\"{book.Title}\" comes from {o.OrgName ?? "the website"}, revision {o.Revision}" + (changed ? " - changed on this device." : ".");
        }
        else if (book.Origin != null)
        {
            CurrentBookText = $"\"{book.Title}\" comes from another website ({book.Origin.Server}).";
        }
        else
        {
            CurrentBookText = $"\"{book.Title}\" is only on this device." + (CanUpload ? " Upload it to share it with your organization." : "");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        var o = Session.Book.Origin;
        if (o == null || !o.IsServer(Settings.ServerUrl) || !IsSignedIn) { UpdateCurrentBook(); return; }
        try
        {
            using var client = NewClient();
            var summary = await client.GetBookSummaryAsync(o.BookId);
            CurrentHasUpdate = summary.Revision > o.Revision;
            CurrentBookText = CurrentHasUpdate
                ? $"Revision {summary.Revision} of \"{summary.Title}\" is on the website{(summary.UpdatedBy != null ? $" (by {summary.UpdatedBy})" : "")}; this device has revision {o.Revision}."
                : $"\"{summary.Title}\" is up to date (revision {o.Revision})" + (BookFingerprint.HasLocalChanges(Session.Book) || Session.IsDirty ? ", with changes on this device." : ".");
        }
        catch (Exception ex)
        {
            CurrentBookText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UpdateCurrentAsync()
    {
        var o = Session.Book.Origin;
        if (o == null) return;
        try
        {
            using var client = NewClient();
            await OpenServerBookAsync(await client.GetBookSummaryAsync(o.BookId));
            CurrentHasUpdate = false;
        }
        catch (Exception ex) { await AppServices.Dialogs.MessageAsync("Update failed", ex.Message); }
    }

    /// <summary>The organization an upload of the open book goes to (its own one when it came from the website), or null when not allowed.</summary>
    private OrgMembershipInfo? TargetOrgForUpload()
    {
        if (Session.Book.Origin is { } o && o.IsServer(Settings.ServerUrl))
            return _orgs.FirstOrDefault(x => x.Id == o.OrgId && x.CanEdit);
        return SelectedOrg is { CanEdit: true } so ? so : null;
    }

    [RelayCommand]
    private async Task UploadCurrentAsync()
    {
        var book = Session.Book;
        if (book.Documents.Count == 0) { await AppServices.Dialogs.MessageAsync("Nothing to upload", "Open or create a book with at least one PDF first."); return; }
        var org = TargetOrgForUpload();
        if (org == null)
        {
            await AppServices.Dialogs.MessageAsync("Not allowed", "Only the managers of an organization can upload its books.");
            return;
        }
        var origin = book.Origin is { } o && o.IsServer(Settings.ServerUrl) && o.OrgId == org.Id ? o : null;

        // the website needs the files themselves: package the book with its media, like "Send book"
        foreach (var src in book.AllMediaSources().Where(s => !s.IsResolved && s.Kind == MediaSourceKind.Url))
        {
            try { await MediaResolver.EnsureResolvedAsync(src, AppServices.Platform.CacheDir); } catch { /* stays a link */ }
        }
        var missing = book.AllMediaSources().Where(s => !s.IsResolved && s.Kind != MediaSourceKind.Url).Select(s => s.DisplayName).ToList();
        if (missing.Count > 0)
        {
            await AppServices.Dialogs.MessageAsync("Files missing", "These files cannot be found, so the book cannot be uploaded: " + string.Join(", ", missing) +
                ". In the Library use \"Locate file…\" (recordings) or add the PDF again.");
            return;
        }
        var temp = Path.Combine(AppServices.TempDir, "upload_" + Guid.NewGuid().ToString("N") + BookPackage.Extension);
        SaveBookResult? result = null;
        try
        {
            using (var progress = AppServices.Dialogs.Progress("Uploading", $"Packaging \"{book.Title}\"…"))
            {
                await BookPackage.SaveAsync(book, temp, embedMedia: true, tempDir: AppServices.TempDir);
                progress.SetMessage($"Uploading \"{book.Title}\" to {org.Name}…");
                using var client = NewClient();
                try
                {
                    result = await client.UploadPackageAsync(org.Id, temp, origin?.Revision, progress: progress);
                }
                catch (OnlineException ex) when (ex.IsConflict)
                {
                    progress.Dispose();
                    var choice = ex.Code == "exists"
                        ? await AppServices.Dialogs.ShowAsync("Already on the website", ex.Message, ("Replace it", "force"), ("Upload as a new book", "new"), ("Cancel", "cancel"))
                        : await AppServices.Dialogs.ShowAsync("Changed on the website",
                            ex.Message + "\n\nUpload your version anyway? It becomes the newest revision; the other changes stay in the website's history only as long as nobody saves over them.",
                            ("Upload mine", "force"), ("Cancel", "cancel"));
                    if (choice == "cancel") return;
                    using var again = AppServices.Dialogs.Progress("Uploading", $"Uploading \"{book.Title}\" to {org.Name}…");
                    result = await client.UploadPackageAsync(org.Id, temp, null, force: choice == "force", asNew: choice == "new", progress: again);
                }
            }
        }
        catch (Exception ex)
        {
            await AppServices.Dialogs.MessageAsync("Upload failed", ex.Message);
            return;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
        if (result == null) return;

        // remember where the book lives now, and keep the local copy in step with it
        if (!string.IsNullOrEmpty(result.Uid)) book.Id = result.Uid;
        book.Origin = new OnlineOrigin
        {
            Server = OnlineOrigin.NormalizeServer(Settings.ServerUrl), OrgId = result.OrgId, OrgName = org.Name, BookId = result.BookId,
            Revision = result.Revision, SyncedAt = DateTimeOffset.Now
        };
        book.Origin.Fingerprint = BookFingerprint.Compute(book);
        var path = Session.BookPath is { } p && File.Exists(p) && !p.StartsWith("content:", StringComparison.OrdinalIgnoreCase)
            ? p
            : OnlineLibrary.PathFor(AppServices.OnlineDir, Settings.ServerUrl, result.OrgId, result.BookId, book.Title);
        try
        {
            await BookPackage.SaveAsync(book, path, embedMedia: Session.BookPath == null || AppServices.Main!.Library.LastSaveEmbedded, tempDir: AppServices.TempDir);
            Session.MarkSaved(path);
            AppServices.Settings.AddRecent(path);
            AppServices.OnlineBooks.Remember(book.Origin, book.Title, path);
        }
        catch (Exception ex)
        {
            Session.MarkDirty(); // the origin is new: the user should save
            AppServices.Main?.SetStatus("Uploaded, but the local copy could not be saved: " + ex.Message);
        }
        _ = AppServices.OnlineSync.AnnounceBookAsync();
        AppServices.Main?.SetStatus(result.Unchanged
            ? $"\"{result.Title}\" is already up to date on the website (revision {result.Revision})."
            : $"Uploaded \"{result.Title}\" to {org.Name} - revision {result.Revision}.");
        UpdateCurrentBook();
        await RefreshBooksAsync();
    }

    // ------------------------------------------------------------------ synchronized playback

    private void RefreshPeers()
    {
        Peers.Clear();
        foreach (var p in AppServices.OnlineSync.Peers.OrderByDescending(p => p.Leading).ThenBy(p => p.Name))
            Peers.Add(new OnlinePeerItem(p, AppServices.OnlineSync.FollowingId == p.Id));
        OnPropertyChanged(nameof(HasPeers));
        OnPropertyChanged(nameof(NoPeers));
        UpdateSyncStatus();
    }

    private void UpdateSyncStatus()
    {
        var online = AppServices.OnlineSync;
        ConnectionText = !IsSignedIn ? "Sign in to play in sync with browsers and with devices on other networks."
            : !OnlineSyncEnabled ? "Synchronized playback through the website is off."
            : $"{online.Status}{(SelectedOrg != null ? $" · {SelectedOrg.Name}" : "")}" + (online.ClockSynced ? $" · round trip {online.RttMs:0} ms" : "");
        SyncStatus = Sync.Role switch
        {
            SyncRole.Leader => online.Leading && online.IsConnected
                ? "You are leading - on the local network and through the website."
                : "You are leading on the local network." + (IsSignedIn && OnlineSyncEnabled ? " (Not connected to the website.)" : ""),
            SyncRole.Follower => Sync.FollowTransport == SyncTransport.Online
                ? $"Following {Sync.LeaderName ?? "the leader"} through the website" + (online.ClockSynced ? $" (±{online.RttMs / 2:0} ms)." : " - measuring the clock…")
                : $"Following {Sync.LeaderName ?? "a device"} on the local network.",
            _ => "Not synchronized."
        };
        OnPropertyChanged(nameof(IsLeading));
        OnPropertyChanged(nameof(IsFollowingOnline));
    }

    [RelayCommand]
    private void Lead()
    {
        Sync.StartLeading();
        UpdateSyncStatus();
        AppServices.Main?.ShowPlayerCommand.Execute(null);
    }

    [RelayCommand]
    private void StopSync()
    {
        Sync.StopLeading();
        Sync.StopFollowing();
        RefreshPeers();
    }

    [RelayCommand]
    private async Task FollowAsync(OnlinePeerItem? item)
    {
        if (item == null) return;
        var peer = item.Peer;
        // open the leader's book first when it is in the library and not open yet
        if (peer.ServerBookId is { } id && !IsOpen(id))
        {
            var summary = Books.FirstOrDefault(b => b.Summary.Id == id)?.Summary;
            if (summary != null && !await OpenServerBookAsync(summary)) return;
        }
        Sync.FollowOnline(peer.Id);
        RefreshPeers();
        AppServices.Main?.ShowPlayerCommand.Execute(null);
    }

    /// <summary>While following through the website: when the leader switches to another book of the library, open it here too.</summary>
    private async Task OpenLeadersBookAsync(SyncState state)
    {
        if (Sync.FollowTransport != SyncTransport.Online || string.IsNullOrEmpty(state.BookId) || state.BookId == Session.Book.Id) return;
        if (_autoOpenUid == state.BookId) return; // one attempt per book
        _autoOpenUid = state.BookId;
        var summary = Books.FirstOrDefault(b => b.Summary.Uid == state.BookId)?.Summary;
        if (summary == null) return;
        if (await OpenServerBookAsync(summary)) AppServices.Main?.ShowPlayerCommand.Execute(null);
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(NoBooks));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(BooksHeading));
    }
}
