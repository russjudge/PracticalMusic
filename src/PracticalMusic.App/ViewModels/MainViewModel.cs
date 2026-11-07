using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.ViewModels;

/// <summary>Top-level navigation, status bar and the dialog overlay.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly Stack<object> _backStack = new();

    public LibraryViewModel Library { get; }
    public PlayerViewModel Player { get; }
    public ShareViewModel Share { get; }
    public AboutViewModel About { get; }
    public DialogService Dialogs => AppServices.Dialogs;
    public BookSession Session => AppServices.Session;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _selectedTab = "library";
    [ObservableProperty] private bool _isLibrarySelected = true;
    [ObservableProperty] private bool _isPlayerSelected;
    [ObservableProperty] private bool _isShareSelected;
    [ObservableProperty] private bool _isAboutSelected;

    public string Title => (Session.IsDirty ? "* " : "") + Session.Book.Title + " - Practical Music";

    public MainViewModel()
    {
        Library = new LibraryViewModel();
        Player = new PlayerViewModel();
        Share = new ShareViewModel();
        About = new AboutViewModel();

        Session.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
        Session.BookReplaced += () => OnPropertyChanged(nameof(Title));
        Session.BookChanged += () => OnPropertyChanged(nameof(Title));

        NavigateTo(Library);
    }

    /// <summary>Starts the network and reopens the last book. Called once AppServices.Main is set.</summary>
    public async Task StartupAsync()
    {
        var settings = AppServices.Settings;
        if (settings.NetworkEnabled) AppServices.StartNetwork();

        var last = App.StartupFile ?? settings.LastBookPath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
        {
            try
            {
                await Library.LoadBookFromPathAsync(last);
            }
            catch (Exception ex)
            {
                SetStatus("Could not reopen last book: " + ex.Message);
            }
        }
    }

    public void SetStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess()) StatusText = text;
        else Dispatcher.UIThread.Post(() => StatusText = text);
    }

    public void NavigateTo(object view, bool pushBack = false)
    {
        if (ReferenceEquals(CurrentView, view)) return;
        if (CurrentView is IActivatable old) old.Deactivate();
        if (pushBack && CurrentView != null) _backStack.Push(CurrentView);
        else if (!pushBack) _backStack.Clear();
        CurrentView = view;
        if (view is IActivatable act) act.Activate();

        IsLibrarySelected = view is LibraryViewModel || view is TimingEditorViewModel;
        IsPlayerSelected = view is PlayerViewModel;
        IsShareSelected = view is ShareViewModel;
        IsAboutSelected = view is AboutViewModel;
    }

    /// <summary>Android back button. Returns true when handled.</summary>
    public bool GoBack()
    {
        if (Dialogs.Current != null)
        {
            Dialogs.Current.Close("cancel");
            return true;
        }
        if (_backStack.Count > 0)
        {
            var previous = _backStack.Pop();
            if (CurrentView is IActivatable old) old.Deactivate();
            CurrentView = previous;
            if (previous is IActivatable act) act.Activate();
            IsLibrarySelected = previous is LibraryViewModel;
            return true;
        }
        if (CurrentView is not LibraryViewModel)
        {
            NavigateTo(Library);
            return true;
        }
        return false;
    }

    [RelayCommand] private void ShowLibrary() => NavigateTo(Library);
    [RelayCommand] private void ShowPlayer() => NavigateTo(Player);
    [RelayCommand] private void ShowShare() => NavigateTo(Share);
    [RelayCommand] private void ShowAbout() => NavigateTo(About);

    public void OpenTimingEditor(TimingEditorViewModel editor) => NavigateTo(editor, pushBack: true);

    public void CloseTimingEditor()
    {
        if (CurrentView is TimingEditorViewModel) GoBack();
    }

    /// <summary>Called by the platform before the app closes; offers to save unsaved work.</summary>
    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!Session.IsDirty) return true;
        var r = await Dialogs.ShowAsync("Unsaved changes",
            $"\"{Session.Book.Title}\" has unsaved changes.",
            ("Save", "save"), ("Discard", "discard"), ("Cancel", "cancel"));
        if (r == "cancel") return false;
        if (r == "save")
        {
            var ok = await Library.SaveAsync(embedMedia: !string.IsNullOrEmpty(Session.BookPath) && Library.LastSaveEmbedded, forcePicker: false);
            return ok;
        }
        return true;
    }

    public async Task OpenReceivedBookAsync(string path)
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        try
        {
            await Library.LoadBookFromPathAsync(path);
            NavigateTo(Library);
            SetStatus($"Opened {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync("Could not open book", ex.Message);
        }
    }

    public string BookFileNameSuggestion() => BookPackage.Sanitize(Session.Book.Title) + BookPackage.Extension;
}
