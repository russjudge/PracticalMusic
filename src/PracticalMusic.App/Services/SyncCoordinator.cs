using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;

namespace PracticalMusic.App.Services;

public enum SyncTransport
{
    None,
    /// <summary>Following a device on the local network (UDP).</summary>
    Lan,
    /// <summary>Following someone through the website (browsers and apps anywhere).</summary>
    Online
}

/// <summary>
/// Synchronized playback over both channels. Leading publishes on the local network and through the website at
/// the same time, so app users on the Wi-Fi and browser users (iPhone, iPad …) follow the same leader. Following
/// uses whichever channel the leader was chosen from. The Player only talks to this class.
/// </summary>
public sealed class SyncCoordinator
{
    public SyncCoordinator(SyncService lan, OnlineSyncClient online)
    {
        Lan = lan;
        Online = online;
        lan.StateReceived += (state, localAt) => { if (lan.Role == SyncRole.Follower) StateReceived?.Invoke(state, localAt); };
        online.StateReceived += (state, localAt) => { if (online.Following) StateReceived?.Invoke(state, localAt); };
        lan.Changed += () => Changed?.Invoke();
        online.Changed += () => Changed?.Invoke();
    }

    public SyncService Lan { get; }
    public OnlineSyncClient Online { get; }

    public SyncRole Role =>
        Lan.Role == SyncRole.Leader || Online.Leading ? SyncRole.Leader :
        Lan.Role == SyncRole.Follower || Online.Following ? SyncRole.Follower : SyncRole.Off;

    public SyncTransport FollowTransport =>
        Lan.Role == SyncRole.Follower ? SyncTransport.Lan : Online.Following ? SyncTransport.Online : SyncTransport.None;

    public bool ClockSynced => FollowTransport == SyncTransport.Online ? Online.ClockSynced : Lan.ClockSynced;
    public double RttMs => FollowTransport == SyncTransport.Online ? Online.RttMs : Lan.RttMs;
    public double ClockOffsetMs => FollowTransport == SyncTransport.Online ? Online.ClockOffsetMs : Lan.ClockOffsetMs;
    public DateTime FollowingSince => FollowTransport == SyncTransport.Online ? Online.FollowingSince : Lan.FollowingSince;
    public string? LeaderName => FollowTransport == SyncTransport.Online ? Online.LeaderName ?? Online.Leader?.Name : Lan.Leader?.Name;

    /// <summary>A followed leader (on either channel) published its state; the second argument is the matching local monotonic time.</summary>
    public event Action<SyncState, double>? StateReceived;
    public event Action? Changed;

    /// <summary>Leads on the local network and - when connected - through the website.</summary>
    public void StartLeading()
    {
        Online.StopFollowing();
        Lan.StartLeading();
        Online.StartLeading();
    }

    public void StopLeading()
    {
        Lan.StopLeading();
        Online.StopLeading();
    }

    public void FollowLan(string peerId)
    {
        Online.StopLeading();
        Online.StopFollowing();
        Lan.Follow(peerId);
    }

    public void FollowOnline(string peerId)
    {
        Lan.StopLeading();
        Lan.StopFollowing();
        Online.Follow(peerId);
    }

    public void StopFollowing()
    {
        Lan.StopFollowing();
        Online.StopFollowing();
    }

    public Task PublishAsync(SyncState state)
    {
        var tasks = new List<Task>(2);
        if (Online.Leading) tasks.Add(Online.PublishAsync(Copy(state))); // converts to the website's clock
        if (Lan.Role == SyncRole.Leader) tasks.Add(Lan.PublishAsync(Copy(state)));
        return Task.WhenAll(tasks);
    }

    private static SyncState Copy(SyncState s) => new()
    {
        SessionId = s.SessionId, Seq = s.Seq, BookId = s.BookId, BookTitle = s.BookTitle, SongId = s.SongId, SongTitle = s.SongTitle,
        SongIndex = s.SongIndex, RecordingId = s.RecordingId, RecordingName = s.RecordingName, IsPlaying = s.IsPlaying,
        PositionMs = s.PositionMs, AtLeaderTimeMs = s.AtLeaderTimeMs
    };
}
