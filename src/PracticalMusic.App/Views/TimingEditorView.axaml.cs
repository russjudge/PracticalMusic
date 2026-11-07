using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class TimingEditorView : UserControl
{
    private TimingEditorViewModel? Vm => DataContext as TimingEditorViewModel;

    public TimingEditorView()
    {
        InitializeComponent();
        Focusable = true;
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => Vm?.BeginSliderDrag(), RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.EndSliderDrag(SeekSlider.Value), RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerCaptureLostEvent, (_, _) => Vm?.EndSliderDrag(SeekSlider.Value), RoutingStrategies.Bubble);
        PagesArea.SizeChanged += (_, _) => ReportViewport();
        AttachedToVisualTree += (_, _) => { ReportViewport(); Focus(); };
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
            case Key.Left: Vm.NudgeCommand.Execute("-2000"); e.Handled = true; break;
            case Key.Right: Vm.NudgeCommand.Execute("2000"); e.Handled = true; break;
            case Key.PageUp: Vm.PrevPageCommand.Execute(null); e.Handled = true; break;
            case Key.PageDown: Vm.NextPageCommand.Execute(null); e.Handled = true; break;
            case Key.N: Vm.NextPageNowCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: Vm.DeleteMarkerCommand.Execute(null); e.Handled = true; break;
        }
    }
}
