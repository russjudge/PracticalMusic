using PracticalMusic.Core.Networking;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class SyncAndPortTests
{
    // A port range nobody else uses on a developer machine (the running app sits on 47313).
    private const int TestPort = 47913;

    [Fact]
    public void Transfer_listener_prefers_the_fixed_port_and_steps_to_the_next_one_when_busy()
    {
        Assert.Equal(47313, TransferService.DefaultPort);
        var dir = Path.Combine(Path.GetTempPath(), "pmport_" + Guid.NewGuid().ToString("N"));
        using var first = new TransferService("A", () => "A", dir);
        using var second = new TransferService("B", () => "B", dir);
        using var any = new TransferService("C", () => "C", dir);
        try
        {
            first.Start(TestPort);
            Assert.Equal(TestPort, first.PreferredPort);
            Assert.InRange(first.Port, TestPort, TestPort + TransferService.PortAttempts - 1);

            second.Start(TestPort);
            Assert.NotEqual(first.Port, second.Port);
            Assert.InRange(second.Port, TestPort, TestPort + TransferService.PortAttempts - 1);
            if (first.Port == TestPort) Assert.Equal(TestPort + 1, second.Port);

            any.Start(0);
            Assert.Equal(0, any.PreferredPort);
            Assert.True(any.Port > 0);
            Assert.NotEqual(first.Port, any.Port);
            Assert.NotEqual(second.Port, any.Port);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Follower_pauses_at_the_leader_position()
    {
        var plan = SyncMath.Plan(new SyncState { IsPlaying = false, PositionMs = 12_000 }, localAtMs: 1000, nowMs: 1000, durationMs: 200_000);
        Assert.Equal(SyncAction.Pause, plan.Action);
        Assert.Equal(12_000, plan.PositionMs);
    }

    [Fact]
    public void Follower_schedules_a_start_that_lies_in_the_near_future()
    {
        var plan = SyncMath.Plan(new SyncState { IsPlaying = true, PositionMs = 5_000 }, localAtMs: 10_650, nowMs: 10_000, durationMs: 200_000);
        Assert.Equal(SyncAction.StartAt, plan.Action);
        Assert.Equal(5_000, plan.PositionMs);
        Assert.Equal(650, plan.DelayMs);
    }

    [Fact]
    public void Follower_catches_up_by_the_time_that_passed_since_the_leader_stamped_the_state()
    {
        var plan = SyncMath.Plan(new SyncState { IsPlaying = true, PositionMs = 5_000 }, localAtMs: 10_000, nowMs: 10_400, durationMs: 200_000);
        Assert.Equal(SyncAction.PlayFrom, plan.Action);
        Assert.Equal(5_400, plan.PositionMs);
    }

    [Fact]
    public void Incomparable_clocks_never_send_the_follower_to_the_end_of_the_song()
    {
        // The defect: a Windows PC up for days against a laptop up for an hour; without a clock offset the
        // "elapsed" time came out as days and the seek clamped to the end of the recording.
        var threeDaysMs = 3 * 24 * 3600 * 1000.0;
        var plan = SyncMath.Plan(new SyncState { IsPlaying = true, PositionMs = 5_000 }, localAtMs: 1_000, nowMs: 1_000 + threeDaysMs, durationMs: 200_000);
        Assert.Equal(SyncAction.PlayFrom, plan.Action);
        Assert.Equal(5_000, plan.PositionMs);

        // and the mirror image (leader clock far ahead) does not park the follower forever
        plan = SyncMath.Plan(new SyncState { IsPlaying = true, PositionMs = 5_000 }, localAtMs: 1_000 + threeDaysMs, nowMs: 1_000, durationMs: 200_000);
        Assert.Equal(SyncAction.StartAt, plan.Action);
        Assert.Equal(SyncService.StartLeadMs, plan.DelayMs);
    }

    [Fact]
    public void Positions_are_clamped_to_the_recording()
    {
        var plan = SyncMath.Plan(new SyncState { IsPlaying = true, PositionMs = 199_900 }, localAtMs: 0, nowMs: 2_000, durationMs: 200_000);
        Assert.Equal(200_000, plan.PositionMs);
        plan = SyncMath.Plan(new SyncState { IsPlaying = false, PositionMs = -5 }, localAtMs: 0, nowMs: 0, durationMs: 0);
        Assert.Equal(0, plan.PositionMs);
    }

    [Fact]
    public void Leader_time_is_converted_with_the_best_known_offset_or_treated_as_now()
    {
        Assert.Equal(9_300, SyncService.LocalTimeFor(leaderTimeMs: 10_000, offsetMs: 700, nowMs: 123));   // measured or coarse offset
        Assert.Equal(123, SyncService.LocalTimeFor(leaderTimeMs: 10_000, offsetMs: null, nowMs: 123));    // nothing known: "now", not the raw value
    }

    [Fact]
    public void Packets_addressed_to_another_instance_carry_the_target_id()
    {
        var bus = new LanBus("me", "Me");
        var p = bus.MakePacket("ping");
        Assert.Null(p.To);
        p.To = "leader";
        Assert.Equal("leader", p.To);
        Assert.Equal("me", p.Id);
    }
}
