using Avalonia;
using PmApp = PracticalMusic.App.App;

namespace PracticalMusic.Desktop;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        var platform = new DesktopPlatformServices();
        PmApp.Platform = platform;
        if (args.Length > 0 && File.Exists(args[0])) PmApp.StartupFile = Path.GetFullPath(args[0]);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(platform.AppDataDir);
                File.AppendAllText(Path.Combine(platform.AppDataDir, "crash.log"), $"{DateTime.Now:u} {ex}\n\n");
            }
            catch { /* nothing more we can do */ }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PmApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
