using CommunityToolkit.Mvvm.ComponentModel;

namespace PracticalMusic.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Views that own resources (audio, timers, network subscriptions) while visible.</summary>
public interface IActivatable
{
    void Activate();
    void Deactivate();
}
