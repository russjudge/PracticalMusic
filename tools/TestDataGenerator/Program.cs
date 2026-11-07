// Generates the app icons (PNG + ICO + Android mipmaps) and a multi-song test PDF.
// Usage: TestDataGenerator <repoRoot> [testDataOutputDir]
using System.Text;
using SkiaSharp;

var repoRoot = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var testOut = args.Length > 1 ? args[1] : Path.Combine(repoRoot, "testdata");
Directory.CreateDirectory(testOut);

GenerateIcons(repoRoot);
var pdfPath = Path.Combine(testOut, "test-sheet-music.pdf");
GenerateTestPdf(pdfPath);
await GenerateDemoBookAsync(testOut, pdfPath);
Console.WriteLine("done");

// Builds demo.pmbook (links) and demo-embedded.pmbook from the test PDF plus any *.mp3 files
// found in the test data folder (first four, in name order), with page timings and markers.
static async Task GenerateDemoBookAsync(string testOut, string pdfPath)
{
    var mp3s = Directory.GetFiles(testOut, "*.mp3").OrderBy(f => f).Take(4).ToList();
    if (mp3s.Count == 0)
    {
        Console.WriteLine("no mp3 files in " + testOut + " - demo book skipped");
        return;
    }
    var book = new PracticalMusic.Core.Models.Book { Title = "Demo Book" };
    book.Documents.Add(new PracticalMusic.Core.Models.PdfDocumentRef
    {
        Name = "test-sheet-music",
        Source = PracticalMusic.Core.Models.MediaSource.FromFile(pdfPath),
        PageCount = 10
    });
    var layout = new[] { ("Morning Hymn", 0, 2), ("River Song", 2, 3), ("Lullaby", 5, 1), ("Grand March", 6, 4) };
    for (var i = 0; i < layout.Length; i++)
    {
        var (title, start, count) = layout[i];
        var song = book.AddSongAt(start, title);
        song.PageCount = count;
        if (i >= mp3s.Count) continue;
        var rec = new PracticalMusic.Core.Models.Recording
        {
            Name = Path.GetFileNameWithoutExtension(mp3s[i]),
            Source = PracticalMusic.Core.Models.MediaSource.FromFile(mp3s[i])
        };
        rec.PageTimings.Add(new PracticalMusic.Core.Models.PageTiming { PageOffset = 0, TimeMs = 0 });
        // 6 lines per page, 4 seconds per line: page p starts at p*24s, line l at +4s*l
        for (var p = 0; p < count; p++)
        {
            if (p > 0) PracticalMusic.Core.Timing.TimingEngine.SetPageStart(rec, p, p * 24000);
            for (var l = 0; l < 6; l++)
            {
                var y = (792 - (660 - l * 100 - 16)) / 792.0; // middle of the staff, normalized from top
                PracticalMusic.Core.Timing.TimingEngine.AddMarker(rec, p, 72 / 612.0, y, p * 24000 + l * 4000);
                PracticalMusic.Core.Timing.TimingEngine.AddMarker(rec, p, 306 / 612.0, y, p * 24000 + l * 4000 + 2000);
            }
        }
        song.Recordings.Add(rec);
    }
    book.NormalizeSongs();
    await PracticalMusic.Core.Packaging.BookPackage.SaveAsync(book, Path.Combine(testOut, "demo.pmbook"), embedMedia: false);
    await PracticalMusic.Core.Packaging.BookPackage.SaveAsync(book, Path.Combine(testOut, "demo-embedded.pmbook"), embedMedia: true);
    Console.WriteLine($"demo books written to {testOut} ({mp3s.Count} recordings)");
}

static void GenerateIcons(string repoRoot)
{
    var assets = Path.Combine(repoRoot, "src", "PracticalMusic.App", "Assets");
    Directory.CreateDirectory(assets);
    var pngs = new Dictionary<int, byte[]>();
    foreach (var size in new[] { 16, 32, 48, 64, 128, 256, 512 })
    {
        pngs[size] = RenderIcon(size);
        File.WriteAllBytes(Path.Combine(assets, $"app-icon-{size}.png"), pngs[size]);
    }
    File.WriteAllBytes(Path.Combine(assets, "app-icon.ico"), BuildIco(pngs, new[] { 16, 32, 48, 64, 128, 256 }));
    File.WriteAllBytes(Path.Combine(repoRoot, "packaging", "app-icon-512.png"), pngs[512]);

    var android = Path.Combine(repoRoot, "src", "PracticalMusic.Android", "Resources");
    foreach (var (folder, size) in new[] { ("mipmap-mdpi", 48), ("mipmap-hdpi", 72), ("mipmap-xhdpi", 96), ("mipmap-xxhdpi", 144), ("mipmap-xxxhdpi", 192) })
    {
        Directory.CreateDirectory(Path.Combine(android, folder));
        File.WriteAllBytes(Path.Combine(android, folder, "appicon.png"), RenderIcon(size));
    }
    Console.WriteLine("icons written to " + assets);
}

