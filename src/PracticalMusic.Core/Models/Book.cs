using System.Text.Json.Serialization;

namespace PracticalMusic.Core.Models;

/// <summary>A PDF file that contributes pages to the book.</summary>
public sealed class PdfDocumentRef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MediaSource Source { get; set; } = new();
    public int PageCount { get; set; }
}

/// <summary>Start time of a page within a recording.</summary>
public sealed class PageTiming
{
    /// <summary>0-based page offset within the song (0 = first page of the song).</summary>
    public int PageOffset { get; set; }
    public double TimeMs { get; set; }
}

/// <summary>A point on a page that corresponds to a time in the recording.</summary>
public sealed class TimeMarker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double TimeMs { get; set; }
    /// <summary>0-based page offset within the song.</summary>
    public int PageOffset { get; set; }
    /// <summary>Normalized X (0 = left edge, 1 = right edge of the page).</summary>
    public double X { get; set; }
    /// <summary>Normalized Y (0 = top, 1 = bottom of the page).</summary>
    public double Y { get; set; }
    public string? Label { get; set; }
}

/// <summary>An audio file (MP3 etc.) linked to a song.</summary>
public sealed class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MediaSource Source { get; set; } = new();
    public double DurationMs { get; set; }
    /// <summary>When each page of the song starts. Offset 0 is always at 0 ms.</summary>
    public List<PageTiming> PageTimings { get; set; } = new();
    /// <summary>Fine-grained time points on the pages, sorted by time.</summary>
    public List<TimeMarker> Markers { get; set; } = new();
}

/// <summary>A song: a contiguous range of pages in the book plus its recordings.</summary>
public sealed class Song
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    /// <summary>0-based index of the first page in the combined page list of the book.</summary>
    public int StartPage { get; set; }
    public int PageCount { get; set; } = 1;
    public List<Recording> Recordings { get; set; } = new();
    /// <summary>Height of the playback highlight band as a fraction of page height.</summary>
    public double HighlightHeight { get; set; } = 0.08;

    [JsonIgnore]
    public int EndPage => StartPage + PageCount - 1;

    public bool ContainsPage(int globalPage) => globalPage >= StartPage && globalPage <= EndPage;
}

/// <summary>The whole setup: PDFs, songs, recordings and timings.</summary>
public sealed class Book
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Book";
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;
    public List<PdfDocumentRef> Documents { get; set; } = new();
    public List<Song> Songs { get; set; } = new();

    [JsonIgnore]
    public int TotalPages => Documents.Sum(d => d.PageCount);

    /// <summary>Maps a global page index to (document, page index inside that document).</summary>
    public (PdfDocumentRef Document, int PageIndex)? ResolvePage(int globalPage)
    {
        if (globalPage < 0) return null;
        var remaining = globalPage;
        foreach (var d in Documents)
        {
            if (remaining < d.PageCount) return (d, remaining);
            remaining -= d.PageCount;
        }
        return null;
    }

    public int FirstPageOf(PdfDocumentRef doc)
    {
        var p = 0;
        foreach (var d in Documents)
        {
            if (ReferenceEquals(d, doc) || d.Id == doc.Id) return p;
            p += d.PageCount;
        }
        return -1;
    }

    public Song? SongAtPage(int globalPage) => Songs.FirstOrDefault(s => s.ContainsPage(globalPage));

    public Song? SongStartingAt(int globalPage) => Songs.FirstOrDefault(s => s.StartPage == globalPage);

    [JsonIgnore]
    public IEnumerable<Song> SongsInPageOrder => Songs.OrderBy(s => s.StartPage);

    public IEnumerable<MediaSource> AllMediaSources()
    {
        foreach (var d in Documents) yield return d.Source;
        foreach (var s in Songs)
            foreach (var r in s.Recordings)
                yield return r.Source;
    }

    /// <summary>
    /// Creates a song starting at <paramref name="startPage"/>. The song runs up to the page
    /// before the next existing song (or the end of the book). The previous song is trimmed
    /// so that songs never overlap.
    /// </summary>
    public Song AddSongAt(int startPage, string? title = null)
    {
        var next = Songs.Where(s => s.StartPage > startPage).OrderBy(s => s.StartPage).FirstOrDefault();
        var end = next?.StartPage - 1 ?? Math.Max(startPage, TotalPages - 1);
        var song = new Song
        {
            Title = title ?? $"Song {Songs.Count + 1}",
            StartPage = startPage,
            PageCount = Math.Max(1, end - startPage + 1)
        };
        var prev = Songs.Where(s => s.StartPage < startPage).OrderByDescending(s => s.StartPage).FirstOrDefault();
        if (prev != null && prev.EndPage >= startPage)
            prev.PageCount = Math.Max(1, startPage - prev.StartPage);
        Songs.Add(song);
        Songs.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));
        return song;
    }

    /// <summary>Clamps song ranges so every song lies inside the book and songs do not overlap.</summary>
    public void NormalizeSongs()
    {
        Songs.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));
        var total = TotalPages;
        for (var i = 0; i < Songs.Count; i++)
        {
            var s = Songs[i];
            if (total == 0) { s.StartPage = 0; s.PageCount = 1; continue; }
            s.StartPage = Math.Clamp(s.StartPage, 0, total - 1);
            var maxEnd = i + 1 < Songs.Count ? Songs[i + 1].StartPage - 1 : total - 1;
            if (maxEnd < s.StartPage) maxEnd = s.StartPage;
            s.PageCount = Math.Clamp(s.PageCount, 1, maxEnd - s.StartPage + 1);
            foreach (var r in s.Recordings)
            {
                r.PageTimings.RemoveAll(t => t.PageOffset >= s.PageCount || t.PageOffset < 0);
                r.Markers.RemoveAll(m => m.PageOffset >= s.PageCount || m.PageOffset < 0);
                r.Markers.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
                r.PageTimings.Sort((a, b) => a.PageOffset.CompareTo(b.PageOffset));
            }
        }
    }
}
