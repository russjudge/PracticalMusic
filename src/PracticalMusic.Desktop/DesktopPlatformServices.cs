using System.Runtime.InteropServices;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Desktop;

/// <summary>Windows and Linux implementation of the platform services.</summary>
public sealed class DesktopPlatformServices : IPlatformServices
{
    public string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Desktop";

    public string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "PracticalMusic");

    public string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "PracticalMusic", "cache");

    public string DefaultDeviceName => Environment.MachineName;

    public bool CopyImportedFiles => false;

    public IAudioPlayer CreateAudioPlayer() => new LibVlcAudioPlayer();

    public void KeepScreenOn(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SetThreadExecutionState(on
                ? ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired
                : ExecutionState.Continuous);
        }
        catch { /* ignore */ }
    }

    public void NetworkActive(bool active) { }

    public void SetImmersive(bool on) { } // the main view switches the window to full screen

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x01,
        DisplayRequired = 0x02,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);
}
