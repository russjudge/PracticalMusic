using PracticalMusic.Core.Models;
using PracticalMusic.Core.Timing;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class TimingEngineTests
{
    private static (Song Song, Recording Rec) MakeSong()
    {
        var song = new Song { Title = "Test", StartPage = 2, PageCount = 3, HighlightHeight = 0.08 };
        var rec = new Recording { Name = "Piano", DurationMs = 60000 };
        rec.PageTimings.Add(new PageTiming { PageOffset = 0, TimeMs = 0 });
        rec.PageTimings.Add(new PageTiming { PageOffset = 1, TimeMs = 20000 });
        rec.PageTimings.Add(new PageTiming { PageOffset = 2, TimeMs = 40000 });
        song.Recordings.Add(rec);
        return (song, rec);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19999, 0)]
    [InlineData(20000, 1)]
    [InlineData(39999, 1)]
    [InlineData(40000, 2)]
    [InlineData(99999, 2)]
    public void PageOffsetAt_follows_page_timings(double ms, int expected)
    {
        var (_, rec) = MakeSong();
        Assert.Equal(expected, TimingEngine.PageOffsetAt(rec, ms));
    }

    [Fact]
    public void First_page_always_starts_at_zero()
    {
        var rec = new Recording();
        Assert.Equal(0, TimingEngine.PageOffsetAt(rec, 12345));
        Assert.Equal(0, TimingEngine.PageStartTime(rec, 0));
        TimingEngine.SetPageStart(rec, 0, 5000); // ignored
        Assert.Empty(rec.PageTimings);
    }

    [Fact]
    public void SetPageStart_keeps_timings_monotonic()
    {
        var (_, rec) = MakeSong();
        TimingEngine.SetPageStart(rec, 1, 45000); // later than page 2 → page 2 is pushed
        Assert.Equal(45000, TimingEngine.PageStartTime(rec, 1));
        Assert.Equal(45000, TimingEngine.PageStartTime(rec, 2));

        TimingEngine.SetPageStart(rec, 2, 10000); // earlier than page 1 → page 1 pulled back
        Assert.Equal(10000, TimingEngine.PageStartTime(rec, 1));
        Assert.Equal(10000, TimingEngine.PageStartTime(rec, 2));
    }

    [Fact]
    public void Markers_define_current_and_next()
    {
        var (song, rec) = MakeSong();
        var a = TimingEngine.AddMarker(rec, 0, 0.1, 0.2, 1000);
        var b = TimingEngine.AddMarker(rec, 0, 0.5, 0.2, 5000);
        var c = TimingEngine.AddMarker(rec, 0, 0.1, 0.4, 9000);

        Assert.Null(TimingEngine.CurrentMarker(rec, 500));
        Assert.Same(a, TimingEngine.CurrentMarker(rec, 1000));
        Assert.Same(a, TimingEngine.CurrentMarker(rec, 4999));
        Assert.Same(b, TimingEngine.CurrentMarker(rec, 5000));
        Assert.Same(b, TimingEngine.MarkerAfter(rec, a));
        Assert.Same(c, TimingEngine.MarkerAfter(rec, b));
        Assert.Null(TimingEngine.MarkerAfter(rec, c));

        // highlight between a and b on the same line
        var hl = TimingEngine.HighlightAt(song, rec, 2000)!.Value;
        Assert.Equal(0, hl.PageOffset);
        Assert.Equal(0.1, hl.X1, 6);
        Assert.Equal(0.5, hl.X2, 6);
        Assert.Equal(0.08, hl.Height, 6);

        // b → c are on different lines: highlight runs to the right edge
        hl = TimingEngine.HighlightAt(song, rec, 6000)!.Value;
        Assert.Equal(0.5, hl.X1, 6);
        Assert.Equal(1.0, hl.X2, 6);
    }

    [Fact]
    public void TimeForTap_interpolates_on_the_same_line()
    {
        var (song, rec) = MakeSong();
        TimingEngine.AddMarker(rec, 0, 0.1, 0.2, 1000);
        TimingEngine.AddMarker(rec, 0, 0.5, 0.2, 5000);
        TimingEngine.AddMarker(rec, 0, 0.1, 0.4, 9000);

        Assert.Equal(3000, TimingEngine.TimeForTap(song, rec, 0, 0.3, 0.2), 1); // halfway between
        Assert.Equal(1000, TimingEngine.TimeForTap(song, rec, 0, 0.05, 0.2), 1); // before first marker of the line
        Assert.Equal(5000, TimingEngine.TimeForTap(song, rec, 0, 0.9, 0.2), 1); // after last marker of the line
        Assert.Equal(9000, TimingEngine.TimeForTap(song, rec, 0, 0.3, 0.41), 1); // second line
        Assert.Equal(20000, TimingEngine.TimeForTap(song, rec, 1, 0.3, 0.3), 1); // page without markers → page start
    }

    [Fact]
    public void VisiblePages_spreads()
    {
        Assert.Equal(new[] { 0 }, TimingEngine.VisiblePages(0, 3, twoPages: false));
        Assert.Equal(new[] { 2 }, TimingEngine.VisiblePages(2, 3, twoPages: false));
        Assert.Equal(new[] { 0, 1 }, TimingEngine.VisiblePages(0, 3, twoPages: true));
        Assert.Equal(new[] { 0, 1 }, TimingEngine.VisiblePages(1, 3, twoPages: true));
        Assert.Equal(new[] { 2 }, TimingEngine.VisiblePages(2, 3, twoPages: true));
        Assert.Equal(new[] { 2, 3 }, TimingEngine.VisiblePages(3, 4, twoPages: true));
    }

    [Fact]
    public void FormatTime_is_readable()
    {
        Assert.Equal("0:00.0", TimingEngine.FormatTime(0));
        Assert.Equal("1:05.3", TimingEngine.FormatTime(65300));
        Assert.Equal("1:01:01", TimingEngine.FormatTime(3661000));
    }
}

