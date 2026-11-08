using PracticalMusic.Core.Audio;

namespace PracticalMusic.App.Services;

/// <summary>What each platform head (desktop, Android) must provide.</summary>
public interface IPlatformServices
{
    /// <summary>"Windows", "Linux", "Android" …</summary>
    string PlatformName { get; }

    /// <summary>Persistent per-user folder for settings, the media library and received books.</summary>
    string AppDataDir { get; }

    /// <summary>Folder for extracted/downloaded media that can be regenerated.</summary>
    string CacheDir { get; }

    /// <summary>Default device name shown to other devices on the network.</summary>
    string DefaultDeviceName { get; }

    /// <summary>True when picked files should be copied into the app library (content URIs on Android).</summary>
    bool CopyImportedFiles { get; }

    IAudioPlayer CreateAudioPlayer();

    /// <summary>Keeps the display awake during playback.</summary>
    void KeepScreenOn(bool on);

    /// <summary>Full-display mode for the Player: hide system bars where the platform has them (desktop window state is handled by the view).</summary>
    void SetImmersive(bool on);

    /// <summary>Called when the network services start/stop (Android acquires a multicast lock).</summary>
    void NetworkActive(bool active);
}
