using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PracticalMusic.App.Services;

public sealed class DialogButton
{
    public string Text { get; init; } = "";
    public string Result { get; init; } = "";
    public bool IsAccent { get; init; }
}

/// <summary>One modal prompt rendered as an in-app overlay (works identically on desktop and Android).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasInput;
    [ObservableProperty] private string _inputLabel = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private bool _hasRange;
    [ObservableProperty] private string _fromLabel = "From";
    [ObservableProperty] private string _toLabel = "To";
    [ObservableProperty] private decimal _rangeFrom = 1;
    [ObservableProperty] private decimal _rangeTo = 1;
    [ObservableProperty] private decimal _rangeMin = 1;
    [ObservableProperty] private decimal _rangeMax = 1;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _showProgress;

    public ObservableCollection<DialogButton> Buttons { get; } = new();
    public TaskCompletionSource<string> Completion { get; } = new();

    [RelayCommand]
    private void Choose(DialogButton button) => Completion.TrySetResult(button.Result);

    public void Close(string result = "") => Completion.TrySetResult(result);
}

/// <summary>Queues prompts; the main view shows <see cref="Current"/> as an overlay.</summary>
public sealed class DialogService : ObservableObject
{
    private readonly ObservableCollection<DialogViewModel> _queue = new();

    public DialogService()
    {
        _queue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(HasDialog));
        };
    }

    public DialogViewModel? Current => _queue.FirstOrDefault();
    public bool HasDialog => _queue.Count > 0;

    public Task<string> ShowAsync(DialogViewModel dialog)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => ShowAsync(dialog));

        _queue.Add(dialog);
        dialog.Completion.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() => _queue.Remove(dialog)),
            TaskScheduler.Default);
        return dialog.Completion.Task;
    }

    public Task<string> ShowAsync(string title, string message, params (string Text, string Result)[] buttons)
    {
        var d = new DialogViewModel { Title = title, Message = message };
        for (var i = 0; i < buttons.Length; i++)
            d.Buttons.Add(new DialogButton { Text = buttons[i].Text, Result = buttons[i].Result, IsAccent = i == 0 });
        return ShowAsync(d);
    }

    public Task MessageAsync(string title, string message) => ShowAsync(title, message, ("OK", "ok"));

    public async Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "No") =>
        await ShowAsync(title, message, (yes, "yes"), (no, "no")) == "yes";

    public async Task<string?> PromptAsync(string title, string message, string initial = "", string ok = "OK")
    {
        var d = new DialogViewModel { Title = title, Message = message, HasInput = true, InputText = initial };
        d.Buttons.Add(new DialogButton { Text = ok, Result = "ok", IsAccent = true });
        d.Buttons.Add(new DialogButton { Text = "Cancel", Result = "cancel" });
        var r = await ShowAsync(d);
        return r == "ok" ? d.InputText : null;
    }

    public sealed record RangePromptResult(string Text, int From, int To);

    /// <summary>
    /// Text + numeric range prompt (used for "which pages make up this song").
    /// Returns null when cancelled; From/To are clamped to [min, max] and ordered.
    /// </summary>
    public async Task<RangePromptResult?> PromptRangeAsync(string title, string message, string inputLabel, string initial,
        string fromLabel, string toLabel, int from, int to, int min, int max, string ok = "OK", string hint = "")
    {
        max = Math.Max(min, max);
        var d = new DialogViewModel
        {
            Title = title, Message = message, HasInput = true, InputLabel = inputLabel, InputText = initial,
            HasRange = true, FromLabel = fromLabel, ToLabel = toLabel,
            RangeMin = min, RangeMax = max,
            RangeFrom = Math.Clamp(from, min, max), RangeTo = Math.Clamp(to, min, max),
            Hint = hint
        };
        d.Buttons.Add(new DialogButton { Text = ok, Result = "ok", IsAccent = true });
        d.Buttons.Add(new DialogButton { Text = "Cancel", Result = "cancel" });
        var r = await ShowAsync(d);
        if (r != "ok") return null;
        var a = (int)Math.Clamp(d.RangeFrom, min, max);
        var b = (int)Math.Clamp(d.RangeTo, min, max);
        return new RangePromptResult(d.InputText, Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>Shows a progress dialog; dispose the returned handle to close it.</summary>
    public ProgressHandle Progress(string title, string message, bool cancellable = false)
    {
        var d = new DialogViewModel { Title = title, Message = message, ShowProgress = true };
        if (cancellable) d.Buttons.Add(new DialogButton { Text = "Cancel", Result = "cancel" });
        _ = ShowAsync(d);
        return new ProgressHandle(d);
    }

    public sealed class ProgressHandle : IDisposable, IProgress<double>
    {
        private readonly DialogViewModel _dialog;
        public ProgressHandle(DialogViewModel dialog) => _dialog = dialog;
        public DialogViewModel Dialog => _dialog;
        public bool IsCancelled => _dialog.Completion.Task.IsCompleted;
        public void Report(double value) => Dispatcher.UIThread.Post(() => _dialog.Progress = Math.Clamp(value, 0, 1));
        public void SetMessage(string message) => Dispatcher.UIThread.Post(() => _dialog.Message = message);
        public void Dispose() => _dialog.Close();
    }
}
