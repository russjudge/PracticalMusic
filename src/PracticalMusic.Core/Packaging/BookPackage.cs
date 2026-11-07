using System.IO.Compression;
using PracticalMusic.Core.Models;

namespace PracticalMusic.Core.Packaging;

/// <summary>
/// Reads and writes .pmbook files. A .pmbook is a ZIP archive that always contains
/// <c>book.json</c>. With "embed media" the PDF and audio files are stored under <c>media/</c>
/// so the package is self-contained; otherwise book.json only references external files/URLs.
/// </summary>
public static class BookPackage
{
    public const string Extension = ".pmbook";
    public const string ManifestEntry = "book.json";
    public const string MediaFolder = "media/";

    /// <summary>
    /// Writes the package. The archive is first written to a temporary file (in <paramref name="tempDir"/>,
    /// or the system temp folder) and then moved into place, so a failed save never leaves a half-written
    /// book behind. The result is verified afterwards: some systems (Windows "Controlled folder access")
    /// silently discard writes to protected folders such as Documents.
    /// </summary>
    public static async Task SaveAsync(Book book, string path, bool embedMedia,
        IProgress<(string Item, double Fraction)>? progress = null, CancellationToken ct = default, string? tempDir = null)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        tempDir = string.IsNullOrEmpty(tempDir) ? Path.GetTempPath() : tempDir;
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, "save_" + Guid.NewGuid().ToString("N") + Extension);

        var copy = BookJson.Clone(book);
        copy.Modified = DateTimeOffset.Now;

        // pair every source in the clone with its runtime-resolved counterpart in the original
        var originals = book.AllMediaSources().ToList();
        var clones = copy.AllMediaSources().ToList();

        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < clones.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var src = clones[i];
                    var resolved = originals[i].ResolvedPath;
                    var canEmbed = !string.IsNullOrEmpty(resolved) && File.Exists(resolved);
                    var mustEmbed = src.Kind == MediaSourceKind.Embedded; // nothing else points at it

                    if ((embedMedia || mustEmbed) && canEmbed)
                    {
                        var entryName = MediaFolder + UniqueName(used, i, src.FileName);
                        progress?.Report((src.FileName, (double)i / Math.Max(1, clones.Count)));
                        var level = src.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.Fastest
                            : CompressionLevel.NoCompression;
                        var entry = zip.CreateEntry(entryName, level);
                        await using (var es = entry.Open())
                        await using (var input = File.OpenRead(resolved!))
                        {
                            await input.CopyToAsync(es, ct);
                        }
                        src.Kind = MediaSourceKind.Embedded;
                        src.EntryName = entryName;
                        src.Path = null;
                        src.SizeBytes = new FileInfo(resolved!).Length;
                    }
                    else if (src.Kind == MediaSourceKind.Embedded)
                    {
                        // Was embedded in an earlier package but the cache copy is gone: keep the reference
                        // so the user sees what is missing rather than silently losing it.
                        src.EntryName = null;
                        src.Kind = MediaSourceKind.File;
                        src.Path ??= resolved;
                    }
                }

                var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var ms = manifest.Open();
                await using var writer = new StreamWriter(ms);
                await writer.WriteAsync(BookJson.Serialize(copy));
            }

            if (!File.Exists(temp))
                throw new IOException($"The book could not be written to the temporary folder \"{tempDir}\".");
            var expected = new FileInfo(temp).Length;

            progress?.Report(("done", 0.98));
            MoveIntoPlace(temp, path);

            var written = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (written != expected)
                throw new IOException(ProtectedFolderMessage(path));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
        progress?.Report(("done", 1));
    }

    private static void MoveIntoPlace(string temp, string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException ex) { last = ex; }
            catch (UnauthorizedAccessException ex) { last = ex; }
            Thread.Sleep(150 * (attempt + 1)); // antivirus / sync clients may hold the target briefly
        }
        // Moving failed (different volume quirks, locked target …): fall back to a plain copy.
        try
        {
            File.Copy(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new IOException(ProtectedFolderMessage(path) + " (" + (last ?? ex).Message + ")", ex);
        }
    }

    private static string ProtectedFolderMessage(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        var hint = OperatingSystem.IsWindows()
            ? " On Windows this usually means \"Controlled folder access\" (Windows Security → Virus & threat protection → " +
              "Ransomware protection) is blocking Practical Music. Allow the app there, or save to a folder that is not protected, such as Downloads."
            : " Check that you have permission to write to that folder.";
        return $"The book could not be saved to \"{folder}\"." + hint;
    }

    /// <summary>Reads book.json only. Embedded media is not extracted.</summary>
    public static Book ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Not a Practical Music book (book.json missing).");
        using var reader = new StreamReader(entry.Open());
        return BookJson.Deserialize(reader.ReadToEnd());
    }

    /// <summary>
    /// Loads a package. Embedded media is extracted to <paramref name="cacheRoot"/>/{bookId}/ and every
    /// source gets a ResolvedPath when the file is available locally. URL sources are left for
    /// <see cref="MediaResolver"/>.
    /// </summary>
    public static async Task<Book> LoadAsync(string path, string cacheRoot, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Not a Practical Music book (book.json missing).");
        Book book;
        using (var reader = new StreamReader(manifestEntry.Open()))
            book = BookJson.Deserialize(await reader.ReadToEndAsync(ct));

        var bookCache = Path.Combine(cacheRoot, Sanitize(book.Id));
        var packageDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        foreach (var src in book.AllMediaSources())
        {
            ct.ThrowIfCancellationRequested();
            switch (src.Kind)
            {
                case MediaSourceKind.Embedded when !string.IsNullOrEmpty(src.EntryName):
                {
                    var entry = zip.GetEntry(src.EntryName);
                    if (entry == null) break;
                    var target = Path.Combine(bookCache, src.EntryName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var fi = new FileInfo(target);
                    if (!fi.Exists || fi.Length != entry.Length)
                    {
                        await using var es = entry.Open();
                        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                        await es.CopyToAsync(output, ct);
                    }
                    src.ResolvedPath = target;
                    src.SizeBytes = entry.Length;
                    break;
                }
                case MediaSourceKind.File:
                {
                    if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
                        src.ResolvedPath = src.Path;
                    else if (!string.IsNullOrEmpty(src.FileName) && File.Exists(Path.Combine(packageDir, src.FileName)))
                        src.ResolvedPath = Path.Combine(packageDir, src.FileName); // moved alongside the package
                    break;
                }
                case MediaSourceKind.Url:
                {
                    var cached = MediaResolver.CachePathFor(cacheRoot, src);
                    if (File.Exists(cached)) src.ResolvedPath = cached;
                    break;
                }
            }
        }

        book.NormalizeSongs();
        return book;
    }

    private static string UniqueName(HashSet<string> used, int index, string fileName)
    {
        var name = Sanitize(string.IsNullOrWhiteSpace(fileName) ? $"media{index}" : fileName);
        var candidate = name;
        var n = 1;
        while (!used.Add(candidate))
            candidate = $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}";
        return candidate;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "file" : s;
    }
}
