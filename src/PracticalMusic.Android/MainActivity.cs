using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using PmApp = PracticalMusic.App.App;

namespace PracticalMusic.Android;

[Activity(
    Label = "Practical Music",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/appicon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity<PmApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PmApp.Platform = new AndroidPlatformServices(this);
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
