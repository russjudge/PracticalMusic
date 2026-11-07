using PracticalMusic.Core.Models;

namespace PracticalMusic.Core.Timing;

/// <summary>A rectangle on a page (normalized 0..1 coordinates) that is currently being played.</summary>
public readonly record struct HighlightRegion(int PageOffset, double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;
}

/// <summary>
/// Pure functions that map between playback time, pages and marker positions.
/// Everything here is deterministic so it can be unit tested without audio or UI.
/// </summary>
public static class TimingEngine
{
    /// <summary>Page offset (0-based within the song) that should be shown at <paramref name="timeMs"/>.</summary>
    public static int PageOffsetAt(Recording recording, double timeMs)
    {
        var page = 0;
        var bestTime = double.NegativeInfinity;
        foreach (var t in recording.PageTimings)
        {
            if (t.PageOffset == 0) continue;
            if (t.TimeMs <= timeMs && t.TimeMs >= bestTime)
            {
                bestTime = t.TimeMs;
                page = Math.Max(page, t.PageOffset);
            }
        }
        return page;
    }

    /// <summary>Time at which <paramref name="pageOffset"/> starts. Page 0 always starts at 0.</summary>
    public static double PageStartTime(Recording recording, int pageOffset)
    {
        if (pageOffset <= 0) return 0;
        var exact = recording.PageTimings.FirstOrDefault(t => t.PageOffset == pageOffset);
        if (exact != null) return exact.TimeMs;
        // fall back to the closest earlier page that has a timing
        var earlier = recording.PageTimings.Where(t => t.PageOffset < pageOffset).OrderByDescending(t => t.PageOffset).FirstOrDefault();
        return earlier?.TimeMs ?? 0;
    }

    public static bool HasPageTiming(Recording recording, int pageOffset) =>
        pageOffset == 0 || recording.PageTimings.Any(t => t.PageOffset == pageOffset);

    /// <summary>Sets the start time of a page and keeps all page timings monotonic.</summary>
    public static void SetPageStart(Recording recording, int pageOffset, double timeMs)
    {
        if (pageOffset <= 0) return;
        timeMs = Math.Max(0, timeMs);
        var existing = recording.PageTimings.FirstOrDefault(t => t.PageOffset == pageOffset);
        if (existing == null) recording.PageTimings.Add(new PageTiming { PageOffset = pageOffset, TimeMs = timeMs });
        else existing.TimeMs = timeMs;

        foreach (var t in recording.PageTimings)
        {
            if (t.PageOffset < pageOffset && t.TimeMs > timeMs) t.TimeMs = timeMs;
            if (t.PageOffset > pageOffset && t.TimeMs < timeMs) t.TimeMs = timeMs;
        }
        recording.PageTimings.Sort((a, b) => a.PageOffset.CompareTo(b.PageOffset));
    }

    public static void ClearPageStart(Recording recording, int pageOffset) =>
        recording.PageTimings.RemoveAll(t => t.PageOffset == pageOffset);

    /// <summary>The marker at or before <paramref name="timeMs"/> (null if none yet).</summary>
    public static TimeMarker? CurrentMarker(Recording recording, double timeMs)
    {
        TimeMarker? best = null;
        foreach (var m in recording.Markers)
            if (m.TimeMs <= timeMs && (best == null || m.TimeMs >= best.TimeMs)) best = m;
        return best;
    }

    /// <summary>The first marker strictly after <paramref name="timeMs"/>.</summary>
    public static TimeMarker? NextMarker(Recording recording, double timeMs)
    {
        TimeMarker? best = null;
        foreach (var m in recording.Markers)
            if (m.TimeMs > timeMs && (best == null || m.TimeMs < best.TimeMs)) best = m;
        return best;
    }

    /// <summary>The marker that directly follows <paramref name="marker"/> in time order.</summary>
    public static TimeMarker? MarkerAfter(Recording recording, TimeMarker marker)
    {
        TimeMarker? best = null;
        foreach (var m in recording.Markers)
        {
            if (ReferenceEquals(m, marker) || m.Id == marker.Id) continue;
            if (m.TimeMs < marker.TimeMs) continue;
            if (m.TimeMs == marker.TimeMs && string.CompareOrdinal(m.Id, marker.Id) < 0) continue;
            if (best == null || m.TimeMs < best.TimeMs) best = m;
        }
        return best;
    }

