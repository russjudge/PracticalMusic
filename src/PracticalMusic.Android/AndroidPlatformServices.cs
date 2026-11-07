using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Android.Views;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Android;

public sealed class AndroidPlatformServices : IPlatformServices
{
    private readonly Activity _activity;
    private WifiManager.MulticastLock? _multicastLock;

    public AndroidPlatformServices(Activity activity)
    {
        _activity = activity;
        var external = activity.GetExternalFilesDir(null)?.AbsolutePath;
        AppDataDir = external ?? activity.FilesDir!.AbsolutePath;
        CacheDir = (activity.ExternalCacheDir ?? activity.CacheDir)!.AbsolutePath;
    }

    public string PlatformName => "Android";
    public string AppDataDir { get; }
    public string CacheDir { get; }

    public string DefaultDeviceName
    {
        get
        {
            try
            {
                var name = global::Android.Provider.Settings.Global.GetString(_activity.ContentResolver, "device_name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { /* ignore */ }
            return $"{Build.Manufacturer} {Build.Model}".Trim();
        }
    }

    public bool CopyImportedFiles => true;

    public IAudioPlayer CreateAudioPlayer() => new AndroidAudioPlayer();

    public void KeepScreenOn(bool on)
    {
        _activity.RunOnUiThread(() =>
        {
            var window = _activity.Window;
            if (window == null) return;
            if (on) window.AddFlags(WindowManagerFlags.KeepScreenOn);
            else window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        });
    }

    public void NetworkActive(bool active)
    {
        try
        {
            if (active)
            {
                if (_multicastLock != null) return;
                var wifi = (WifiManager?)_activity.ApplicationContext?.GetSystemService(Context.WifiService);
                _multicastLock = wifi?.CreateMulticastLock("practicalmusic");
                _multicastLock?.SetReferenceCounted(false);
                _multicastLock?.Acquire();
            }
            else
            {
                _multicastLock?.Release();
                _multicastLock = null;
            }
        }
        catch { /* not fatal: discovery may still work */ }
    }
}
