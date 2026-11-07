using CommunityToolkit.Mvvm.ComponentModel;
using PracticalMusic.Core.Models;

namespace PracticalMusic.App.Services;

/// <summary>The book that is currently open, shared by every view model.</summary>
public sealed partial class BookSession : ObservableObject
{
    [ObservableProperty] private Book _book = new();
    [ObservableProperty] private string? _bookPath;
    [ObservableProperty] private bool _isDirty;

    /// <summary>A different Book instance was loaded or created.</summary>
    public event Action? BookReplaced;

    /// <summary>Documents, songs, recordings or timings changed inside the current book.</summary>
    public event Action? BookChanged;

    public string Title => Book.Title;

    public void Replace(Book book, string? path)
    {
        Book = book;
        BookPath = path;
        IsDirty = false;
        OnPropertyChanged(nameof(Title));
        BookReplaced?.Invoke();
    }

    public void MarkDirty()
    {
        IsDirty = true;
        Book.Modified = DateTimeOffset.Now;
        OnPropertyChanged(nameof(Title));
        BookChanged?.Invoke();
    }

    public void MarkSaved(string path)
    {
        BookPath = path;
        IsDirty = false;
    }
}
