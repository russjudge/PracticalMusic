using Avalonia.Controls;
using Avalonia.Interactivity;
using PracticalMusic.App.Services;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
    }

    private async void OnCopyPrompt(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try
        {
            await clipboard.SetTextAsync(vm.OriginalPrompt);
            AppServices.Main?.SetStatus("Original prompt copied to the clipboard.");
        }
        catch (Exception ex)
        {
            AppServices.Main?.SetStatus("Could not copy: " + ex.Message);
        }
    }
}
