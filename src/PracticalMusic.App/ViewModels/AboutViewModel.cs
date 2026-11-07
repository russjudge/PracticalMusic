using System.Reflection;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Services;

namespace PracticalMusic.App.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    private string? _originalPrompt;

    [ObservableProperty] private bool _showPrompt;

    public string AppName => "Practical Music";
    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string Author => "Russ Judge";
    public string Platform => AppServices.Platform.PlatformName;
    public string DataFolder => AppServices.Platform.AppDataDir;
    public string Description =>
        "Practical Music links PDF sheet music with audio recordings. Import a PDF and MP3s, mark where each " +
        "page and each line starts in the recording, then play: the pages turn themselves and the part being " +
        "played is highlighted. Books can be saved with the media embedded or as lightweight link files, sent to " +
        "other devices on the local network, and played back in sync across devices.";
    public string Credits =>
        "Built by Russ Judge with the assistance of Claude (Anthropic).\n\n" +
        "Open source components: Avalonia UI (MIT), PDFium (BSD), LibVLC / LibVLCSharp (LGPL), CommunityToolkit.Mvvm (MIT).";

    public string PromptButtonText => ShowPrompt ? "Hide the original prompt" : "Show the original prompt";

    /// <summary>The request, word for word, that Claude was given to create this application.</summary>
    public string OriginalPrompt
    {
        get
        {
            if (_originalPrompt != null) return _originalPrompt;
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://PracticalMusic.App/Assets/original-prompt.txt"));
                using var reader = new StreamReader(stream);
                _originalPrompt = reader.ReadToEnd().Trim();
            }
            catch (Exception ex)
            {
                _originalPrompt = "The original prompt could not be loaded: " + ex.Message;
            }
            return _originalPrompt;
        }
    }

    partial void OnShowPromptChanged(bool value) => OnPropertyChanged(nameof(PromptButtonText));

    [RelayCommand]
    private void TogglePrompt() => ShowPrompt = !ShowPrompt;
}
