namespace PracticalMusic.Core.Models;

/// <summary>
/// Where a book came from when it was downloaded from (or uploaded to) a Practical Music website.
/// It is stored in book.json, so the link survives saving the book locally and sending it to other
/// devices: anyone who is a member of the same organization can check the website for a newer revision.
/// Older versions of the app simply ignore it.
/// </summary>
public sealed class OnlineOrigin
{
    /// <summary>Base address of the website, for example https://practicalmusic.russjudge.com</summary>
    public string Server { get; set; } = "";

    public long OrgId { get; set; }
    public string? OrgName { get; set; }

    /// <summary>The book's id on that website.</summary>
    public long BookId { get; set; }

    /// <summary>Revision of the book on the website when it was downloaded or uploaded.</summary>
    public int Revision { get; set; }

    /// <summary>
    /// <see cref="BookFingerprint"/> of that revision. When the fingerprint of the book in hand differs,
    /// the book was changed locally after it was downloaded or uploaded.
    /// </summary>
    public string? Fingerprint { get; set; }

    public DateTimeOffset? SyncedAt { get; set; }

    public bool IsServer(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) && string.Equals(NormalizeServer(Server), NormalizeServer(baseUrl), StringComparison.OrdinalIgnoreCase);

    /// <summary>"https://Host/" (or just "host") → "https://host": lower-case, no trailing slash.</summary>
    public static string NormalizeServer(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var s = url.Trim().TrimEnd('/');
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s; // same assumption as OnlineClient.TryNormalizeServer
        return Uri.TryCreate(s, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/')
            : s.ToLowerInvariant();
    }
}
