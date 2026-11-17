using PracticalMusic.Core.Networking;

namespace PracticalMusic.Core.Online;

/// <summary>
/// Messages on the website's synchronized-playback WebSocket (<c>/ws/sync</c>). One class with optional
/// fields, like <see cref="LanPacket"/>, keeps the protocol simple for the browser client too.
///
/// Browsers cannot use the LAN protocol (UDP broadcast, raw TCP), so the website relays sync between
/// everyone who is connected to the same organization: browsers and apps alike. Times are measured on
/// the server's monotonic clock: every client estimates "server clock minus local clock" with ping/pong
/// round trips, a leader converts its local times to server time before publishing, and followers convert
/// them back to their own clock. <see cref="SyncState.AtLeaderTimeMs"/> therefore carries server time on
/// this channel.
///
/// Client → server: hello (first message; joins an organization), book, ping, lead, unlead, follow, unfollow, state.
/// Server → client: welcome, peers, pong, state, leaderGone, error.
/// </summary>
public sealed class SyncWireMessage
{
    public const int ProtocolVersion = 1;

    public string Type { get; set; } = "";

    // hello
    public long? OrgId { get; set; }
    public string? Name { get; set; }
    public string? Platform { get; set; }
    public int? Protocol { get; set; }

    // hello / book: what the sender has open
    public long? ServerBookId { get; set; }
    public string? BookUid { get; set; }
    public string? BookTitle { get; set; }

    // welcome
    public string? ConnectionId { get; set; }

    // ping / pong: client send time (client clock) and server time (server clock)
    public double? T0 { get; set; }
    public double? T1 { get; set; }

    // follow / state / leaderGone
    public string? LeaderId { get; set; }
    public SyncState? State { get; set; }

    /// <summary>Server clock when the server sent the message: a coarse clock estimate until the first pong arrives.</summary>
    public double? ServerTimeMs { get; set; }

    public List<OnlinePeer>? Peers { get; set; }

    // error
    public string? Message { get; set; }
}

/// <summary>Someone connected to the same organization through the website (a browser or an app).</summary>
public sealed class OnlinePeer
{
    public string Id { get; set; } = "";
    /// <summary>Device name chosen by the user.</summary>
    public string Name { get; set; } = "";
    /// <summary>Full name of the signed-in user.</summary>
    public string UserName { get; set; } = "";
    /// <summary>"Browser", "Windows", "Linux", "Android" …</summary>
    public string Platform { get; set; } = "";
    public long? ServerBookId { get; set; }
    public string? BookUid { get; set; }
    public string? BookTitle { get; set; }
    public bool Leading { get; set; }
}

/// <summary>
/// Estimates the offset between a remote clock and the local clock from ping/pong round trips: the sample
/// with the shortest round trip wins (its one-way delays are the most symmetric). Same math as the LAN
/// <see cref="SyncService"/>; the browser client implements the identical algorithm.
/// </summary>
public sealed class ClockOffsetEstimator
{
    private readonly List<(double Offset, double Rtt)> _samples = new();
    private readonly object _gate = new();
    private double? _coarse;

    /// <summary>Remote clock minus local clock, in ms (valid when <see cref="IsSynced"/>).</summary>
    public double OffsetMs { get; private set; }
    public double RttMs { get; private set; }
    public bool IsSynced { get { lock (_gate) return _samples.Count > 0; } }
    public bool HasEstimate { get { lock (_gate) return _samples.Count > 0 || _coarse.HasValue; } }

    /// <summary>Adds a round trip: <paramref name="t0"/> and <paramref name="t2"/> on the local clock, <paramref name="t1"/> on the remote clock. Returns false for implausible samples.</summary>
    public bool AddSample(double t0, double t1, double t2)
    {
        var rtt = t2 - t0;
        if (rtt < 0 || rtt > 5000) return false;
        lock (_gate)
        {
            _samples.Add((t1 - (t0 + t2) / 2, rtt));
            if (_samples.Count > 8) _samples.RemoveAt(0);
            var best = _samples.MinBy(s => s.Rtt);
            OffsetMs = best.Offset;
            RttMs = best.Rtt;
        }
        return true;
    }

    /// <summary>Remote time stamped on a message as it left: accurate to the one-way latency. Used until the first real sample.</summary>
    public void AddCoarse(double remoteNowMs, double localNowMs)
    {
        lock (_gate) _coarse = remoteNowMs - localNowMs;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _coarse = null;
            OffsetMs = 0;
            RttMs = 0;
        }
    }

    /// <summary>Remote clock offset to use right now, or null when nothing is known yet.</summary>
    public double? CurrentOffset
    {
        get { lock (_gate) return _samples.Count > 0 ? OffsetMs : _coarse; }
    }

    /// <summary>Remote time → local time. With no estimate at all the remote time is treated as "now", never compared raw.</summary>
    public double ToLocal(double remoteMs, double localNowMs) =>
        CurrentOffset is { } o ? remoteMs - o : localNowMs;

    /// <summary>Local time → remote time (null when nothing is known yet).</summary>
    public double? ToRemote(double localMs) => CurrentOffset is { } o ? localMs + o : null;
}
