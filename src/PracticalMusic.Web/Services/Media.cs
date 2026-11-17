using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using PracticalMusic.Core.Packaging;
using PracticalMusic.Core.Pdf;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Services;

/// <summary>
/// Uploaded files. Bytes are stored once per content (<c>media/&lt;sha256&gt;</c>); each organization gets its
/// own row, so access is always checked against the organization the row belongs to.
/// </summary>
public sealed class MediaStorage(DataPaths paths, IMediaRepository media, IMemoryCache cache)
{
    /// <summary>
    /// Serializes "store the file + record the row" against "delete rows/files nobody uses" (maintenance), so a file
    /// that is being uploaded again can never be deleted between the two steps.
    /// </summary>
    public static readonly SemaphoreSlim FileLock = new(1, 1);

    public static readonly IReadOnlyDictionary<string, string> AudioTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".wma"] = "audio/x-ms-wma"
    };

    public async Task<MediaRow?> GetAsync(long id) =>
        await cache.GetOrCreateAsync("media:" + id, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1); return media.GetAsync(id); });

    public string FilePath(MediaRow row) => paths.MediaFile(row.Sha256);

    /// <summary>Stores an uploaded PDF or audio file for an organization (or returns the existing row for identical content).</summary>
    public async Task<MediaRow> ImportAsync(long orgId, Stream content, string? fileName, long maxBytes, long? userId, CancellationToken ct = default)
    {
        var name = CleanFileName(fileName);
        var temp = paths.NewTempFile();
        try
        {
            var head = new byte[16 * 1024];
            var headLength = 0;
            long size = 0;
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                        throw new UserFacingException($"\"{name}\" is larger than the {maxBytes / (1024 * 1024)} MB this website accepts.", StatusCodes.Status413PayloadTooLarge, "too_large");
                    if (headLength < head.Length)
                    {
                        var n = Math.Min(read, head.Length - headLength);
                        Array.Copy(buffer, 0, head, headLength, n);
                        headLength += n;
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (size == 0) throw new UserFacingException($"\"{name}\" is empty.");

            var headSpan = head.AsSpan(0, headLength);
            string kind, contentType;
            int? pages = null;
            if (IsPdf(headSpan))
            {
                kind = MediaKinds.Pdf;
                contentType = "application/pdf";
                pages = CountPdfPages(temp)
                        ?? throw new UserFacingException($"\"{name}\" could not be read as a PDF. Is it damaged or password protected?");
                if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            }
            else if (AudioTypes.TryGetValue(Path.GetExtension(name), out var audioType) && LooksLikeAudio(headSpan))
            {
                kind = MediaKinds.Audio;
                contentType = audioType;
            }
            else
            {
                throw new UserFacingException(
                    $"\"{name}\" is not a PDF or a supported audio file (MP3, M4A, AAC, WAV, OGG, Opus, FLAC, WMA).",
                    StatusCodes.Status415UnsupportedMediaType, "unsupported");
            }

            await FileLock.WaitAsync(ct);
            try
            {
                MoveIntoStore(temp, sha);
                var now = DateTime.UtcNow;
                var existing = await media.FindAsync(orgId, sha);
                if (existing != null)
                {
                    await media.TouchAsync(new[] { existing.Id }, now); // uploaded again: in use again
                    return existing;
                }
                var row = new MediaRow
                {
                    OrgId = orgId, Sha256 = sha, Kind = kind, FileName = name, ContentType = contentType, SizeBytes = size,
                    PageCount = pages, CreatedAt = now, CreatedBy = userId, LastUsedAt = now
                };
                row.Id = await media.InsertAsync(row);
                return row;
            }
            finally
            {
                FileLock.Release();
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private void MoveIntoStore(string temp, string sha)
    {
        var target = paths.MediaFile(sha);
        if (File.Exists(target)) return; // identical content is already stored
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try { File.Move(temp, target); }
        catch (IOException) when (File.Exists(target)) { /* a parallel upload of the same file won */ }
    }

    public static string CleanFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "");
        name = BookPackage.Sanitize(name);
        if (name.Length > 150)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 12) ext = "";
            name = Path.GetFileNameWithoutExtension(name)[..Math.Min(120, Path.GetFileNameWithoutExtension(name).Length)] + ext;
        }
        if (name.Length > 150) name = name[..150];
        return string.IsNullOrWhiteSpace(name) || name == "file" ? "file" : name;
    }

    /// <summary>"%PDF-" within the first 1024 bytes (the PDF specification allows leading garbage).</summary>
    public static bool IsPdf(ReadOnlySpan<byte> head) => head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8) >= 0;

    /// <summary>Recognizes the usual audio containers by their first bytes, so a renamed web page is not accepted as a recording.</summary>
    public static bool LooksLikeAudio(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4) return false;
        if (h.StartsWith("RIFF"u8) && h.Length >= 12 && h.Slice(8, 4).SequenceEqual("WAVE"u8)) return true;
        if (h.StartsWith("OggS"u8) || h.StartsWith("fLaC"u8)) return true;
        if (h.Length >= 8 && h.Slice(4, 4).SequenceEqual("ftyp"u8)) return true;       // MP4 / M4A
        if (h.StartsWith(new byte[] { 0x30, 0x26, 0xB2, 0x75 })) return true;          // ASF (WMA)
        // MP3 and AAC streams: some encoders write zero padding before the first frame; players skip it
        var start = 0;
        while (start < h.Length && h[start] == 0) start++;
        var s = h[start..];
        if (s.Length < 4) return false;
        if (s.StartsWith("ID3"u8)) return true;                                        // MP3 with an ID3v2 tag
        return IsMpegAudioHeader(s) || IsAdtsHeader(s);
    }

    /// <summary>A valid MPEG audio (MP1/2/3) frame header: sync, no reserved version/layer, a real bit rate and sample rate.</summary>
    public static bool IsMpegAudioHeader(ReadOnlySpan<byte> s) =>
        s.Length >= 4 && s[0] == 0xFF && (s[1] & 0xE0) == 0xE0 &&
        ((s[1] >> 3) & 3) != 1 && ((s[1] >> 1) & 3) != 0 &&
        (s[2] >> 4) != 15 && ((s[2] >> 2) & 3) != 3;

    /// <summary>An ADTS (raw AAC) frame header.</summary>
    public static bool IsAdtsHeader(ReadOnlySpan<byte> s) =>
        s.Length >= 4 && s[0] == 0xFF && (s[1] & 0xF6) == 0xF0 && ((s[2] >> 2) & 0xF) < 13;

    public static int? CountPdfPages(string path)
    {
        try
        {
            using var doc = PdfDocument.Load(path);
            return doc.PageCount > 0 ? doc.PageCount : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Minimal PNG writer for rendered pages: 8-bit grayscale when the page has no color (most sheet music), RGB otherwise.</summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes opaque top-down BGRA pixels.</summary>
    public static byte[] Encode(int width, int height, byte[] bgra, CompressionLevel level = CompressionLevel.Optimal)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("pixel buffer too small", nameof(bgra));
        var gray = IsGray(bgra, width * height);
        var channels = gray ? 1 : 3;
        var rowLength = width * channels;

        using var output = new MemoryStream();
        output.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                    // bit depth
        ihdr[9] = (byte)(gray ? 0 : 2); // color type: grayscale or truecolor
        WriteChunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, level, leaveOpen: true))
            {
                var previous = new byte[rowLength];
                var current = new byte[rowLength];
                var candidates = new byte[5][];
                for (var f = 0; f < 5; f++) candidates[f] = new byte[rowLength];
                for (var y = 0; y < height; y++)
                {
                    var src = y * width * 4;
                    if (gray)
                        for (var x = 0; x < width; x++) current[x] = bgra[src + x * 4];
                    else
                        for (var x = 0; x < width; x++)
                        {
                            current[x * 3] = bgra[src + x * 4 + 2];
                            current[x * 3 + 1] = bgra[src + x * 4 + 1];
                            current[x * 3 + 2] = bgra[src + x * 4];
                        }
                    var best = Filter(current, previous, channels, candidates);
                    z.WriteByte((byte)best);
                    z.Write(candidates[best]);
                    (previous, current) = (current, previous);
                }
            }
            WriteChunk(output, "IDAT", idat.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static bool IsGray(byte[] bgra, int pixels)
    {
        for (var i = 0; i < pixels; i++)
        {
            var p = i * 4;
            if (bgra[p] != bgra[p + 1] || bgra[p] != bgra[p + 2]) return false;
        }
        return true;
    }

    /// <summary>Fills the candidate rows for filters 0-4 and returns the one with the smallest sum of absolute values (the usual heuristic).</summary>
    private static int Filter(byte[] row, byte[] prior, int bpp, byte[][] c)
    {
        var bestSum = long.MaxValue;
        var best = 0;
        for (var f = 0; f < 5; f++)
        {
            var dst = c[f];
            long sum = 0;
            for (var i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], cc = i >= bpp ? prior[i - bpp] : 0;
                var v = f switch
                {
                    0 => row[i],
                    1 => row[i] - a,
                    2 => row[i] - b,
                    3 => row[i] - ((a + b) >> 1),
                    _ => row[i] - Paeth(a, b, cc)
                };
                var bv = (byte)v;
                dst[i] = bv;
                sum += bv < 128 ? bv : 256 - bv;
            }
            if (sum < bestSum) { bestSum = sum; best = f; }
        }
        return best;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

/// <summary>
/// Renders PDF pages to PNG with PDFium - the same engine the apps use - so iPhones, iPads and every other
/// browser get exactly the same page images without downloading whole PDFs. Images are cached on disk
/// (content-addressed, never stale) and the least recently used are trimmed by the maintenance task.
/// </summary>
public sealed class PageRenderService(DataPaths paths, ILogger<PageRenderService> log) : IDisposable
{
    /// <summary>Requested widths are rounded up to one of these, so the cache holds a bounded number of variants.</summary>
    public static readonly int[] Widths = { 160, 320, 480, 640, 800, 1000, 1200, 1500, 1800, 2200, 2600 };
    private const int MaxOpenDocuments = 4;
    /// <summary>PDFs are held in memory while open; above this total the least recently used are closed.</summary>
    private const long MaxOpenBytes = 400L * 1024 * 1024;

    private readonly SemaphoreSlim _renderGate = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflight = new();
    private readonly object _docsGate = new();
    private readonly Dictionary<string, (PdfDocument Doc, long Touched, long Bytes)> _docs = new();
    private long _tick;

    public static int BucketWidth(int requested)
    {
        foreach (var w in Widths) if (w >= requested) return w;
        return Widths[^1];
    }

    public string CachePath(string sha256, int pageIndex, int width) =>
        Path.Combine(paths.PageCache, sha256[..2], sha256, $"{pageIndex}-{width}.png");

    /// <summary>Path of the PNG for a page, rendering it first when it is not cached yet.</summary>
    public async Task<string> GetPageImageAsync(MediaRow media, string mediaFile, int pageIndex, int requestedWidth, CancellationToken ct)
    {
        if (media.Kind != MediaKinds.Pdf) throw UserFacingException.NotFound("Not a PDF.");
        if (pageIndex < 0 || pageIndex >= (media.PageCount ?? 0)) throw UserFacingException.NotFound("No such page.");
        var width = BucketWidth(requestedWidth);
        var path = CachePath(media.Sha256, pageIndex, width);
        if (File.Exists(path))
        {
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { /* best effort, used for trimming */ }
            return path;
        }
        var lazy = _inflight.GetOrAdd(path, key => new Lazy<Task<string>>(() => RenderAsync(media.Sha256, mediaFile, pageIndex, width, key)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        finally
        {
            if (lazy.Value.IsCompleted) _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(path, lazy));
        }
    }

    private async Task<string> RenderAsync(string sha, string mediaFile, int pageIndex, int width, string path)
    {
        await _renderGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                // a crafted page with an absurd shape must not turn into a gigantic bitmap: height is capped too
                var maxHeight = width * 4;
                PdfPageImage image;
                try { image = Document(sha, mediaFile).Render(pageIndex, width, maxHeight); }
                catch (ObjectDisposedException) { image = Document(sha, mediaFile).Render(pageIndex, width, maxHeight); } // evicted meanwhile: reopen
                var png = PngEncoder.Encode(image.Width, image.Height, image.Pixels);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, png);
                File.Move(temp, path, overwrite: true);
                return path;
            });
        }
        catch (Exception ex) when (ex is not UserFacingException)
        {
            log.LogWarning(ex, "Rendering page {Page} of {Sha} failed", pageIndex, sha);
            throw new UserFacingException("This page could not be rendered.", StatusCodes.Status500InternalServerError, "render_failed");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private PdfDocument Document(string sha, string mediaFile)
    {
        lock (_docsGate)
        {
            if (_docs.TryGetValue(sha, out var entry))
            {
                _docs[sha] = (entry.Doc, ++_tick, entry.Bytes);
                return entry.Doc;
            }
            if (!File.Exists(mediaFile)) throw new FileNotFoundException("media file missing", mediaFile);
            var doc = PdfDocument.Load(mediaFile);
            _docs[sha] = (doc, ++_tick, new FileInfo(mediaFile).Length);
            while (_docs.Count > 1 && (_docs.Count > MaxOpenDocuments || _docs.Values.Sum(d => d.Bytes) > MaxOpenBytes))
            {
                var oldest = _docs.MinBy(kv => kv.Value.Touched);
                _docs.Remove(oldest.Key);
                oldest.Value.Doc.Dispose();
            }
            return doc;
        }
    }

    public void Dispose()
    {
        lock (_docsGate)
        {
            foreach (var d in _docs.Values) d.Doc.Dispose();
            _docs.Clear();
        }
    }
}