public class BookModelTests
{
    private static Book MakeBook()
    {
        var book = new Book { Title = "Book" };
        book.Documents.Add(new PdfDocumentRef { Name = "A", PageCount = 4 });
        book.Documents.Add(new PdfDocumentRef { Name = "B", PageCount = 3 });
        return book;
    }

    [Fact]
    public void ResolvePage_maps_across_documents()
    {
        var book = MakeBook();
        Assert.Equal(7, book.TotalPages);
        Assert.Equal(("A", 0), (book.ResolvePage(0)!.Value.Document.Name, book.ResolvePage(0)!.Value.PageIndex));
        Assert.Equal(("A", 3), (book.ResolvePage(3)!.Value.Document.Name, book.ResolvePage(3)!.Value.PageIndex));
        Assert.Equal(("B", 0), (book.ResolvePage(4)!.Value.Document.Name, book.ResolvePage(4)!.Value.PageIndex));
        Assert.Null(book.ResolvePage(7));
        Assert.Equal(4, book.FirstPageOf(book.Documents[1]));
    }

    [Fact]
    public void AddSongAt_splits_and_trims()
    {
        var book = MakeBook();
        var s1 = book.AddSongAt(0, "One");
        Assert.Equal(7, s1.PageCount);
        var s2 = book.AddSongAt(4, "Two");
        Assert.Equal(4, s1.PageCount);
        Assert.Equal(3, s2.PageCount);
        var s3 = book.AddSongAt(2, "Three");
        Assert.Equal(2, s1.PageCount);
        Assert.Equal(2, s3.PageCount);
        Assert.Equal(3, s2.PageCount);
        Assert.Equal(new[] { "One", "Three", "Two" }, book.SongsInPageOrder.Select(s => s.Title));
        Assert.Same(s3, book.SongAtPage(3));
        Assert.Same(s2, book.SongStartingAt(4));
    }

    [Fact]
    public void NormalizeSongs_clamps_to_book()
    {
        var book = MakeBook();
        book.Songs.Add(new Song { Title = "X", StartPage = 5, PageCount = 10 });
        book.Songs.Add(new Song { Title = "Y", StartPage = 1, PageCount = 10 });
        book.NormalizeSongs();
        Assert.Equal("Y", book.Songs[0].Title);
        Assert.Equal(4, book.Songs[0].PageCount); // pages 1..4
        Assert.Equal(2, book.Songs[1].PageCount); // pages 5..6
    }

    [Fact]
    public void Json_round_trip_preserves_everything()
    {
        var book = MakeBook();
        var song = book.AddSongAt(0, "One");
        var rec = new Recording { Name = "Vocals", Source = MediaSource.FromUrl("https://example.com/a.mp3"), DurationMs = 1234 };
        rec.PageTimings.Add(new PageTiming { PageOffset = 1, TimeMs = 1000 });
        TimingEngine.AddMarker(rec, 1, 0.25, 0.5, 1500, "chorus");
        song.Recordings.Add(rec);

        var clone = BookJson.Clone(book);
        Assert.Equal(book.Id, clone.Id);
        Assert.Equal(2, clone.Documents.Count);
        var r2 = clone.Songs[0].Recordings[0];
        Assert.Equal(MediaSourceKind.Url, r2.Source.Kind);
        Assert.Equal("a.mp3", r2.Source.FileName);
        Assert.Equal(1000, r2.PageTimings.Single(t => t.PageOffset == 1).TimeMs);
        Assert.Equal("chorus", r2.Markers.Single().Label);
        Assert.Equal(0.25, r2.Markers.Single().X);
    }
}
