using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PracticalMusic.App.Services;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class MainView : UserControl
{
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
        if (e.Key == Key.Escape && vm.CurrentView is TimingEditorViewModel) { vm.CloseTimingEditor(); e.Handled = true; }
    }
}
