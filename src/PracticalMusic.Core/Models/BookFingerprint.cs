using System.Security.Cryptography;
using System.Text.Json;

namespace PracticalMusic.Core.Models;

/// <summary>
/// A hash of what the user edits in a book: title, PDFs, songs, recordings, page times and markers.
/// Deliberately left out: the modification time, the online origin, where media files live (local path,
/// package entry) and recording durations (they are measured when a recording is played). Two copies of a
/// book with the same fingerprint therefore show the same music, whichever device or website they are on.
/// The website and the apps compute it with this same code, so it can be compared across them.
/// </summary>
public static class BookFingerprint
{
    public static string Compute(Book book)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("title", book.Title ?? "");

            w.WriteStartArray("documents");
            foreach (var d in book.Documents)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id);
                w.WriteString("name", d.Name);
                w.WriteNumber("pages", d.PageCount);
                WriteSource(w, d.Source);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("songs");
            foreach (var s in book.Songs.OrderBy(s => s.StartPage).ThenBy(s => s.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("title", s.Title);
                w.WriteNumber("start", s.StartPage);
                w.WriteNumber("pages", s.PageCount);
                w.WriteNumber("hl", s.HighlightHeight);
                w.WriteStartArray("recordings");
                foreach (var r in s.Recordings)
                {
                    w.WriteStartObject();
                    w.WriteString("id", r.Id);
                    w.WriteString("name", r.Name);
                    WriteSource(w, r.Source);
                    w.WriteStartArray("pageTimes");
                    foreach (var t in r.PageTimings.Where(t => t.PageOffset > 0).OrderBy(t => t.PageOffset))
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(t.PageOffset);
                        w.WriteNumberValue(t.TimeMs);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("markers");
                    foreach (var m in r.Markers.OrderBy(m => m.TimeMs).ThenBy(m => m.Id, StringComparer.Ordinal))
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(m.Id);
                        w.WriteNumberValue(m.TimeMs);
                        w.WriteNumberValue(m.PageOffset);
                        w.WriteNumberValue(m.X);
                        w.WriteNumberValue(m.Y);
                        w.WriteStringValue(m.Label ?? "");
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteSource(Utf8JsonWriter w, MediaSource src)
    {
        w.WriteString("file", src.FileName ?? "");
        if (src.Kind == MediaSourceKind.Url)
            w.WriteString("url", src.Url ?? ""); // the size of a download is only known after it happened
        else
            w.WriteNumber("size", src.SizeBytes);
    }

    /// <summary>True when the book was changed since it was last downloaded from or uploaded to its website.</summary>
    public static bool HasLocalChanges(Book book) =>
        book.Origin?.Fingerprint is { Length: > 0 } fp && !string.Equals(fp, Compute(book), StringComparison.OrdinalIgnoreCase);
}
