using Avalonia.Platform.Storage;
using PracticalMusic.App.ViewModels;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Networking;

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
    public static SyncService Sync { get; private set; } = null!;
    public static MainViewModel? Main { get; set; }

    /// <summary>Set by the main view once it is attached to a top level (needed for file pickers).</summary>
    public static IStorageProvider? Storage { get; set; }

    /// <summary>Path of the file currently loaded in <see cref="Audio"/>, so views can avoid reloading it.</summary>
    public static string? LoadedAudioPath { get; set; }

    public static string LibraryDir => Path.Combine(Platform.AppDataDir, "library");
    public static string IncomingDir => Path.Combine(Platform.AppDataDir, "received");
    public static string TempDir => Path.Combine(Platform.CacheDir, "temp");

    public static void Initialize(IPlatformServices platform)
    {
        Platform = platform;
        Directory.CreateDirectory(platform.AppDataDir);
        Directory.CreateDirectory(platform.CacheDir);
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(TempDir);

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
        Sync = new SyncService(Lan);
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
        Sync.StopLeading();
        Sync.StopFollowing();
        Lan.Stop();
        Transfer.Stop();
        Platform.NetworkActive(false);
    }

    public static void Shutdown()
    {
        try { StopNetwork(); } catch { /* ignore */ }
        try { Audio.Dispose(); } catch { /* ignore */ }
        try { Pages.Dispose(); } catch { /* ignore */ }
        Settings.Save();
    }
}
