using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PracticalMusic.App.Services;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class MainView : UserControl
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            AppServices.Storage = top.StorageProvider;
            top.BackRequested += OnBackRequested;
        }
        if (DataContext is MainViewModel vm && !ReferenceEquals(vm, _vm))
        {
            _vm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyImmersive(vm.IsImmersive);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsImmersive) && sender is MainViewModel vm) ApplyImmersive(vm.IsImmersive);
    }

    /// <summary>Desktop: full-display mode means a full-screen window; the previous state is restored afterwards.</summary>
    private void ApplyImmersive(bool on)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        if (on)
        {
            if (window.WindowState != WindowState.FullScreen) _stateBeforeFullScreen = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        else if (window.WindowState == WindowState.FullScreen)
        {
            window.WindowState = _stateBeforeFullScreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullScreen;
        }
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.GoBack()) e.Handled = true;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // Enter / Escape inside a dialog
        if (vm.Dialogs.Current is { } dialog)
        {
            if (e.Key == Key.Escape) { dialog.Close("cancel"); e.Handled = true; }
            else if (e.Key == Key.Enter && dialog.Buttons.Count > 0) { dialog.Close(dialog.Buttons[0].Result); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape && vm.CurrentView is TimingEditorViewModel) { vm.CloseTimingEditor(); e.Handled = true; return; }
        // full-display mode of the Player: Esc leaves it, F11 toggles it
        if (e.Key == Key.Escape && vm.IsImmersive) { vm.Player.FillDisplay = false; e.Handled = true; return; }
        if (e.Key == Key.F11 && vm.CurrentView is PlayerViewModel) { vm.Player.FillDisplay = !vm.Player.FillDisplay; e.Handled = true; }
    }
}
