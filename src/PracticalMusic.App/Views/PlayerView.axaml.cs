using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class PlayerView : UserControl
{
    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    public PlayerView()
    {
        InitializeComponent();
        Focusable = true;
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => Vm?.BeginSliderDrag(), RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.EndSliderDrag(SeekSlider.Value), RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerCaptureLostEvent, (_, _) => Vm?.EndSliderDrag(SeekSlider.Value), RoutingStrategies.Bubble);
        PagesArea.SizeChanged += (_, _) => ReportViewport();
        AttachedToVisualTree += (_, _) => { ReportViewport(); Focus(); };
        DataContextChanged += (_, _) => ReportViewport();
    }

    private void ReportViewport()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Vm?.SetViewport(PagesArea.Bounds.Width, PagesArea.Bounds.Height, scale);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Vm == null || e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: Vm.PlayPauseCommand.Execute(null); e.Handled = true; break;
            case Key.Left: Vm.NudgeCommand.Execute("-5000"); e.Handled = true; break;
            case Key.Right: Vm.NudgeCommand.Execute("5000"); e.Handled = true; break;
            case Key.PageUp: Vm.TurnPageCommand.Execute("prev"); e.Handled = true; break;
            case Key.PageDown: Vm.TurnPageCommand.Execute("next"); e.Handled = true; break;
            case Key.N: Vm.NextSongCommand.Execute(null); e.Handled = true; break;
            case Key.P: Vm.PrevSongCommand.Execute(null); e.Handled = true; break;
            case Key.Home: Vm.RestartCommand.Execute(null); e.Handled = true; break;
        }
    }
}
