using System.Text.Json;
using System.Text.Json.Serialization;
using PracticalMusic.Core.Models;

namespace PracticalMusic.Core.Online;

// Data exchanged with a Practical Music website. The website and the apps share these classes, so the
// protocol cannot drift apart; the browser client (wwwroot/js) mirrors the same camelCase JSON.

public static class OnlineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public static class OrgRoles
{
    /// <summary>Maintains the organization's books and media and manages its members.</summary>
    public const string Manager = "manager";
    /// <summary>Reads and plays the organization's books.</summary>
    public const string Member = "member";

    public static bool IsValid(string? role) => role is Manager or Member;
}

public sealed class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Shown on the website's "Devices" page so the user can revoke the token later.</summary>
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
}

public sealed class LoginResponse
{
    /// <summary>Opaque bearer token; send as "Authorization: Bearer …".</summary>
    public string Token { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public UserInfo User { get; set; } = new();
}

public sealed class UserInfo
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public bool IsSiteAdmin { get; set; }
    /// <summary>Organizations the user is an active member of.</summary>
    public List<OrgMembershipInfo> Organizations { get; set; } = new();
}

public sealed class OrgMembershipInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary><see cref="OrgRoles"/>; site administrators are reported as managers.</summary>
    public string Role { get; set; } = OrgRoles.Member;
    /// <summary>True for managers (and site administrators): may upload and edit books.</summary>
    public bool CanEdit { get; set; }
}

public sealed class BookSummary
{
    public long Id { get; set; }
    public long OrgId { get; set; }
    /// <summary><see cref="Book.Id"/> inside book.json.</summary>
    public string Uid { get; set; } = "";
    public string Title { get; set; } = "";
    public int Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public int SongCount { get; set; }
    public long MediaBytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class MediaInfo
{
    public long Id { get; set; }
    /// <summary>"pdf" or "audio".</summary>
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? PageCount { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>A book as the web client edits it: book.json plus where each embedded media entry lives on the server.</summary>
public sealed class BookDocument
{
    public long Id { get; set; }
    public long OrgId { get; set; }
    public string OrgName { get; set; } = "";
    public int Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public bool CanEdit { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public Book Book { get; set; } = new();
    /// <summary>Package entry name (MediaSource.EntryName) → media stored on the server.</summary>
    public Dictionary<string, MediaInfo> Media { get; set; } = new();
}

/// <summary>Create or update a book from the web editor.</summary>
public sealed class SaveBookRequest
{
    public Book Book { get; set; } = new();
    /// <summary>Package entry name → media id returned by the upload endpoint.</summary>
    public Dictionary<string, long> Media { get; set; } = new();
    /// <summary>The revision the edit started from; a different current revision is a conflict (HTTP 409) unless Force is set.</summary>
    public int? BaseRevision { get; set; }
    public bool Force { get; set; }
}

public sealed class SaveBookResult
{
    public long BookId { get; set; }
    /// <summary>The book's identity (book.json "id") as stored. Differs from the upload's when it was stored as a new book.</summary>
    public string Uid { get; set; } = "";
    public long OrgId { get; set; }
    public string Title { get; set; } = "";
    public int Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public bool Created { get; set; }
    /// <summary>The content was identical to the current revision, so no new revision was made.</summary>
    public bool Unchanged { get; set; }
}

public sealed class ApiError
{
    public string Error { get; set; } = "";
    /// <summary>Machine-readable reason, for example "conflict", "forbidden", "not_found", "invalid".</summary>
    public string? Code { get; set; }
    /// <summary>For conflicts: the revision currently on the server.</summary>
    public int? CurrentRevision { get; set; }
}

/// <summary>Response headers of the package download.</summary>
public static class OnlineHeaders
{
    public const string Revision = "X-PM-Revision";
    public const string Fingerprint = "X-PM-Fingerprint";
}
