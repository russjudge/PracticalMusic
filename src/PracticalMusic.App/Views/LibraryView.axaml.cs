using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PracticalMusic.App.ViewModels;

namespace PracticalMusic.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var files = e.Data.GetFiles()?.ToList();
        if (files == null || files.Count == 0) return;
        e.Handled = true;

        // Was the drop target a song in the song list, or a page thumbnail?
        PageItemViewModel? page = null;
        SongItemViewModel? song = null;
        var visual = e.Source as Visual;
        while (visual != null)
        {
            if (visual is StyledElement { DataContext: PageItemViewModel p }) { page = p; break; }
            if (visual is StyledElement { DataContext: SongItemViewModel s }) { song = s; break; }
            visual = visual.GetVisualParent();
        }
        await vm.HandleDroppedFilesAsync(files, page, song);
    }
}
