using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // full-display mode: real pointer movement or a tap over the pages brings the floating controls back.
        // Synthetic move events (tooltips, layout changes under a resting pointer) must not keep them awake.
        PagesHost.AddHandler(PointerMovedEvent, (_, e) =>
        {
            var p = e.GetPosition(PagesHost);
            if (Math.Abs(p.X - _lastPointer.X) + Math.Abs(p.Y - _lastPointer.Y) < 4) return;
            _lastPointer = p;
            Vm?.PokeOverlay();
        }, RoutingStrategies.Tunnel);
        PagesHost.AddHandler(PointerPressedEvent, (_, e) => { _lastPointer = e.GetPosition(PagesHost); Vm?.PokeOverlay(); Focus(); }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
    }

    private Point _lastPointer;
    private PlayerViewModel? _hooked;

    private void HookViewModel()
    {
        if (ReferenceEquals(_hooked, Vm)) return;
        if (_hooked != null) _hooked.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = Vm;
        if (_hooked != null) _hooked.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.ShowOverlay) or nameof(PlayerViewModel.FillDisplay))) return;
        // bars and controls appear/disappear: keep the keyboard focus on the view so Space/Esc/PageDown keep working,
        // and do not leave a tooltip of a now-hidden button on screen
        Dispatcher.UIThread.Post(() =>
        {
            Focus();
            if (Vm is { ShowOverlay: false })
                foreach (var c in OverlayBar.GetVisualDescendants().OfType<Control>()) ToolTip.SetIsOpen(c, false);
        });
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
            case Key.PageUp:
            case Key.Up: Vm.TurnPageCommand.Execute("prev"); e.Handled = true; break;
            case Key.PageDown:
            case Key.Down: Vm.TurnPageCommand.Execute("next"); e.Handled = true; break;
            case Key.N: Vm.NextSongCommand.Execute(null); e.Handled = true; break;
            case Key.P: Vm.PrevSongCommand.Execute(null); e.Handled = true; break;
            case Key.Home: Vm.RestartCommand.Execute(null); e.Handled = true; break;
            case Key.S: Vm.StopCommand.Execute(null); e.Handled = true; break;
        }
        if (e.Handled) Vm.PokeOverlay();
    }
}
