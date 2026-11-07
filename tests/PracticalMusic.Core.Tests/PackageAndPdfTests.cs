using PracticalMusic.Core.Models;
using PracticalMusic.Core.Packaging;
using PracticalMusic.Core.Pdf;
using PracticalMusic.Core.Timing;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class PackageAndPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pmtests_" + Guid.NewGuid().ToString("N"));

    public PackageAndPdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>A tiny single-page PDF written by hand (no external library needed).</summary>
    public static string WriteMinimalPdf(string path, int pages = 2)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // placeholder for pages
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var kids = "";
        for (var i = 0; i < pages; i++)
        {
            var content = $"BT /F1 24 Tf 72 700 Td (Page {i + 1}) Tj ET 72 600 m 540 600 l S";
            kids += $"{objects.Count + 1} 0 R ";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>";

        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    [Fact]
    public void Pdfium_renders_pages_with_correct_aspect()
    {
        var pdf = WriteMinimalPdf(Path.Combine(_dir, "a.pdf"), pages: 3);
        using var doc = PdfDocument.Load(pdf);
        Assert.Equal(3, doc.PageCount);
        var (w, h) = doc.GetPageSize(0);
        Assert.Equal(612, w, 1);
        Assert.Equal(792, h, 1);

        var img = doc.Render(1, 300);
        Assert.Equal(300, img.Width);
        Assert.Equal(388, img.Height); // 300 * 792 / 612
        Assert.Equal(img.Width * img.Height * 4, img.Pixels.Length);
        // page is mostly white, with some black text/line pixels
        var dark = 0;
        for (var i = 0; i < img.Pixels.Length; i += 4)
            if (img.Pixels[i] < 128 && img.Pixels[i + 1] < 128 && img.Pixels[i + 2] < 128) dark++;
        Assert.InRange(dark, 50, img.Width * img.Height / 4);

        var fit = doc.Render(0, 1000, 200);
        Assert.Equal(200, fit.Height);
        Assert.Equal(155, fit.Width);
    }

    [Fact]
    public async Task Package_round_trips_with_embedded_media_and_links()
    {
        var pdf = WriteMinimalPdf(Path.Combine(_dir, "score.pdf"), pages: 2);
        var mp3 = Path.Combine(_dir, "take1.mp3");
        File.WriteAllBytes(mp3, Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray());

        var book = new Book { Title = "Round Trip" };
        book.Documents.Add(new PdfDocumentRef { Name = "score", Source = MediaSource.FromFile(pdf), PageCount = 2 });
        var song = book.AddSongAt(0, "Song");
        var rec = new Recording { Name = "Take 1", Source = MediaSource.FromFile(mp3), DurationMs = 5000 };
        TimingEngine.SetPageStart(rec, 1, 2500);
        TimingEngine.AddMarker(rec, 0, 0.2, 0.3, 500);
        song.Recordings.Add(rec);

        // embedded
        var embedded = Path.Combine(_dir, "embedded.pmbook");
        await BookPackage.SaveAsync(book, embedded, embedMedia: true);
        var manifest = BookPackage.ReadManifest(embedded);
        Assert.All(manifest.AllMediaSources(), s => Assert.Equal(MediaSourceKind.Embedded, s.Kind));

        // delete originals: the embedded package must still load
        File.Delete(mp3);
        var cache = Path.Combine(_dir, "cache");
        var loaded = await BookPackage.LoadAsync(embedded, cache);
        Assert.Equal("Round Trip", loaded.Title);
        Assert.Equal(book.Id, loaded.Id);
        var lrec = loaded.Songs[0].Recordings[0];
        Assert.True(lrec.Source.IsResolved);
        Assert.Equal(5000, new FileInfo(lrec.Source.ResolvedPath!).Length);
        Assert.True(loaded.Documents[0].Source.IsResolved);
        Assert.Equal(2500, TimingEngine.PageStartTime(lrec, 1));
        Assert.Single(lrec.Markers);

        // links only: sources that only exist in the cache are embedded anyway; file sources stay as paths
        var links = Path.Combine(_dir, "links.pmbook");
        await BookPackage.SaveAsync(book, links, embedMedia: false);
        var linkManifest = BookPackage.ReadManifest(links);
        Assert.Equal(MediaSourceKind.File, linkManifest.Documents[0].Source.Kind);
        Assert.Equal(pdf, linkManifest.Documents[0].Source.Path);
        Assert.True(new FileInfo(links).Length < new FileInfo(embedded).Length);

        var linkLoaded = await BookPackage.LoadAsync(links, cache);
        Assert.True(linkLoaded.Documents[0].Source.IsResolved);
        Assert.False(linkLoaded.Songs[0].Recordings[0].Source.IsResolved); // mp3 was deleted
    }

    [Fact]
    public async Task Links_package_finds_media_next_to_the_package()
    {
        var pdfDir = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(pdfDir);
        var pdf = WriteMinimalPdf(Path.Combine(pdfDir, "moved.pdf"));
        var book = new Book { Title = "Moved" };
        book.Documents.Add(new PdfDocumentRef { Name = "moved", Source = MediaSource.FromFile(pdf), PageCount = 2 });
        var pkg = Path.Combine(_dir, "moved.pmbook");
        await BookPackage.SaveAsync(book, pkg, embedMedia: false);

        File.Move(pdf, Path.Combine(_dir, "moved.pdf")); // original path is gone; file now sits next to the package
        var loaded = await BookPackage.LoadAsync(pkg, Path.Combine(_dir, "cache"));
        Assert.True(loaded.Documents[0].Source.IsResolved);
    }
}
