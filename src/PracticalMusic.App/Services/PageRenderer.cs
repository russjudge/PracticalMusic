using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Pdf;

namespace PracticalMusic.App.Services;

/// <summary>
/// Opens PDFs once, renders pages on a background thread and caches the resulting bitmaps.
/// Cache keys include the requested size (bucketed) so thumbnails and full pages coexist.
/// </summary>
public sealed class PageRenderer : IDisposable
{
    private const int SizeBucket = 64;
    private const int MaxCacheBytes = 220 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, PdfDocument> _docs = new();           // resolved path -> doc
    private readonly Dictionary<string, (Bitmap Bitmap, long Bytes, long Touched)> _cache = new();
    private long _cacheBytes;
    private long _tick;

    public int GetPageCount(string path)
    {
        var doc = Open(path);
        return doc?.PageCount ?? 0;
    }

    public (double Width, double Height)? GetPageSize(Book book, int globalPage)
    {
        var located = book.ResolvePage(globalPage);
        if (located == null) return null;
        var path = located.Value.Document.Source.ResolvedPath;
        if (string.IsNullOrEmpty(path)) return null;
        var doc = Open(path);
        if (doc == null || located.Value.PageIndex >= doc.PageCount) return null;
        try { return doc.GetPageSize(located.Value.PageIndex); } catch { return null; }
    }

    /// <summary>Renders (or fetches from cache) a page that fits inside maxWidth x maxHeight device pixels.</summary>
    public Task<Bitmap?> RenderAsync(Book book, int globalPage, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        var located = book.ResolvePage(globalPage);
        if (located == null) return Task.FromResult<Bitmap?>(null);
        var (document, pageIndex) = located.Value;
        var path = document.Source.ResolvedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Task.FromResult<Bitmap?>(null);

        maxWidth = Math.Max(SizeBucket, (maxWidth + SizeBucket - 1) / SizeBucket * SizeBucket);
        maxHeight = maxHeight >= int.MaxValue / 2 ? int.MaxValue : Math.Max(SizeBucket, (maxHeight + SizeBucket - 1) / SizeBucket * SizeBucket);
        var key = $"{path}|{pageIndex}|{maxWidth}x{maxHeight}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _cache[key] = (hit.Bitmap, hit.Bytes, ++_tick);
                return Task.FromResult<Bitmap?>(hit.Bitmap);
            }
        }

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var doc = Open(path);
            if (doc == null || pageIndex >= doc.PageCount) return null;
            PdfPageImage image;
            try { image = doc.Render(pageIndex, maxWidth, maxHeight); }
            catch { return null; }
            var bitmap = ToBitmap(image);
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var existing)) { bitmap.Dispose(); return existing.Bitmap; }
                var bytes = (long)image.Width * image.Height * 4;
                _cache[key] = (bitmap, bytes, ++_tick);
                _cacheBytes += bytes;
                Evict();
            }
            return bitmap;
        }, ct);
    }

    private void Evict()
    {
        while (_cacheBytes > MaxCacheBytes && _cache.Count > 4)
        {
            var oldest = _cache.MinBy(kv => kv.Value.Touched);
            _cache.Remove(oldest.Key);
            _cacheBytes -= oldest.Value.Bytes;
            // Bitmaps may still be displayed; let the GC finalize them instead of disposing eagerly.
        }
    }

    private static unsafe Bitmap ToBitmap(PdfPageImage image)
    {
        fixed (byte* p = image.Pixels)
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, (IntPtr)p,
                new PixelSize(image.Width, image.Height), new Vector(96, 96), image.Stride);
        }
    }

    private PdfDocument? Open(string path)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(path, out var doc)) return doc;
            try
            {
                doc = PdfDocument.Load(path);
                _docs[path] = doc;
                return doc;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Forgets a document (e.g. after it was removed from the book or replaced on disk).</summary>
    public void Forget(string path)
    {
        lock (_gate)
        {
            if (_docs.Remove(path, out var doc)) doc.Dispose();
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToList())
            {
                _cacheBytes -= _cache[key].Bytes;
                _cache.Remove(key);
            }
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _cacheBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var d in _docs.Values) d.Dispose();
            _docs.Clear();
            _cache.Clear();
            _cacheBytes = 0;
        }
    }
}
