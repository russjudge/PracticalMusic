namespace PracticalMusic.Core.Networking;

public enum SyncAction
{
    /// <summary>Pause (or stay paused) at <see cref="SyncPlan.PositionMs"/>.</summary>
    Pause,
    /// <summary>Cue <see cref="SyncPlan.PositionMs"/> now and start playing after <see cref="SyncPlan.DelayMs"/>.</summary>
    StartAt,
    /// <summary>Play from <see cref="SyncPlan.PositionMs"/> right now (correct the position if already playing).</summary>
    PlayFrom
}

/// <summary>What a follower should do with a leader state.</summary>
public readonly record struct SyncPlan(SyncAction Action, double PositionMs, double DelayMs);

/// <summary>
/// Follower decision logic, kept free of UI and audio so it can be tested. Besides turning a leader
/// state into an action it bounds the result: a leader timestamp that converts to an absurd local time
/// (clocks not comparable, stale packet) is treated as "now" instead of being extrapolated, and
/// positions are clamped to the recording. This is what stops a follower from seeking to the end of
/// the song when something about the clock conversion is wrong.
/// </summary>
public static class SyncMath
{
    /// <summary>A playing leader publishes at least every second; an apparent packet age beyond this means the clocks are not comparable.</summary>
    public const double MaxCatchUpMs = 15_000;
    /// <summary>Leaders schedule starts <see cref="SyncService.StartLeadMs"/> ahead; a lead far beyond that means the clocks are not comparable.</summary>
    public const double MaxScheduleAheadMs = 5_000;

    /// <param name="state">The leader's state.</param>
    /// <param name="localAtMs">Local monotonic time corresponding to <see cref="SyncState.AtLeaderTimeMs"/>.</param>
    /// <param name="nowMs">Local monotonic time now.</param>
    /// <param name="durationMs">Recording length on this device (0 if unknown).</param>
    public static SyncPlan Plan(SyncState state, double localAtMs, double nowMs, double durationMs)
    {
        var position = Clamp(state.PositionMs, durationMs);
        if (!state.IsPlaying) return new SyncPlan(SyncAction.Pause, position, 0);

        var lead = localAtMs - nowMs;
        if (lead > 0)
        {
            // scheduled start; an absurd lead means the conversion is wrong, so use the leader's usual lead time
            if (lead > MaxScheduleAheadMs) lead = SyncService.StartLeadMs;
            return new SyncPlan(SyncAction.StartAt, position, lead);
        }

        var elapsed = -lead;
        if (elapsed > MaxCatchUpMs) elapsed = 0; // stale or untrustworthy timestamp: take the leader's position as sent
        return new SyncPlan(SyncAction.PlayFrom, Clamp(state.PositionMs + elapsed, durationMs), 0);
    }

    private static double Clamp(double positionMs, double durationMs) =>
        durationMs > 0 ? Math.Clamp(positionMs, 0, durationMs) : Math.Max(0, positionMs);
}
