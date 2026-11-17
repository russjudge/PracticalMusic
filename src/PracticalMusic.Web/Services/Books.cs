using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Services;

/// <summary>
/// Stores books. Every save - from the web editor or an uploaded .pmbook - goes through
/// <see cref="SaveAsync"/>, which validates and canonicalizes the book, checks that all media belong to the
/// organization, and creates a new revision when the content changed.
/// </summary>
public sealed class BookService(IBookRepository books, IMediaRepository media, Audit audit)
{
    public const int MaxDocuments = 100;
    public const int MaxSongs = 1000;
    public const int MaxRecordingsPerSong = 50;
    public const int MaxMarkersPerRecording = 20_000;
    /// <summary>Stored book.json limit (MariaDB's default max_allowed_packet is 16 MB).</summary>
    public const int MaxBookJsonChars = 12 * 1024 * 1024;

    public static BookSummary ToSummary(BookRow b) => new()
    {
        Id = b.Id, OrgId = b.OrgId, Uid = b.BookUid, Title = b.Title, Revision = b.Revision, Fingerprint = b.Fingerprint,
        SongCount = b.SongCount, MediaBytes = b.MediaBytes, UpdatedAt = new DateTimeOffset(b.UpdatedAt, TimeSpan.Zero), UpdatedBy = b.UpdatedByName
    };

    public static MediaInfo ToInfo(MediaRow m) => new()
    {
        Id = m.Id, Kind = m.Kind, FileName = m.FileName, ContentType = m.ContentType, SizeBytes = m.SizeBytes, PageCount = m.PageCount, Sha256 = m.Sha256
    };

