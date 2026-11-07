using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PracticalMusic.App.Services;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.ViewModels;

public sealed partial class PeerItemViewModel : ViewModelBase
{
    public PeerInfo Peer { get; }
    public PeerItemViewModel(PeerInfo peer) => Peer = peer;
    public string Id => Peer.Id;
    public string Name => Peer.Name;
    public string Details => $"{Peer.Platform} · {Peer.Address}" + (string.IsNullOrEmpty(Peer.BookTitle) ? "" : $" · \"{Peer.BookTitle}\"");
    public bool Leading => Peer.Leading;
    [ObservableProperty] private string _activity = "";
    [ObservableProperty] private bool _isBusy;
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Leading));
    }
}

/// <summary>Network page: discover devices, send/request books, lead or follow synchronized playback.</summary>
public sealed partial class ShareViewModel : ViewModelBase, IActivatable
{
    public ObservableCollection<PeerItemViewModel> Peers { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private bool _networkEnabled;
    [ObservableProperty] private string _localInfo = "";
    [ObservableProperty] private string _firewallHint = "";
    [ObservableProperty] private string _syncStatus = "";
    [ObservableProperty] private bool _isLeading;
    [ObservableProperty] private bool _isFollowing;

    public bool HasPeers => Peers.Count > 0;
    public bool NoPeers => Peers.Count == 0;
    public string BookTitle => AppServices.Session.Book.Title;

    public ShareViewModel()
    {
        DeviceName = AppServices.Settings.DeviceName;
        NetworkEnabled = AppServices.Settings.NetworkEnabled;

        AppServices.Lan.PeersChanged += () => Dispatcher.UIThread.Post(RefreshPeers);
        AppServices.Lan.Log += m => AddLog(m);
        AppServices.Transfer.Log += m => AddLog(m);
        AppServices.Transfer.OfferHandler = OnOfferAsync;
        AppServices.Transfer.RequestHandler = OnRequestAsync;
        AppServices.Transfer.FileReceived += OnFileReceived;
        AppServices.Sync.Changed += () => Dispatcher.UIThread.Post(UpdateSyncStatus);
        AppServices.Session.BookReplaced += () => OnPropertyChanged(nameof(BookTitle));
        AppServices.Session.BookChanged += () => OnPropertyChanged(nameof(BookTitle));
    }

    public void Activate()
    {
        RefreshPeers();
        UpdateSyncStatus();
        UpdateLocalInfo();
        _ = AppServices.Lan.AnnounceAsync();
    }

    public void Deactivate() { }

    private void UpdateLocalInfo()
    {
        var lan = AppServices.Lan;
        var transfer = AppServices.Transfer;
        var portNote = transfer.PreferredPort != 0 && transfer.Port != transfer.PreferredPort
            ? $" (port {transfer.PreferredPort} was in use)" : "";
        LocalInfo = lan.IsRunning
            ? $"This device: {lan.LocalName} · {LanBus.LocalIPv4()?.ToString() ?? "no IPv4"} · discovery UDP {LanBus.DiscoveryPort} · transfer TCP {transfer.Port}{portNote}"
            : "Network sharing is off.";
        FirewallHint = lan.IsRunning
            ? $"Firewalls must allow inbound UDP {LanBus.DiscoveryPort} and TCP {transfer.Port} from this network on every device; the install notes list the exact commands (ufw on Linux, New-NetFirewallRule on Windows)."
            : "";
    }

    private void AddLog(string message) => Dispatcher.UIThread.Post(() =>
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    });

    private void RefreshPeers()
    {
        var current = AppServices.Lan.Peers;
        foreach (var stale in Peers.Where(p => current.All(c => c.Id != p.Id)).ToList()) Peers.Remove(stale);
        foreach (var peer in current)
        {
            var existing = Peers.FirstOrDefault(p => p.Id == peer.Id);
            if (existing == null) Peers.Add(new PeerItemViewModel(peer));
            else existing.Refresh();
        }
        OnPropertyChanged(nameof(HasPeers));
        OnPropertyChanged(nameof(NoPeers));
        UpdateLocalInfo();
    }

    private void UpdateSyncStatus()
    {
        var sync = AppServices.Sync;
        IsLeading = sync.Role == SyncRole.Leader;
        IsFollowing = sync.Role == SyncRole.Follower;
        SyncStatus = sync.Role switch
        {
            SyncRole.Leader => "You are leading. Play/pause/seek in the Player is mirrored on followers.",
            SyncRole.Follower => sync.ClockSynced
                ? $"Following {sync.Leader?.Name ?? "leader"} · clock offset {sync.ClockOffsetMs:0} ms · round trip {sync.RttMs:0} ms"
                : $"Following {sync.Leader?.Name ?? "leader"} · measuring clock…",
            _ => "Not synchronized. Lead a session, or follow a device that is leading."
        };
    }

    // ---------------------------------------------------------------- settings

    [RelayCommand]
    private void ApplyDeviceName()
    {
        var name = string.IsNullOrWhiteSpace(DeviceName) ? AppServices.Platform.DefaultDeviceName : DeviceName.Trim();
        DeviceName = name;
        AppServices.Settings.DeviceName = name;
        AppServices.Settings.Save();
        AppServices.Lan.LocalName = name;
        _ = AppServices.Lan.AnnounceAsync();
        UpdateLocalInfo();
    }

    partial void OnNetworkEnabledChanged(bool value)
    {
        AppServices.Settings.NetworkEnabled = value;
        AppServices.Settings.Save();
        if (value) AppServices.StartNetwork(); else AppServices.StopNetwork();
        RefreshPeers();
        UpdateSyncStatus();
        UpdateLocalInfo();
    }

