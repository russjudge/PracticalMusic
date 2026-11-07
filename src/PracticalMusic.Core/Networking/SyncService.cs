using System.Net;
using PracticalMusic.Core.Audio;

namespace PracticalMusic.Core.Networking;

public enum SyncRole
{
    Off,
    Leader,
    Follower
}

/// <summary>
/// Synchronized playback. The leader broadcasts its playback state stamped with its own
/// monotonic clock; followers measure the clock offset to the leader with ping/pong round trips
/// and convert leader timestamps to local time. Starts are scheduled slightly in the future so
/// every device begins at the same instant.
/// Ping and pong are broadcast (addressed with <see cref="LanPacket.To"/>) rather than sent to a
/// peer's ephemeral port, so they pass a firewall that only opens the discovery port. Until the
/// first pong arrives, the leader's send time stamped on every sync packet gives a coarse offset
/// (accurate to the one-way network latency) so a follower never has to compare raw clocks.
/// </summary>
public sealed class SyncService : IDisposable
{
    /// <summary>How far ahead a leader schedules a start so followers can prepare.</summary>
    public const double StartLeadMs = 700;

    private readonly LanBus _bus;
    private readonly List<(double Offset, double Rtt)> _samples = new();
    private CancellationTokenSource? _pingCts;
    private long _seq;
    private double? _coarseOffsetMs;

    public SyncRole Role { get; private set; } = SyncRole.Off;
    public string SessionId { get; private set; } = "";
    public string? LeaderId { get; private set; }
    public SyncState? LastState { get; private set; }

    /// <summary>leader clock minus local clock, in ms. Valid when <see cref="ClockSynced"/>.</summary>
    public double ClockOffsetMs { get; private set; }
    public double RttMs { get; private set; }
    public bool ClockSynced => _samples.Count > 0;
    /// <summary>True when some estimate of the leader's clock exists (measured, or coarse from a sync packet).</summary>
    public bool HasClockEstimate => ClockSynced || _coarseOffsetMs.HasValue;
    public DateTime LastLeaderPacket { get; private set; }
    public DateTime FollowingSince { get; private set; }

    public PeerInfo? Leader => LeaderId == null ? null : _bus.FindPeer(LeaderId);

    /// <summary>Raised on a follower when the leader publishes state. The second argument is the local monotonic time that corresponds to <see cref="SyncState.AtLeaderTimeMs"/>.</summary>
    public event Action<SyncState, double>? StateReceived;
    public event Action? Changed;

    public SyncService(LanBus bus)
    {
        _bus = bus;
        _bus.PacketReceived += OnPacket;
    }

    public void StartLeading()
    {
        StopFollowing();
        Role = SyncRole.Leader;
        SessionId = Guid.NewGuid().ToString("N");
        _bus.Leading = true;
        _ = _bus.AnnounceAsync();
        Changed?.Invoke();
    }

    public void StopLeading()
    {
        if (Role != SyncRole.Leader) return;
        Role = SyncRole.Off;
        _bus.Leading = false;
        _ = _bus.AnnounceAsync();
        Changed?.Invoke();
    }

    public void Follow(string leaderId)
    {
        StopLeading();
        LeaderId = leaderId;
        Role = SyncRole.Follower;
        FollowingSince = DateTime.UtcNow;
        _samples.Clear();
        _coarseOffsetMs = null;
        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        _ = PingLoop(_pingCts.Token);
        Changed?.Invoke();
    }

    public void StopFollowing()
    {
        if (Role != SyncRole.Follower) return;
        _pingCts?.Cancel();
        _pingCts = null;
        Role = SyncRole.Off;
        LeaderId = null;
        LastState = null;
        _samples.Clear();
        _coarseOffsetMs = null;
        Changed?.Invoke();
    }

    public double LeaderNowMs => MonoClock.NowMs;

    /// <summary>
    /// Converts a leader timestamp to the local monotonic clock: with the measured offset once ping/pong
    /// succeeded, otherwise with the coarse estimate taken from the leader's packets. With no estimate at
    /// all the leader's time is treated as "now" - never as a raw clock value, because the two machines'
    /// monotonic clocks (time since boot) have nothing to do with each other.
    /// </summary>
    public double ToLocalMs(double leaderTimeMs) =>
        LocalTimeFor(leaderTimeMs, ClockSynced ? ClockOffsetMs : _coarseOffsetMs, MonoClock.NowMs);

    /// <summary>Pure form of <see cref="ToLocalMs"/>: <paramref name="offsetMs"/> is leader clock minus local clock, or null when unknown.</summary>
    public static double LocalTimeFor(double leaderTimeMs, double? offsetMs, double nowMs) =>
        offsetMs.HasValue ? leaderTimeMs - offsetMs.Value : nowMs;

    /// <summary>Leader: broadcasts the current playback state.</summary>
    public Task PublishAsync(SyncState state)
    {
        if (Role != SyncRole.Leader) return Task.CompletedTask;
        state.SessionId = SessionId;
        state.Seq = ++_seq;
        LastState = state;
        var packet = _bus.MakePacket("sync");
        packet.Sync = state;
        packet.T1 = MonoClock.NowMs; // send time: lets followers without a pong yet estimate the clock offset
        return _bus.BroadcastAsync(packet);
    }

    private async Task PingLoop(CancellationToken ct)
    {
        var burst = 5;
        while (!ct.IsCancellationRequested)
        {
            var leader = Leader;
            if (leader != null)
            {
                var p = _bus.MakePacket("ping");
                p.To = leader.Id;
                p.T0 = MonoClock.NowMs;
                await _bus.BroadcastAsync(p); // broadcast: only the discovery port has to be open on the leader
            }
            try { await Task.Delay(burst-- > 0 ? 250 : 2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnPacket(LanPacket packet, IPEndPoint from)
    {
        switch (packet.Type)
        {
            case "ping" when Role == SyncRole.Leader:
            {
                var reply = _bus.MakePacket("pong");
                reply.To = packet.Id;
                reply.T0 = packet.T0;
                reply.T1 = MonoClock.NowMs;
                _ = _bus.BroadcastAsync(reply); // broadcast: only the discovery port has to be open on the follower
                break;
            }
            case "pong" when Role == SyncRole.Follower && packet.Id == LeaderId:
            {
                var t2 = MonoClock.NowMs;
                var rtt = t2 - packet.T0;
                if (rtt < 0 || rtt > 5000) break;
                var offset = packet.T1 - (packet.T0 + t2) / 2;
                _samples.Add((offset, rtt));
                if (_samples.Count > 8) _samples.RemoveAt(0);
                var best = _samples.MinBy(s => s.Rtt);
                ClockOffsetMs = best.Offset;
                RttMs = best.Rtt;
                Changed?.Invoke();
                break;
            }
            case "sync" when Role == SyncRole.Follower && packet.Id == LeaderId && packet.Sync != null:
            {
                LastState = packet.Sync;
                LastLeaderPacket = DateTime.UtcNow;
                if (!ClockSynced && packet.T1 > 0) _coarseOffsetMs = packet.T1 - MonoClock.NowMs;
                StateReceived?.Invoke(packet.Sync, ToLocalMs(packet.Sync.AtLeaderTimeMs));
                break;
            }
            case "bye" when Role == SyncRole.Follower && packet.Id == LeaderId:
                StopFollowing();
                break;
        }
    }

    public void Dispose()
    {
        _bus.PacketReceived -= OnPacket;
        _pingCts?.Cancel();
    }
}
