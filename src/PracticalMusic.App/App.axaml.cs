using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PracticalMusic.App.Services;
using PracticalMusic.App.ViewModels;
using PracticalMusic.App.Views;

namespace PracticalMusic.App;

public partial class App : Application
{
    /// <summary>Set by the platform head (desktop / Android) before the framework initializes.</summary>
    public static IPlatformServices? Platform { get; set; }

    /// <summary>Optional .pmbook passed on the command line (desktop) to open at startup.</summary>
    public static string? StartupFile { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (Platform == null)
            throw new InvalidOperationException("App.Platform must be set by the platform project before startup.");

        AppServices.Initialize(Platform);
        var main = new MainViewModel();
        AppServices.Main = main;
        _ = main.StartupAsync();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = main };
                desktop.ShutdownRequested += (_, _) => AppServices.Shutdown();
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = main };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