    [RelayCommand]
    private void Refresh()
    {
        _ = AppServices.Lan.AnnounceAsync();
        RefreshPeers();
    }

    // ---------------------------------------------------------------- transfers

    private async Task<string?> PackageCurrentBookAsync()
    {
        var book = AppServices.Session.Book;
        if (book.Documents.Count == 0)
        {
            await AppServices.Dialogs.MessageAsync("Nothing to send", "Open or create a book with at least one PDF first.");
            return null;
        }
        var path = Path.Combine(AppServices.TempDir, BookPackage.Sanitize(book.Title) + BookPackage.Extension);
        using var progress = AppServices.Dialogs.Progress("Preparing", "Packaging book with media…");
        try
        {
            await BookPackage.SaveAsync(book, path, embedMedia: true,
                new Progress<(string Item, double Fraction)>(p => progress.Report(p.Fraction)));
            return path;
        }
        catch (Exception ex)
        {
            progress.Dispose();
            await AppServices.Dialogs.MessageAsync("Packaging failed", ex.Message);
            return null;
        }
    }

    [RelayCommand]
    private async Task SendToAsync(PeerItemViewModel? peer)
    {
        if (peer == null) return;
        var path = await PackageCurrentBookAsync();
        if (path == null) return;
        await SendPackageAsync(peer, path);
    }

    private async Task SendPackageAsync(PeerItemViewModel peer, string path)
    {
        peer.IsBusy = true;
        peer.Activity = "Waiting for acceptance…";
        try
        {
            var progress = new Progress<double>(f => peer.Activity = $"Sending {f:P0}");
            var ok = await AppServices.Transfer.SendFileAsync(peer.Peer.Address, peer.Peer.TransferPort, path, AppServices.Session.Book.Title, progress, CancellationToken.None);
            peer.Activity = ok ? "Sent ✓" : "Declined";
            AddLog(ok ? $"Sent book to {peer.Name}." : $"{peer.Name} declined the book.");
        }
        catch (Exception ex)
        {
            peer.Activity = "Failed";
            AddLog($"Send to {peer.Name} failed: {ex.Message}");
        }
        finally { peer.IsBusy = false; }
    }

    [RelayCommand]
    private async Task BlastAsync()
    {
        if (Peers.Count == 0)
        {
            await AppServices.Dialogs.MessageAsync("No devices", "No other devices running Practical Music were found on this network.");
            return;
        }
        var path = await PackageCurrentBookAsync();
        if (path == null) return;
        var targets = Peers.ToList();
        AddLog($"Blasting book to {targets.Count} device(s)…");
        await Task.WhenAll(targets.Select(p => SendPackageAsync(p, path)));
    }

    [RelayCommand]
    private async Task RequestFromAsync(PeerItemViewModel? peer)
    {
        if (peer == null) return;
        peer.IsBusy = true;
        peer.Activity = "Requesting…";
        try
        {
            var ok = await AppServices.Transfer.RequestBookAsync(peer.Id, peer.Peer.Address, peer.Peer.TransferPort, CancellationToken.None);
            peer.Activity = ok ? "Accepted, receiving…" : "Declined";
            AddLog(ok ? $"{peer.Name} agreed to send its book." : $"{peer.Name} declined the request.");
        }
        catch (Exception ex)
        {
            peer.Activity = "Failed";
            AddLog($"Request to {peer.Name} failed: {ex.Message}");
        }
        finally { peer.IsBusy = false; }
    }

    private async Task<bool> OnOfferAsync(TransferOffer offer)
    {
        AddLog($"{offer.SenderName} offers \"{offer.Title}\" ({offer.Size / 1048576.0:0.0} MB).");
        var size = offer.Size >= 1048576 ? $"{offer.Size / 1048576.0:0.0} MB" : $"{offer.Size / 1024.0:0} KB";
        return await AppServices.Dialogs.ConfirmAsync("Incoming book",
            $"{offer.SenderName} wants to send you \"{offer.Title}\" ({size}).\nAccept?", "Accept", "Reject");
    }

    private async Task<string?> OnRequestAsync(TransferRequest request)
    {
        AddLog($"{request.SenderName} requests your book.");
        var book = AppServices.Session.Book;
        var ok = await AppServices.Dialogs.ConfirmAsync("Book requested",
            $"{request.SenderName} asks you to send \"{book.Title}\" (with media). Send it?", "Send", "Decline");
        if (!ok) return null;
        return await Dispatcher.UIThread.InvokeAsync(PackageCurrentBookAsync);
    }

    private void OnFileReceived(TransferOffer offer, string path) => Dispatcher.UIThread.Post(async () =>
    {
        AddLog($"Received \"{offer.Title}\" from {offer.SenderName}: {path}");
        var peer = Peers.FirstOrDefault(p => p.Id == offer.SenderId);
        if (peer != null) peer.Activity = "Received ✓";
        if (AppServices.Main != null) await AppServices.Main.ReceivedBookAsync(path, offer.Title, offer.SenderName);
    });

    // ---------------------------------------------------------------- sync

    [RelayCommand]
    private void Lead()
    {
        AppServices.Sync.StartLeading();
        UpdateSyncStatus();
        AddLog("Started leading a sync session.");
    }

    [RelayCommand]
    private void StopSync()
    {
        AppServices.Sync.StopLeading();
        AppServices.Sync.StopFollowing();
        UpdateSyncStatus();
        AddLog("Sync stopped.");
    }

    [RelayCommand]
    private void Follow(PeerItemViewModel? peer)
    {
        if (peer == null) return;
        AppServices.Sync.Follow(peer.Id);
        UpdateSyncStatus();
        AddLog($"Following {peer.Name}.");
        AppServices.Main?.ShowPlayerCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();
}
