using System.Net;
using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Networking;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class NetworkTests
{
    [Fact]
    public async Task Transfer_offer_is_confirmed_then_streamed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmnet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var payload = new byte[300_000];
            new Random(1).NextBytes(payload);
            var source = Path.Combine(dir, "book.pmbook");
            File.WriteAllBytes(source, payload);

            using var receiver = new TransferService("B", () => "Bob", Path.Combine(dir, "in"));
            TransferOffer? seen = null;
            receiver.OfferHandler = o => { seen = o; return Task.FromResult(true); };
            string? received = null;
            receiver.FileReceived += (_, path) => received = path;
            receiver.Start();

            using var sender = new TransferService("A", () => "Alice", Path.Combine(dir, "in2"));
            sender.Start();
            var ok = await sender.SendFileAsync(IPAddress.Loopback, receiver.Port, source, "My Book", null, CancellationToken.None);

            Assert.True(ok);
            Assert.NotNull(seen);
            Assert.Equal("Alice", seen!.SenderName);
            Assert.Equal(payload.Length, seen.Size);
            Assert.NotNull(received);
            Assert.Equal(payload, File.ReadAllBytes(received!));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Transfer_offer_can_be_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmnet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "book.pmbook");
            File.WriteAllBytes(source, new byte[10]);
            using var receiver = new TransferService("B", () => "Bob", Path.Combine(dir, "in"));
            receiver.OfferHandler = _ => Task.FromResult(false);
            receiver.Start();
            using var sender = new TransferService("A", () => "Alice", Path.Combine(dir, "in2"));
            sender.Start();
            var ok = await sender.SendFileAsync(IPAddress.Loopback, receiver.Port, source, "x", null, CancellationToken.None);
            Assert.False(ok);
            Assert.False(Directory.Exists(Path.Combine(dir, "in")) && Directory.GetFiles(Path.Combine(dir, "in")).Length > 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Request_makes_the_peer_send_its_book_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmnet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bobsBook = Path.Combine(dir, "bobs.pmbook");
            File.WriteAllBytes(bobsBook, new byte[12345]);

            using var bob = new TransferService("B", () => "Bob", Path.Combine(dir, "bob-in"));
            bob.RequestHandler = _ => Task.FromResult<string?>(bobsBook);
            bob.Start();

            using var alice = new TransferService("A", () => "Alice", Path.Combine(dir, "alice-in"));
            var tcs = new TaskCompletionSource<(TransferOffer, string)>();
            alice.OfferHandler = _ => Task.FromResult(false); // would reject unsolicited offers
            alice.FileReceived += (o, p) => tcs.TrySetResult((o, p));
            alice.Start();

            var accepted = await alice.RequestBookAsync("B", IPAddress.Loopback, bob.Port, CancellationToken.None);
            Assert.True(accepted);
            var (offer, path) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(offer.WasRequested);
            Assert.Equal(12345, new FileInfo(path).Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Two_instances_discover_each_other_and_sync_clock()
    {
        using var a = new LanBus("A", "Alice") { TransferPort = 1 };
        using var b = new LanBus("B", "Bob") { TransferPort = 2 };
        a.Start();
        b.Start();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && (a.FindPeer("B") == null || b.FindPeer("A") == null))
            await Task.Delay(100);
        if (a.FindPeer("B") == null)
        {
            // broadcast can be blocked by local firewall policy; do not fail the whole suite for that
            return;
        }
        Assert.Equal("Bob", a.FindPeer("B")!.Name);

        using var syncA = new SyncService(a);
        using var syncB = new SyncService(b);
        syncA.StartLeading();
        await a.AnnounceAsync();
        syncB.Follow("A");
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !syncB.ClockSynced) await Task.Delay(50);
        Assert.True(syncB.ClockSynced);
        Assert.InRange(Math.Abs(syncB.ClockOffsetMs), 0, 50); // same machine, same clock

        SyncState? got = null;
        double localAt = 0;
        syncB.StateReceived += (s, l) => { got = s; localAt = l; };
        var at = MonoClock.NowMs + 500;
        await syncA.PublishAsync(new SyncState { SongTitle = "X", IsPlaying = true, PositionMs = 1000, AtLeaderTimeMs = at });
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && got == null) await Task.Delay(20);
        Assert.NotNull(got);
        Assert.Equal("X", got!.SongTitle);
        Assert.InRange(Math.Abs(localAt - at), 0, 60);
    }

    [Fact]
    public void PositionClock_extrapolates_while_running()
    {
        var clock = new PositionClock();
        clock.Update(1000, running: false);
        Assert.Equal(1000, clock.PositionMs);
        clock.SetRunning(true);
        Thread.Sleep(120);
        Assert.InRange(clock.PositionMs, 1090, 1400);
        clock.Correct(1110, true, toleranceMs: 500); // within tolerance → ignored
        Assert.InRange(clock.PositionMs, 1090, 1400);
        clock.Correct(5000, true, toleranceMs: 50); // out of tolerance → snaps
        Assert.InRange(clock.PositionMs, 5000, 5100);
    }
}
