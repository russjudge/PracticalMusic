using System.IO.Compression;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class OnlineTests
{
    private static Book MakeBook()
    {
        var book = new Book { Title = "Sunday" };
        book.Documents.Add(new PdfDocumentRef { Id = "d", Name = "Charts", PageCount = 3, Source = new MediaSource { Kind = MediaSourceKind.File, Path = "/a/Charts.pdf", FileName = "Charts.pdf", SizeBytes = 100 } });
        var rec = new Recording { Id = "r", Name = "Band", Source = new MediaSource { Kind = MediaSourceKind.File, Path = "/a/x.mp3", FileName = "x.mp3", SizeBytes = 200 }, DurationMs = 1000 };
        rec.PageTimings.Add(new PageTiming { PageOffset = 0, TimeMs = 0 });
        rec.PageTimings.Add(new PageTiming { PageOffset = 1, TimeMs = 30000 });
        rec.Markers.Add(new TimeMarker { Id = "m", PageOffset = 0, TimeMs = 1000, X = 0.2, Y = 0.3 });
        book.Songs.Add(new Song { Id = "s", Title = "Grace", StartPage = 0, PageCount = 2, Recordings = { rec } });
        return book;
    }

    [Fact]
    public void Fingerprint_ignores_where_files_live_and_what_is_measured_while_playing()
    {
        var a = MakeBook();
        var b = BookJson.Clone(a);
        b.Modified = DateTimeOffset.Now.AddDays(-3);
        b.Origin = new OnlineOrigin { Server = "https://x", BookId = 5, Revision = 2 };
        foreach (var src in b.AllMediaSources())
        {
            src.Kind = MediaSourceKind.Embedded;
            src.EntryName = "media/" + src.FileName;
            src.Path = null;
        }
        b.Songs[0].Recordings[0].DurationMs = 123456; // measured when played
        Assert.Equal(BookFingerprint.Compute(a), BookFingerprint.Compute(b));
    }

    [Fact]
    public void Fingerprint_changes_with_the_music()
    {
        var baseline = BookFingerprint.Compute(MakeBook());
        var edits = new Action<Book>[]
        {
            b => b.Title = "Monday",
            b => b.Songs[0].Title = "Other",
            b => b.Songs[0].PageCount = 3,
            b => b.Songs[0].HighlightHeight = 0.1,
            b => b.Songs[0].Recordings[0].Markers[0].X = 0.25,
            b => b.Songs[0].Recordings[0].PageTimings[1].TimeMs = 30001,
            b => b.Songs[0].Recordings[0].Name = "Piano",
            b => b.Documents[0].Source.SizeBytes = 101,
            b => b.Songs[0].Recordings.Add(new Recording { Id = "r2", Name = "Vocals" })
        };
        foreach (var edit in edits)
        {
            var book = MakeBook();
            edit(book);
            Assert.NotEqual(baseline, BookFingerprint.Compute(book));
        }
    }

    [Fact]
    public void Local_changes_are_detected_against_the_origin()
    {
        var book = MakeBook();
        Assert.False(BookFingerprint.HasLocalChanges(book)); // no origin
        book.Origin = new OnlineOrigin { Fingerprint = BookFingerprint.Compute(book) };
        Assert.False(BookFingerprint.HasLocalChanges(book));
        book.Songs[0].Title = "Changed";
        Assert.True(BookFingerprint.HasLocalChanges(book));
    }

    [Fact]
    public void Origin_survives_saving_and_older_files_still_load()
    {
        var book = MakeBook();
        book.Origin = new OnlineOrigin { Server = "https://practicalmusic.example.com", OrgId = 3, OrgName = "Grace", BookId = 42, Revision = 7, Fingerprint = "abc" };
        var copy = BookJson.Deserialize(BookJson.Serialize(book));
        Assert.Equal(42, copy.Origin!.BookId);
        Assert.Equal(7, copy.Origin.Revision);
        Assert.Null(BookJson.Deserialize(BookJson.Serialize(MakeBook())).Origin);
        Assert.DoesNotContain("origin", BookJson.Serialize(MakeBook())); // nothing new in books that were never online
    }

    [Theory]
    [InlineData("practicalmusic.russjudge.com", "https://practicalmusic.russjudge.com")]
    [InlineData("https://PracticalMusic.RussJudge.com/", "https://practicalmusic.russjudge.com")]
    [InlineData("http://localhost:5080", "http://localhost:5080")]
    [InlineData("http://192.168.1.20:5080/", "http://192.168.1.20:5080")]
    public void Server_addresses_are_normalized(string input, string expected)
    {
        Assert.True(OnlineClient.TryNormalizeServer(input, out var url, out _));
        Assert.Equal(expected, url);
        Assert.True(new OnlineOrigin { Server = expected }.IsServer(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://practicalmusic.example.com")] // passwords must not travel unencrypted over the internet
    [InlineData("ftp://example.com")]
    public void Unsafe_or_invalid_server_addresses_are_refused(string input)
    {
        Assert.False(OnlineClient.TryNormalizeServer(input, out _, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Sync_socket_address_follows_the_website_scheme()
    {
        using var secure = new OnlineClient("https://pm.example.com");
        Assert.Equal("wss://pm.example.com/ws/sync", secure.SyncSocketUri.ToString());
        using var local = new OnlineClient("http://localhost:5080");
        Assert.Equal("ws://localhost:5080/ws/sync", local.SyncSocketUri.ToString());
    }

    [Fact]
    public void Clock_estimator_prefers_the_fastest_round_trip()
    {
        var c = new ClockOffsetEstimator();
        Assert.Equal(42, c.ToLocal(999_999, 42)); // unknown: "now", never a raw comparison
        Assert.Null(c.ToRemote(1));
        c.AddCoarse(remoteNowMs: 5000, localNowMs: 1000);
        Assert.Equal(1000, c.ToLocal(5000, 0));
        Assert.True(c.AddSample(100, 1100, 120));   // rtt 20 → offset 990
        Assert.True(c.AddSample(200, 1300, 300));   // rtt 100 → offset 1050, worse
        Assert.Equal(990, c.OffsetMs);
        Assert.Equal(20, c.RttMs);
        Assert.Equal(1010, c.ToLocal(2000, 0));
        Assert.Equal(1000, c.ToRemote(10));
        Assert.False(c.AddSample(100, 0, 50));      // negative round trip
        Assert.False(c.AddSample(0, 0, 6000));      // too slow to trust
    }

    [Fact]
    public async Task Package_entries_cannot_write_outside_the_cache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmslip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var book = new Book { Title = "evil" };
            book.Documents.Add(new PdfDocumentRef { Name = "x", PageCount = 1, Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "../../escaped.txt", FileName = "escaped.txt" } });
            var package = Path.Combine(dir, "evil.pmbook");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                await using (var w = new StreamWriter(zip.CreateEntry("book.json").Open())) await w.WriteAsync(BookJson.Serialize(book));
                await using (var w = new StreamWriter(zip.CreateEntry("../../escaped.txt").Open())) await w.WriteAsync("gotcha");
            }
            var cache = Path.Combine(dir, "a", "b", "cache");
            var loaded = await BookPackage.LoadAsync(package, cache);
            Assert.False(File.Exists(Path.Combine(dir, "a", "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "escaped.txt")));
            Assert.False(loaded.Documents[0].Source.IsResolved);

            Assert.Null(BookPackage.SafeCachePath(cache, "../x"));
            Assert.Null(BookPackage.SafeCachePath(cache, "media/../../x"));
            Assert.StartsWith(Path.GetFullPath(cache), BookPackage.SafeCachePath(cache, "/etc/passwd")!); // rooted names stay inside
            Assert.Equal(Path.Combine(Path.GetFullPath(cache), "media", "song.mp3"), BookPackage.SafeCachePath(cache, "media/song.mp3"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task A_crafted_book_id_cannot_move_the_cache_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmslip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // "id": ".." would otherwise mean the folder above the cache (where the apps keep their settings)
            var book = new Book { Id = "..", Title = "evil" };
            book.Documents.Add(new PdfDocumentRef { Name = "x", PageCount = 1, Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "settings.json", FileName = "settings.json" } });
            var package = Path.Combine(dir, "evil.pmbook");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                await using (var w = new StreamWriter(zip.CreateEntry("book.json").Open())) await w.WriteAsync(BookJson.Serialize(book));
                await using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open())) await w.WriteAsync("{\"gotcha\":true}");
            }
            var cache = Path.Combine(dir, "app", "cache");
            var loaded = await BookPackage.LoadAsync(package, cache);
            Assert.False(File.Exists(Path.Combine(dir, "app", "settings.json")));
            var extracted = loaded.Documents[0].Source.ResolvedPath!;
            Assert.StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, Path.GetFullPath(extracted));

            Assert.Equal(Path.Combine(cache, "0123456789abcdef0123456789abcdef"), BookPackage.BookCacheFolder(cache, "0123456789abcdef0123456789abcdef"));
            foreach (var id in new[] { ".", "..", "a/../..", "CON", new string('x', 300), "" })
                Assert.StartsWith("id-", Path.GetFileName(BookPackage.BookCacheFolder(cache, id)));
            Assert.Equal("file", BookPackage.Sanitize(".."));
            Assert.Equal("file", BookPackage.Sanitize(" . "));
            Assert.Equal("a..b", BookPackage.Sanitize("a..b"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