    public async Task<BookDocument> GetDocumentAsync(BookRow row, string orgName, bool canEdit)
    {
        var book = BookJson.Deserialize(row.BookJson);
        var map = await books.GetMediaAsync(row.Id);
        return new BookDocument
        {
            Id = row.Id, OrgId = row.OrgId, OrgName = orgName, Revision = row.Revision, Fingerprint = row.Fingerprint, CanEdit = canEdit,
            UpdatedAt = new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero), UpdatedBy = row.UpdatedByName,
            Book = book,
            Media = map.ToDictionary(kv => kv.Key, kv => ToInfo(kv.Value), StringComparer.Ordinal)
        };
    }

    /// <param name="bookId">The book to update, or null to create one.</param>
    /// <param name="mediaRefs">Entry name (MediaSource.EntryName) → media id, for every embedded source.</param>
    /// <param name="baseRevision">The revision the edit started from. Required (unless <paramref name="force"/>) to update a book.</param>
    public async Task<SaveBookResult> SaveAsync(long orgId, long? bookId, Book input, IReadOnlyDictionary<string, long> mediaRefs,
        int? baseRevision, bool force, long userId)
    {
        BookRow? existing = null;
        if (bookId != null)
        {
            existing = await books.GetAsync(bookId.Value);
            if (existing == null || existing.OrgId != orgId) throw UserFacingException.NotFound("Book not found.");
        }

        var mediaById = await LoadOrgMediaAsync(orgId, mediaRefs);
        var (book, entries, mediaBytes) = Canonicalize(input, mediaRefs, mediaById);

        var now = DateTime.UtcNow;
        if (existing != null)
        {
            book.Id = existing.BookUid; // a book keeps its identity
            if (!force && baseRevision != existing.Revision)
                throw new UserFacingException(
                    $"\"{existing.Title}\" was changed on the website{(existing.UpdatedByName is { } who ? " by " + who : "")} since this copy was taken " +
                    $"(it is now revision {existing.Revision}{(baseRevision is { } b ? $", this copy is based on revision {b}" : "")}).",
                    StatusCodes.Status409Conflict, "conflict", existing.Revision);
        }
        else
        {
            if (!IsUid(book.Id)) book.Id = Guid.NewGuid().ToString("N");
            var clash = await books.FindByUidAsync(orgId, book.Id);
            if (clash != null)
                throw new UserFacingException($"This organization already has this book (\"{clash.Title}\", revision {clash.Revision}).",
                    StatusCodes.Status409Conflict, "exists", clash.Revision);
        }

        var fingerprint = BookFingerprint.Compute(book);
        var json = BookJson.SerializeCompact(book);
        if (json.Length > MaxBookJsonChars)
            throw new UserFacingException("This book is too large to store (too many songs, recordings or markers). Split it into several books.");
        // unchanged = same content AND the same stored files (a replaced file with the same name and size is a change)
        if (existing != null && fingerprint == existing.Fingerprint && SameMedia(await books.GetMediaAsync(existing.Id), entries))
            return new SaveBookResult
            {
                BookId = existing.Id, Uid = existing.BookUid, OrgId = orgId, Title = existing.Title, Revision = existing.Revision, Fingerprint = fingerprint, Unchanged = true
            };

        var row = new BookRow
        {
            Id = existing?.Id ?? 0,
            OrgId = orgId,
            BookUid = book.Id,
            Title = book.Title,
            Revision = (existing?.Revision ?? 0) + 1,
            Fingerprint = fingerprint,
            BookJson = json,
            SongCount = book.Songs.Count,
            MediaBytes = mediaBytes,
            CreatedAt = existing?.CreatedAt ?? now,
            CreatedBy = existing?.CreatedBy ?? userId,
            UpdatedAt = now,
            UpdatedBy = userId
        };
        if (existing == null)
        {
            row.Id = await books.InsertAsync(row, entries);
            await audit.WriteAsync("book.created", $"\"{row.Title}\"", userId, orgId);
        }
        else
        {
            if (!await books.UpdateAsync(row, existing.Revision, entries))
                throw new UserFacingException("Someone else saved this book at the same moment. Open it again and repeat your change.",
                    StatusCodes.Status409Conflict, "conflict", existing.Revision + 1);
            await audit.WriteAsync("book.updated", $"\"{row.Title}\" revision {row.Revision}", userId, orgId);
        }
        return new SaveBookResult
        {
            BookId = row.Id, Uid = row.BookUid, OrgId = orgId, Title = row.Title, Revision = row.Revision, Fingerprint = fingerprint, Created = existing == null
        };
    }

    private static bool SameMedia(IReadOnlyDictionary<string, MediaRow> stored, IReadOnlyDictionary<string, long> entries) =>
        stored.Count == entries.Count && entries.All(kv => stored.TryGetValue(kv.Key, out var m) && m.Id == kv.Value);

    private async Task<Dictionary<long, MediaRow>> LoadOrgMediaAsync(long orgId, IReadOnlyDictionary<string, long> refs)
    {
        var rows = await media.GetManyAsync(refs.Values.Distinct().ToList());
        if (rows.Any(r => r.OrgId != orgId)) throw UserFacingException.Forbidden("The book refers to files of another organization.");
        return rows.ToDictionary(r => r.Id);
    }

    private static bool IsUid(string? id) => id is { Length: >= 8 and <= 32 } && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// A clean copy: limits enforced, ids filled in, page counts from the stored PDFs, local paths removed,
    /// entry names of the form "media/&lt;file&gt;", songs normalized. Throws <see cref="UserFacingException"/>.
    /// </summary>
    public static (Book Book, Dictionary<string, long> Entries, long MediaBytes) Canonicalize(Book input,
        IReadOnlyDictionary<string, long> mediaRefs, IReadOnlyDictionary<long, MediaRow> mediaById)
    {
        var book = BookJson.Clone(input);
        book.Origin = null;
        book.FormatVersion = Book.CurrentFormatVersion;
        book.Modified = DateTimeOffset.UtcNow;
        book.Title = Text.Clean(book.Title, 200) ?? "Untitled Book";
        if (book.Documents.Count > MaxDocuments) throw new UserFacingException($"A book can have at most {MaxDocuments} PDF files.");
        if (book.Songs.Count > MaxSongs) throw new UserFacingException($"A book can have at most {MaxSongs} songs.");

        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        var nameOf = new Dictionary<long, string>();        // media id → canonical entry name
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        MediaRow MapEmbedded(MediaSource src, string kind, string what)
        {
            if (src.Kind != MediaSourceKind.Embedded || string.IsNullOrEmpty(src.EntryName) ||
                !mediaRefs.TryGetValue(src.EntryName, out var mediaId) || !mediaById.TryGetValue(mediaId, out var m))
                throw new UserFacingException($"{what} \"{src.DisplayName}\" is not stored on this website yet. Upload the file (or save the book with media) and try again.");
            if (m.Kind != kind)
                throw new UserFacingException($"{what} \"{src.DisplayName}\" is not {(kind == MediaKinds.Pdf ? "a PDF" : "an audio file")}.");
            if (!nameOf.TryGetValue(m.Id, out var entry))
            {
                var baseName = MediaStorage.CleanFileName(string.IsNullOrWhiteSpace(src.FileName) ? m.FileName : src.FileName);
                entry = "media/" + baseName;
                for (var n = 2; !usedNames.Add(entry); n++)
                    entry = $"media/{Path.GetFileNameWithoutExtension(baseName)}_{n}{Path.GetExtension(baseName)}";
                nameOf[m.Id] = entry;
                entries[entry] = m.Id;
            }
            src.EntryName = entry;
            src.Path = null;
            src.Url = null;
            src.SizeBytes = m.SizeBytes;
            if (string.IsNullOrWhiteSpace(src.FileName)) src.FileName = m.FileName;
            src.FileName = Text.Clean(src.FileName, 255) ?? m.FileName;
            return m;
        }

        foreach (var doc in book.Documents)
        {
            if (!IsUid(doc.Id)) doc.Id = Guid.NewGuid().ToString("N");
            if (doc.Source.Kind == MediaSourceKind.Url)
                throw new UserFacingException($"The PDF \"{doc.Source.DisplayName}\" is a web link. Upload the PDF file itself.");
            var m = MapEmbedded(doc.Source, MediaKinds.Pdf, "The PDF");
            doc.PageCount = m.PageCount ?? 0;
            doc.Name = Text.Clean(doc.Name, 200) ?? Path.GetFileNameWithoutExtension(m.FileName);
        }

        foreach (var song in book.Songs)
        {
            if (!IsUid(song.Id)) song.Id = Guid.NewGuid().ToString("N");
            song.Title = Text.Clean(song.Title, 200) ?? "Untitled song";
            song.HighlightHeight = Finite(song.HighlightHeight, 0.08, 0.01, 0.5);
            if (song.Recordings.Count > MaxRecordingsPerSong) throw new UserFacingException($"A song can have at most {MaxRecordingsPerSong} recordings.");
            foreach (var rec in song.Recordings)
            {
                if (!IsUid(rec.Id)) rec.Id = Guid.NewGuid().ToString("N");
                rec.Name = Text.Clean(rec.Name, 200) ?? "Recording";
                if (rec.Source.Kind == MediaSourceKind.Url)
                {
                    if (!Uri.TryCreate(rec.Source.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || rec.Source.Url!.Length > 2000)
                        throw new UserFacingException($"The recording \"{rec.Name}\" has an invalid web address.");
                    rec.Source.Path = null;
                    rec.Source.EntryName = null;
                    rec.Source.FileName = Text.Clean(rec.Source.FileName, 255) ?? "recording";
                }
                else
                {
                    MapEmbedded(rec.Source, MediaKinds.Audio, "The recording");
                }
                rec.DurationMs = Finite(rec.DurationMs, 0, 0, 86_400_000);
                rec.PageTimings = rec.PageTimings
                    .Where(t => t.PageOffset >= 0 && double.IsFinite(t.TimeMs))
                    .GroupBy(t => t.PageOffset).Select(g => g.First())
                    .Select(t => new PageTiming { PageOffset = t.PageOffset, TimeMs = Math.Max(0, t.TimeMs) })
                    .ToList();
                if (rec.Markers.Count > MaxMarkersPerRecording) throw new UserFacingException($"A recording can have at most {MaxMarkersPerRecording} markers.");
                foreach (var m in rec.Markers)
                {
                    if (!IsUid(m.Id)) m.Id = Guid.NewGuid().ToString("N");
                    m.TimeMs = Finite(m.TimeMs, 0, 0, 86_400_000);
                    m.X = Finite(m.X, 0, 0, 1);
                    m.Y = Finite(m.Y, 0, 0, 1);
                    m.PageOffset = Math.Max(0, m.PageOffset);
                    m.Label = Text.Clean(m.Label, 100);
                }
            }
        }
        book.NormalizeSongs();
        var bytes = entries.Values.Distinct().Sum(id => mediaById[id].SizeBytes);
        return (book, entries, bytes);
    }

    private static double Finite(double v, double fallback, double min, double max) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>.pmbook files: importing an upload (from the apps or the browser) and building downloads.</summary>
public sealed class PackageService(BookService bookService, IBookRepository books, MediaStorage storage, DataPaths paths,
    SiteUrls urls, IOptions<SiteOptions> site)
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> BuildLocks = new();

    private long MaxBytes => site.Value.MaxUploadMegabytes * 1024L * 1024L;

    /// <summary>
    /// Imports a self-contained .pmbook. When the organization already has a book with the same identity
    /// (book.json "id") it is updated - <paramref name="baseRevision"/> must then match the current revision
    /// unless <paramref name="force"/> is set; <paramref name="asNew"/> stores it as a separate book instead.
    /// </summary>
    public async Task<SaveBookResult> ImportAsync(long orgId, Stream body, int? baseRevision, bool force, bool asNew, long userId, CancellationToken ct)
    {
        var temp = paths.NewTempFile(".pmbook");
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                        throw new UserFacingException($"The book is larger than the {site.Value.MaxUploadMegabytes} MB this website accepts.", StatusCodes.Status413PayloadTooLarge, "too_large");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            ZipArchive zip;
            try { zip = ZipFile.OpenRead(temp); }
            catch (InvalidDataException) { throw new UserFacingException("That file is not a Practical Music book (.pmbook)."); }
            using (zip)
            {
                var manifest = zip.GetEntry(BookPackage.ManifestEntry)
                               ?? throw new UserFacingException("That file is not a Practical Music book (book.json is missing).");
                if (manifest.Length > 32L * 1024 * 1024) throw new UserFacingException("book.json is unreasonably large.");
                Book book;
                try
                {
                    using var reader = new StreamReader(manifest.Open());
                    book = BookJson.Deserialize(await reader.ReadToEndAsync(ct));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
                {
                    throw new UserFacingException("The book's book.json is damaged: " + ex.Message);
                }

                var refs = new Dictionary<string, long>(StringComparer.Ordinal);
                var missing = new List<string>();
                long declared = 0;
                foreach (var src in book.AllMediaSources())
                {
                    if (src.Kind == MediaSourceKind.Url) continue;
                    if (src.Kind != MediaSourceKind.Embedded || string.IsNullOrEmpty(src.EntryName))
                    {
                        missing.Add(src.DisplayName);
                        continue;
                    }
                    if (refs.ContainsKey(src.EntryName)) continue;
                    var entry = zip.GetEntry(src.EntryName);
                    if (entry == null)
                    {
                        missing.Add(src.DisplayName);
                        continue;
                    }
                    declared += entry.Length;
                    if (declared > MaxBytes * 2) throw new UserFacingException("The book's files are too large in total.", StatusCodes.Status413PayloadTooLarge, "too_large");
                    await using var stream = new LimitedStream(entry.Open(), entry.Length);
                    var row = await storage.ImportAsync(orgId, stream, string.IsNullOrWhiteSpace(src.FileName) ? Path.GetFileName(src.EntryName) : src.FileName, MaxBytes, userId, ct);
                    refs[src.EntryName] = row.Id;
                }
                if (missing.Count > 0)
                    throw new UserFacingException(
                        $"The book refers to {missing.Count} file(s) that are not inside it ({string.Join(", ", missing.Distinct().Take(4))}{(missing.Count > 4 ? ", …" : "")}). " +
                        "In the app use \"Save (with media)\" (or Locate file… for missing recordings) and upload again.");

                long? bookId = null;
                if (asNew) book.Id = Guid.NewGuid().ToString("N");
                else
                {
                    // a book downloaded from (or uploaded to) this website names its server book; otherwise match the identity
                    if (book.Origin is { } origin && origin.OrgId == orgId && origin.IsServer(urls.BaseUrl) &&
                        await books.GetAsync(origin.BookId) is { } linked && linked.OrgId == orgId)
                        bookId = linked.Id;
                    else if (!string.IsNullOrWhiteSpace(book.Id))
                        bookId = (await books.FindByUidAsync(orgId, book.Id))?.Id;
                }

                if (bookId != null && baseRevision == null && !force && !asNew &&
                    book.Origin is { } fromHere && fromHere.BookId == bookId && fromHere.IsServer(urls.BaseUrl))
                    baseRevision = fromHere.Revision; // the copy says which revision it was taken from

                if (bookId != null && baseRevision == null && !force)
                {
                    var existing = await books.GetAsync(bookId.Value);
                    throw new UserFacingException(
                        $"This organization already has this book (\"{existing?.Title}\", revision {existing?.Revision}).", // each client offers its own choices
                        StatusCodes.Status409Conflict, "exists", existing?.Revision);
                }
                return await bookService.SaveAsync(orgId, bookId, book, refs, baseRevision, force, userId);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// A self-contained .pmbook of the current revision (cached). Its book.json carries an <see cref="OnlineOrigin"/>
    /// so the apps can later check for a newer revision and upload changes back.
    /// </summary>
    public async Task<string> GetPackageAsync(BookRow row, OrgRow org, CancellationToken ct)
    {
        var path = Path.Combine(paths.Packages, $"{row.Id}-r{row.Revision}-{row.Fingerprint[..12]}.pmbook");
        if (File.Exists(path)) return path;
        var gate = BuildLocks.GetOrAdd(row.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path;
            var book = BookJson.Deserialize(row.BookJson);
            book.Origin = new OnlineOrigin
            {
                Server = urls.BaseUrl,
                OrgId = org.Id,
                OrgName = org.Name,
                BookId = row.Id,
                Revision = row.Revision,
                Fingerprint = row.Fingerprint,
                SyncedAt = new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero)
            };
            var mediaMap = await books.GetMediaAsync(row.Id);
            Directory.CreateDirectory(paths.Packages);
            var temp = paths.NewTempFile(".pmbook");
            try
            {
                await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var (entryName, m) in mediaMap.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        var level = m.Kind == MediaKinds.Pdf ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                        var entry = zip.CreateEntry(entryName, level);
                        await using var es = entry.Open();
                        await using var input = File.OpenRead(storage.FilePath(m));
                        await input.CopyToAsync(es, ct);
                    }
                    var manifest = zip.CreateEntry(BookPackage.ManifestEntry, CompressionLevel.Optimal);
                    await using var ms = manifest.Open();
                    await using var writer = new StreamWriter(ms);
                    await writer.WriteAsync(BookJson.Serialize(book));
                }
                var now = await books.GetAsync(row.Id);
                if (now == null || now.Revision != row.Revision)
                    throw new UserFacingException("The book was just changed. Download it again.", StatusCodes.Status409Conflict, "conflict", now?.Revision);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
            foreach (var old in Directory.EnumerateFiles(paths.Packages, $"{row.Id}-r*.pmbook").Where(f => !string.Equals(f, path, StringComparison.Ordinal)))
            {
                try { File.Delete(old); } catch { /* in use by a download; the maintenance task retries */ }
            }
            return path;
        }
        finally
        {
            gate.Release();
        }
    }

    public static string DownloadName(string title) => BookPackage.Sanitize(title) + BookPackage.Extension;

    /// <summary>Refuses to deliver more bytes than a zip entry declares (defends against zip bombs).</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => limit;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int n)
        {
            _read += n;
            if (_read > limit) throw new UserFacingException("A file inside the book is larger than it claims to be.");
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
