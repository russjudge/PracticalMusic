using PracticalMusic.Core.Audio;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Networking;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;
using Xunit;

namespace PracticalMusic.Web.Tests;

/// <summary>The desktop/Android apps' own client code (PracticalMusic.Core.Online) against the real website, in-process.</summary>
[Collection("web")]
public sealed class AppClientTests(WebFixture web)
{
    private OnlineClient Client(string? token = null) => new("https://pm.test", token, web.Server.CreateHandler());

    [Fact]
    public async Task The_app_signs_in_downloads_and_uploads_with_conflict_detection()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync(name: "Morgan");
        await web.AddMemberAsync(manager.Id, org.Id, "manager");

        using var app = Client();
        var login = await app.LoginAsync(new LoginRequest { Email = manager.Email, Password = "correct horse battery", DeviceName = "Morgan's PC", Platform = "Windows" });
        Assert.False(string.IsNullOrEmpty(login.Token));
        Assert.True(Assert.Single(login.User.Organizations).CanEdit);
        var me = await app.GetMeAsync();
        Assert.Equal(manager.Email, me.Email);

        // a book made in the app
        var dir = Path.Combine(web.DataDir, "appclient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pdf = Path.Combine(dir, "Set.pdf");
        await File.WriteAllBytesAsync(pdf, WebFixture.MinimalPdf(2));
        var book = new Book { Title = "Set list" };
        book.Documents.Add(new PdfDocumentRef { Name = "Set", PageCount = 2, Source = MediaSource.FromFile(pdf) });
        book.Songs.Add(new Song { Title = "Opening", StartPage = 0, PageCount = 2 });
        var package = Path.Combine(dir, "set.pmbook");
        await BookPackage.SaveAsync(book, package, embedMedia: true);

        var progress = new List<double>();
        var uploaded = await app.UploadPackageAsync(org.Id, package, null, progress: new Progress<double>(progress.Add));
        Assert.True(uploaded.Created);
        Assert.Equal(book.Id, uploaded.Uid);

        var list = await app.GetBooksAsync(org.Id);
        var summary = Assert.Single(list);
        Assert.Equal("Set list", summary.Title);

        // download → the book carries its origin
        var downloaded = Path.Combine(dir, "down.pmbook");
        Assert.Equal(1, await app.DownloadPackageAsync(summary.Id, downloaded));
        var copy = await BookPackage.LoadAsync(downloaded, Path.Combine(dir, "cache"));
        Assert.Equal(summary.Id, copy.Origin!.BookId);
        Assert.False(BookFingerprint.HasLocalChanges(copy));

        // someone changes the book on the website (revision 2)…
        copy.Songs[0].Title = "Opening (new key)";
        var edited = Path.Combine(dir, "edited.pmbook");
        await BookPackage.SaveAsync(copy, edited, embedMedia: true);
        var second = await app.UploadPackageAsync(org.Id, edited, baseRevision: 1);
        Assert.Equal(2, second.Revision);

        // …so an upload of a copy based on revision 1 is refused, unless forced
        var conflict = await Assert.ThrowsAsync<OnlineException>(() => app.UploadPackageAsync(org.Id, edited, baseRevision: 1));
        Assert.True(conflict.IsConflict);
        Assert.Equal(2, conflict.CurrentRevision);
        Assert.Contains("revision 2", conflict.Message);
        var summary2 = await app.GetBookSummaryAsync(summary.Id);
        Assert.Equal(2, summary2.Revision);

        // signing out revokes the token
        await app.LogoutAsync();
        using var stale = Client(login.Token);
        var gone = await Assert.ThrowsAsync<OnlineException>(() => stale.GetMeAsync());
        Assert.True(gone.IsUnauthorized);
    }

    [Fact]
    public async Task Wrong_passwords_give_a_readable_error()
    {
        using var app = Client();
        var ex = await Assert.ThrowsAsync<OnlineException>(() => app.LoginAsync(new LoginRequest { Email = "nobody@example.com", Password = "nope" }));
        Assert.True(ex.IsUnauthorized);
        Assert.Contains("not correct", ex.Message);
    }

    [Fact]
    public async Task Two_apps_play_in_sync_through_the_website()
    {
        var org = await web.CreateOrgAsync();
        var a = await web.CreateUserAsync(name: "Ann");
        var b = await web.CreateUserAsync(name: "Ben");
        await web.AddMemberAsync(a.Id, org.Id, "member");
        await web.AddMemberAsync(b.Id, org.Id, "member");

        async Task<OnlineSyncClient> ConnectAsync(string email, string name)
        {
            using var c = Client();
            var login = await c.LoginAsync(new LoginRequest { Email = email, Password = "correct horse battery" });
            var sync = new OnlineSyncClient(name, "Tests")
            {
                BookInfoProvider = () => (null, "book-uid", "Sunday"),
                Connector = async (uri, token, ct) =>
                {
                    var ws = web.Server.CreateWebSocketClient();
                    ws.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
                    return await ws.ConnectAsync(new Uri(web.Server.BaseAddress, "ws/sync"), ct);
                }
            };
            sync.Start(new Uri("wss://pm.test/ws/sync"), login.Token, org.Id);
            return sync;
        }

        static async Task WaitAsync(Func<bool> condition, string what)
        {
            for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
            Assert.True(condition(), what);
        }

        using var leader = await ConnectAsync(a.Email, "Ann's laptop");
        using var follower = await ConnectAsync(b.Email, "Ben's tablet");
        await WaitAsync(() => leader.IsConnected && follower.IsConnected, "both connected");
        await WaitAsync(() => follower.Peers.Any(p => p.Name == "Ann's laptop"), "the follower sees the leader");
        await WaitAsync(() => leader.ClockSynced && follower.ClockSynced, "clocks measured");

        leader.StartLeading();
        await WaitAsync(() => follower.Peers.Any(p => p.Name == "Ann's laptop" && p.Leading), "leader announced");
        var annId = follower.Peers.Single(p => p.Name == "Ann's laptop").Id;
        SyncState? received = null;
        double receivedLocalAt = 0;
        follower.StateReceived += (s, at) => { received = s; receivedLocalAt = at; };
        follower.Follow(annId);
        await Task.Delay(100);

        // a start scheduled 700 ms ahead on the leader's clock arrives as about 700 ms ahead on the follower's
        var at = MonoClock.NowMs + 700;
        await leader.PublishAsync(new SyncState { BookId = "book-uid", SongTitle = "Opening", IsPlaying = true, PositionMs = 0, AtLeaderTimeMs = at });
        await WaitAsync(() => received != null, "state relayed");
        Assert.Equal("Opening", received!.SongTitle);
        // both clients share this process' clock, so the conversion must land on the same instant (within the round trip)
        Assert.InRange(receivedLocalAt, at - 100, at + 100);

        leader.StopLeading();
        await WaitAsync(() => !follower.Following, "the follower notices that the leader stopped");
    }
}
