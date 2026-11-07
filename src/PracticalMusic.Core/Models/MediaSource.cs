using System.Text.Json.Serialization;

namespace PracticalMusic.Core.Models;

/// <summary>Where a PDF or MP3 lives.</summary>
public enum MediaSourceKind
{
    /// <summary>A file on the local machine (absolute path).</summary>
    File,
    /// <summary>An http(s) URL which is downloaded into the cache on demand.</summary>
    Url,
    /// <summary>Bytes embedded inside the .pmbook package.</summary>
    Embedded
}

/// <summary>Reference to a media file (PDF or audio).</summary>
public sealed class MediaSource
{
    public MediaSourceKind Kind { get; set; } = MediaSourceKind.File;

    /// <summary>Absolute local path (Kind == File).</summary>
    public string? Path { get; set; }

    /// <summary>Download URL (Kind == Url).</summary>
    public string? Url { get; set; }

    /// <summary>Zip entry name inside the package (Kind == Embedded).</summary>
    public string? EntryName { get; set; }

    /// <summary>Original file name, used for display and when embedding.</summary>
    public string FileName { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>
    /// Runtime only: a local path that can actually be opened right now.
    /// Set by the package loader / media resolver. Never serialized.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedPath { get; set; }

    [JsonIgnore]
    public bool IsResolved => !string.IsNullOrEmpty(ResolvedPath) && System.IO.File.Exists(ResolvedPath);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(FileName)
        ? (Path ?? Url ?? EntryName ?? "(unnamed)")
        : FileName;

    public static MediaSource FromFile(string path)
    {
        var fi = new FileInfo(path);
        return new MediaSource
        {
            Kind = MediaSourceKind.File,
            Path = fi.FullName,
            FileName = fi.Name,
            SizeBytes = fi.Exists ? fi.Length : 0,
            ResolvedPath = fi.Exists ? fi.FullName : null
        };
    }

    public static MediaSource FromUrl(string url)
    {
        var name = url;
        try { name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath); } catch { /* keep url */ }
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        return new MediaSource { Kind = MediaSourceKind.Url, Url = url, FileName = name };
    }

    public MediaSource Clone() => new()
    {
        Kind = Kind, Path = Path, Url = Url, EntryName = EntryName,
        FileName = FileName, SizeBytes = SizeBytes, ResolvedPath = ResolvedPath
    };
}