    public static bool OnSameLine(Song song, TimeMarker a, TimeMarker b) =>
        a.PageOffset == b.PageOffset && Math.Abs(a.Y - b.Y) < song.HighlightHeight * 0.75;

    /// <summary>
    /// The region that is currently being played. The band starts at the current marker and
    /// ends at the next marker when that marker is on the same line, otherwise at the right
    /// edge of the page.
    /// </summary>
    public static HighlightRegion? HighlightAt(Song song, Recording recording, double timeMs)
    {
        var current = CurrentMarker(recording, timeMs);
        if (current == null) return null;
        var next = MarkerAfter(recording, current);
        var h = Math.Clamp(song.HighlightHeight, 0.01, 0.5);
        var y1 = Math.Clamp(current.Y - h / 2, 0, 1);
        var y2 = Math.Clamp(current.Y + h / 2, 0, 1);
        if (next != null && OnSameLine(song, current, next) && next.X > current.X)
            return new HighlightRegion(current.PageOffset, current.X, y1, next.X, y2);
        return new HighlightRegion(current.PageOffset, current.X, y1, 1.0, y2);
    }

    /// <summary>
    /// Converts a tap on a page into a playback time. Uses the markers on that page when
    /// available, interpolating between two markers on the same line; otherwise the page start.
    /// </summary>
    public static double TimeForTap(Song song, Recording recording, int pageOffset, double x, double y)
    {
        var onPage = recording.Markers.Where(m => m.PageOffset == pageOffset).ToList();
        if (onPage.Count == 0) return PageStartTime(recording, pageOffset);

        var lineTolerance = Math.Clamp(song.HighlightHeight, 0.01, 0.5) * 0.75;
        var sameLine = onPage.Where(m => Math.Abs(m.Y - y) <= lineTolerance).ToList();

        TimeMarker chosen;
        if (sameLine.Count > 0)
        {
            var leftOf = sameLine.Where(m => m.X <= x).OrderByDescending(m => m.X).FirstOrDefault();
            chosen = leftOf ?? sameLine.OrderBy(m => m.X).First();
        }
        else
        {
            chosen = onPage.OrderBy(m => Math.Abs(m.Y - y) * 4 + Math.Abs(m.X - x)).First();
        }

        var next = MarkerAfter(recording, chosen);
        if (next != null && OnSameLine(song, chosen, next) && next.X > chosen.X && x > chosen.X && x < next.X)
        {
            var f = (x - chosen.X) / (next.X - chosen.X);
            return chosen.TimeMs + f * (next.TimeMs - chosen.TimeMs);
        }
        return chosen.TimeMs;
    }

    public static TimeMarker AddMarker(Recording recording, int pageOffset, double x, double y, double timeMs, string? label = null)
    {
        var m = new TimeMarker
        {
            PageOffset = pageOffset,
            X = Math.Clamp(x, 0, 1),
            Y = Math.Clamp(y, 0, 1),
            TimeMs = Math.Max(0, timeMs),
            Label = label
        };
        recording.Markers.Add(m);
        recording.Markers.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return m;
    }

    public static void SortMarkers(Recording recording) =>
        recording.Markers.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

    /// <summary>Index of the two-page spread (0-based, counted from the first page of the song).</summary>
    public static int SpreadIndexForPage(int pageOffset) => Math.Max(0, pageOffset) / 2;

    /// <summary>Page offsets visible for a spread: one page in portrait, two in landscape.</summary>
    public static int[] VisiblePages(int pageOffset, int songPageCount, bool twoPages)
    {
        if (songPageCount <= 0) return Array.Empty<int>();
        pageOffset = Math.Clamp(pageOffset, 0, songPageCount - 1);
        if (!twoPages) return new[] { pageOffset };
        var first = SpreadIndexForPage(pageOffset) * 2;
        return first + 1 < songPageCount ? new[] { first, first + 1 } : new[] { first };
    }

    public static string FormatTime(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }
}
