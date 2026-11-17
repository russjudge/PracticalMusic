using Avalonia.Platform.Storage;
using PracticalMusic.App.ViewModels;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;

namespace PracticalMusic.App.Services;

/// <summary>Composition root. Everything the view models share lives here.</summary>
public static class AppServices
{
    public static IPlatformServices Platform { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static BookSession Session { get; private set; } = null!;
    public static DialogService Dialogs { get; private set; } = null!;
    public static PageRenderer Pages { get; private set; } = null!;
    public static IAudioPlayer Audio { get; private set; } = null!;
    public static LanBus Lan { get; private set; } = null!;
    public static TransferService Transfer { get; private set; } = null!;
    /// <summary>Synchronized playback on the local network.</summary>
    public static SyncService LanSync { get; private set; } = null!;
    /// <summary>Synchronized playback through the Practical Music website.</summary>
    public static OnlineSyncClient OnlineSync { get; private set; } = null!;
    /// <summary>Both of the above: what the Player uses.</summary>
    public static SyncCoordinator Sync { get; private set; } = null!;
    public static MainViewModel? Main { get; set; }

    /// <summary>Set by the main view once it is attached to a top level (needed for file pickers).</summary>
    public static IStorageProvider? Storage { get; set; }

    /// <summary>Opens web pages in the system browser (set by the main view).</summary>
    public static ILauncher? Launcher { get; set; }

    public static OnlineLibrary OnlineBooks { get; private set; } = null!;

    /// <summary>Path of the file currently loaded in <see cref="Audio"/>, so views can avoid reloading it.</summary>
    public static string? LoadedAudioPath { get; set; }

    public static string LibraryDir => Path.Combine(Platform.AppDataDir, "library");
    public static string IncomingDir => Path.Combine(Platform.AppDataDir, "received");
    public static string TempDir => Path.Combine(Platform.CacheDir, "temp");
    /// <summary>Books downloaded from websites: online/&lt;host&gt;/&lt;organization&gt;/….pmbook</summary>
    public static string OnlineDir => Path.Combine(Platform.AppDataDir, "online");

    public static void Initialize(IPlatformServices platform)
    {
        Platform = platform;
        Directory.CreateDirectory(platform.AppDataDir);
        Directory.CreateDirectory(platform.CacheDir);
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(OnlineDir);
        OnlineBooks = new OnlineLibrary(OnlineDir);

        Settings = AppSettings.Load(platform.AppDataDir);
        if (string.IsNullOrWhiteSpace(Settings.DeviceName))
        {
            Settings.DeviceName = platform.DefaultDeviceName;
            Settings.Save();
        }

        Session = new BookSession();
        Dialogs = new DialogService();
        Pages = new PageRenderer();
        Audio = platform.CreateAudioPlayer();
        Audio.Volume = Math.Clamp(Settings.Volume, 0, 1);

        // A fresh id per process so that two copies on one machine (e.g. for testing) see each other.
        var instanceId = Guid.NewGuid().ToString("N");
        Lan = new LanBus(instanceId, Settings.DeviceName) { Platform = platform.PlatformName };
        Lan.BookInfoProvider = () => (Session.Book.Id, Session.Book.Title);
        Transfer = new TransferService(instanceId, () => Settings.DeviceName, IncomingDir);
        LanSync = new SyncService(Lan);
        OnlineSync = new OnlineSyncClient(Settings.DeviceName, platform.PlatformName)
        {
            BookInfoProvider = () =>
            {
                var book = Session.Book;
                var serverBookId = book.Origin is { } o && o.IsServer(Settings.ServerUrl) ? o.BookId : (long?)null;
                return (serverBookId, book.Id, book.Title);
            }
        };
        Sync = new SyncCoordinator(LanSync, OnlineSync);
        Session.BookReplaced += () => _ = OnlineSync.AnnounceBookAsync();
    }

    /// <summary>Connects to the website for synchronized playback (when signed in, enabled and an organization is chosen).</summary>
    public static void StartOnlineSync()
    {
        var s = Settings;
        if (!s.OnlineSyncEnabled || string.IsNullOrEmpty(s.OnlineToken) || s.OnlineOrgId is not { } orgId ||
            !OnlineClient.TryNormalizeServer(s.ServerUrl, out var server, out _))
        {
            OnlineSync.Stop();
            return;
        }
        using var client = new OnlineClient(server);
        OnlineSync.LocalName = s.DeviceName;
        OnlineSync.Start(client.SyncSocketUri, s.OnlineToken, orgId);
    }

    public static void StartNetwork()
    {
        if (Lan.IsRunning) return;
        try
        {
            Transfer.Start(Settings.TransferPort);
            Lan.TransferPort = Transfer.Port;
            Lan.LocalName = Settings.DeviceName;
            Lan.Start();
            Platform.NetworkActive(true);
        }
        catch (Exception ex)
        {
            Main?.SetStatus("Network unavailable: " + ex.Message);
        }
    }

    public static void StopNetwork()
    {
        if (!Lan.IsRunning) return;
        LanSync.StopLeading();
        LanSync.StopFollowing();
        Lan.Stop();
        Transfer.Stop();
        Platform.NetworkActive(false);
    }

    public static void Shutdown()
    {
        try { StopNetwork(); } catch { /* ignore */ }
        try { OnlineSync.Stop(); } catch { /* ignore */ }
        try { Audio.Dispose(); } catch { /* ignore */ }
        try { Pages.Dispose(); } catch { /* ignore */ }
        Settings.Save();
    }
}
