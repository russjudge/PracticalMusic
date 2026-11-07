using PracticalMusic.Core.Models;
using PracticalMusic.Core.Packaging;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class SaveAndUrlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-save-tests-" + Guid.NewGuid().ToString("N"));

    public SaveAndUrlTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Save_uses_the_given_temp_folder_and_leaves_no_temp_files_behind()
    {
        var pdf = PackageAndPdfTests.WriteMinimalPdf(Path.Combine(_dir, "sheet.pdf"), 2);
        var book = new Book { Title = "Temp test" };
        book.Documents.Add(new PdfDocumentRef { Name = "sheet", Source = MediaSource.FromFile(pdf), PageCount = 2 });
        book.AddSongAt(0, "Song");

        var tempDir = Path.Combine(_dir, "temp");
        var target = Path.Combine(_dir, "out", "book.pmbook");
        await BookPackage.SaveAsync(book, target, embedMedia: true, tempDir: tempDir);

        Assert.True(File.Exists(target));
        Assert.Empty(Directory.GetFiles(tempDir));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.tmp"));
        var reloaded = BookPackage.ReadManifest(target);
        Assert.Equal("Temp test", reloaded.Title);

        // saving again over the existing file (the normal "Save" case) must work too
        await BookPackage.SaveAsync(book, target, embedMedia: false, tempDir: tempDir);
        Assert.Equal(MediaSourceKind.File, BookPackage.ReadManifest(target).Documents[0].Source.Kind);
    }

    [Fact]
    public async Task Url_probe_rejects_web_pages_without_network_access()
    {
        var soundslice = await MediaResolver.ProbeUrlAsync("https://www.soundslice.com/slices/MNR3c/");
        Assert.False(soundslice.IsAudio);
        Assert.Contains("Soundslice", soundslice.Problem);

        var notAUrl = await MediaResolver.ProbeUrlAsync("song.mp3");
        Assert.False(notAUrl.IsAudio);
    }

    [Fact]
    public void Web_page_content_types_are_recognised()
    {
        Assert.True(MediaResolver.IsWebPage("text/html"));
        Assert.True(MediaResolver.IsWebPage("application/xhtml+xml"));
        Assert.False(MediaResolver.IsWebPage("audio/mpeg"));
        Assert.True(MediaResolver.HasAudioExtension("Lion and the Lamb.MP3"));
        Assert.False(MediaResolver.HasAudioExtension("download"));
    }
}