static byte[] RenderIcon(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    var s = size;
    var pad = s * 0.04f;
    var rect = new SKRect(pad, pad, s - pad, s - pad);

    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(s, s),
            new[] { new SKColor(0x1E, 0x3A, 0x8A), new SKColor(0x0E, 0x76, 0x90) }, null, SKShaderTileMode.Clamp);
        c.DrawRoundRect(rect, s * 0.2f, s * 0.2f, bg);
    }

    // staff lines
    using (var line = new SKPaint { Color = new SKColor(255, 255, 255, 110), StrokeWidth = Math.Max(1, s * 0.022f), IsAntialias = true })
    {
        for (var i = 0; i < 5; i++)
        {
            var y = s * (0.30f + i * 0.10f);
            c.DrawLine(s * 0.14f, y, s * 0.86f, y, line);
        }
    }

    // eighth note
    using (var note = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill })
    using (var stem = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.055f, StrokeCap = SKStrokeCap.Round })
    {
        var headCx = s * 0.42f;
        var headCy = s * 0.68f;
        c.Save();
        c.RotateDegrees(-20, headCx, headCy);
        c.DrawOval(new SKRect(headCx - s * 0.14f, headCy - s * 0.10f, headCx + s * 0.14f, headCy + s * 0.10f), note);
        c.Restore();
        var stemX = headCx + s * 0.12f;
        c.DrawLine(stemX, headCy - s * 0.02f, stemX, s * 0.22f, stem);
        using var flag = new SKPath();
        flag.MoveTo(stemX, s * 0.22f);
        flag.CubicTo(stemX + s * 0.05f, s * 0.30f, stemX + s * 0.22f, s * 0.32f, stemX + s * 0.16f, s * 0.48f);
        flag.CubicTo(stemX + s * 0.20f, s * 0.36f, stemX + s * 0.06f, s * 0.34f, stemX, s * 0.30f);
        flag.Close();
        c.DrawPath(flag, note);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static byte[] BuildIco(Dictionary<int, byte[]> pngs, int[] sizes)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    foreach (var size in sizes)
    {
        var data = pngs[size];
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(data.Length); w.Write(offset);
        offset += data.Length;
    }
    foreach (var size in sizes) w.Write(pngs[size]);
    return ms.ToArray();
}

static void GenerateTestPdf(string path)
{
    // 4 songs with 2, 3, 1 and 4 pages = 10 pages, letter size
    var songs = new[] { ("Morning Hymn", 2), ("River Song", 3), ("Lullaby", 1), ("Grand March", 4) };
    var pages = new List<string>();
    foreach (var (title, count) in songs)
        for (var p = 1; p <= count; p++)
            pages.Add(PageContent(title, p, count));

    var objects = new List<string>();
    // 1 catalog, 2 pages, 3 font, then page+content pairs
    var kids = new StringBuilder();
    for (var i = 0; i < pages.Count; i++) kids.Append($"{4 + i * 2} 0 R ");
    objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
    objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
    objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
    for (var i = 0; i < pages.Count; i++)
    {
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
        var content = pages[i];
        objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
    }

    var sb = new StringBuilder();
    var offsets = new List<int>();
    sb.Append("%PDF-1.4\n%âãÏÓ\n");
    for (var i = 0; i < objects.Count; i++)
    {
        offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
        sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    var xref = Encoding.Latin1.GetByteCount(sb.ToString());
    sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
    sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    File.WriteAllBytes(path, Encoding.Latin1.GetBytes(sb.ToString()));
    Console.WriteLine($"test PDF written to {path} ({pages.Count} pages)");
}

static string PageContent(string title, int page, int count)
{
    var sb = new StringBuilder();
    sb.Append($"BT /F1 26 Tf 72 730 Td ({Escape(title)}) Tj ET\n");
    sb.Append($"BT /F1 12 Tf 72 712 Td (Page {page} of {count}  -  Practical Music test score) Tj ET\n");
    sb.Append("0.6 w\n");
    const int lines = 6;
    for (var l = 0; l < lines; l++)
    {
        var top = 660 - l * 100;
        sb.Append($"BT /F1 9 Tf 40 {top - 20} Td (L{l + 1}) Tj ET\n");
        for (var i = 0; i < 5; i++)
        {
            var y = top - i * 8;
            sb.Append($"72 {y} m 540 {y} l S\n");
        }
        // bar lines every 117pt
        for (var b = 0; b <= 4; b++)
        {
            var x = 72 + b * 117;
            sb.Append($"{x} {top} m {x} {top - 32} l S\n");
        }
        // a few "notes" (filled rectangles) so lines look different
        var rnd = new Random(title.Length * 31 + page * 7 + l);
        for (var n = 0; n < 12; n++)
        {
            var x = 85 + n * 37 + rnd.Next(0, 10);
            var y = top - rnd.Next(0, 5) * 8 - 3;
            sb.Append($"{x} {y} 6 5 re f\n");
            sb.Append($"{x + 6} {y + 2} m {x + 6} {y + 24} l S\n");
        }
    }
    return sb.ToString();
}

static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
