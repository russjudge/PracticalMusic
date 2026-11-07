using System.Runtime.InteropServices;

namespace PracticalMusic.Core.Pdf;

/// <summary>A rendered page: 32-bit BGRA pixels, opaque, top-down rows.</summary>
public sealed class PdfPageImage
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; }

    public PdfPageImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>A PDF loaded into PDFium. Rendering is serialized through a global lock.</summary>
public sealed class PdfDocument : IDisposable
{
    private readonly byte[] _data;
    private GCHandle _pin;
    private IntPtr _doc;
    private bool _disposed;

    public int PageCount { get; }
    public string? SourcePath { get; }

    private PdfDocument(byte[] data, string? sourcePath)
    {
        PdfiumNative.EnsureInitialized();
        _data = data;
        SourcePath = sourcePath;
        _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);
        lock (PdfiumNative.Lock)
        {
            _doc = PdfiumNative.FPDF_LoadMemDocument(_pin.AddrOfPinnedObject(), _data.Length, null);
            if (_doc == IntPtr.Zero)
            {
                var err = PdfiumNative.FPDF_GetLastError();
                _pin.Free();
                throw new InvalidDataException($"PDFium could not open the document (error {err}).");
            }
            PageCount = PdfiumNative.FPDF_GetPageCount(_doc);
        }
    }

    public static PdfDocument Load(string path) => new(File.ReadAllBytes(path), path);

    public static PdfDocument Load(byte[] data) => new(data, null);

    public static int GetPageCount(string path)
    {
        using var doc = Load(path);
        return doc.PageCount;
    }

    /// <summary>Page size in PDF points (1/72 inch).</summary>
    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        ThrowIfDisposed();
        lock (PdfiumNative.Lock)
        {
            var page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            try
            {
                return (PdfiumNative.FPDF_GetPageWidthF(page), PdfiumNative.FPDF_GetPageHeightF(page));
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Renders a page so that it fits inside <paramref name="maxWidth"/> x <paramref name="maxHeight"/>
    /// pixels while preserving the aspect ratio.
    /// </summary>
    public PdfPageImage Render(int pageIndex, int maxWidth, int maxHeight = int.MaxValue)
    {
        ThrowIfDisposed();
        lock (PdfiumNative.Lock)
        {
            var page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
            if (page == IntPtr.Zero) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            try
            {
                double pw = PdfiumNative.FPDF_GetPageWidthF(page);
                double ph = PdfiumNative.FPDF_GetPageHeightF(page);
                if (pw <= 0 || ph <= 0) { pw = 612; ph = 792; }
                var scale = Math.Min(maxWidth / pw, maxHeight / ph);
                var w = Math.Max(1, (int)Math.Round(pw * scale));
                var h = Math.Max(1, (int)Math.Round(ph * scale));

                var pixels = new byte[w * h * 4];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    var bmp = PdfiumNative.FPDFBitmap_CreateEx(w, h, PdfiumNative.FPDFBitmap_BGRA, handle.AddrOfPinnedObject(), w * 4);
                    if (bmp == IntPtr.Zero) throw new OutOfMemoryException("PDFium could not allocate a bitmap.");
                    try
                    {
                        PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                        PdfiumNative.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, PdfiumNative.FPDF_ANNOT);
                    }
                    finally
                    {
                        PdfiumNative.FPDFBitmap_Destroy(bmp);
                    }
                }
                finally
                {
                    handle.Free();
                }
                return new PdfPageImage(w, h, pixels);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfDocument));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (PdfiumNative.Lock)
        {
            if (_doc != IntPtr.Zero) PdfiumNative.FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
        }
        if (_pin.IsAllocated) _pin.Free();
    }
}
