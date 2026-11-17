using System.Text.Json;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.Services;

/// <summary>
/// Remembers which books were downloaded from (or uploaded to) a website and where the local copy is, so the
/// Online page can show "on this device", "update available" or "changed on this device" without opening each file.
/// Stored as online/index.json in the app data folder.
/// </summary>
public sealed class OnlineLibrary
{
    public sealed class Entry
    {
        public string Server { get; set; } = "";
        public long OrgId { get; set; }
        public long BookId { get; set; }
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        public int Revision { get; set; }
    }

    private readonly string _file;
    private List<Entry> _entries = new();

    public OnlineLibrary(string onlineDir)
    {
        _file = System.IO.Path.Combine(onlineDir, "index.json");
        try
        {
            if (File.Exists(_file)) _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? new();
        }
        catch { _entries = new(); }
    }

    public Entry? Find(string server, long bookId) =>
        _entries.FirstOrDefault(e => e.BookId == bookId && OnlineOrigin.NormalizeServer(e.Server) == OnlineOrigin.NormalizeServer(server) && File.Exists(e.Path));

    public void Remember(OnlineOrigin origin, string title, string path)
    {
        _entries.RemoveAll(e => e.BookId == origin.BookId && OnlineOrigin.NormalizeServer(e.Server) == OnlineOrigin.NormalizeServer(origin.Server));
        _entries.Add(new Entry { Server = OnlineOrigin.NormalizeServer(origin.Server), OrgId = origin.OrgId, BookId = origin.BookId, Title = title, Path = path, Revision = origin.Revision });
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* only a convenience */ }
    }

    /// <summary>Where a downloaded book is kept: online/&lt;host&gt;/&lt;organization&gt;/&lt;title&gt;-&lt;id&gt;.pmbook</summary>
    public static string PathFor(string onlineDir, string server, long orgId, long bookId, string title)
    {
        var host = Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host + (uri.IsDefaultPort ? "" : "_" + uri.Port) : "server";
        return System.IO.Path.Combine(onlineDir, BookPackage.Sanitize(host), orgId.ToString(), $"{BookPackage.Sanitize(title)}-{bookId}{BookPackage.Extension}");
    }

    /// <summary>True when the local copy was edited after it was downloaded/uploaded (reads book.json only).</summary>
    public static bool HasLocalChanges(string path)
    {
        try { return BookFingerprint.HasLocalChanges(BookPackage.ReadManifest(path)); }
        catch { return false; }
    }
}
