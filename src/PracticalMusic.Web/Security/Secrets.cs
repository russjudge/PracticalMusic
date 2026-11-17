using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Security;

/// <summary>Random tokens (sent by e-mail or handed to apps) and their stored hashes.</summary>
public static class Tokens
{
    /// <summary>256 random bits, URL-safe.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Only this hash is stored, so a copy of the database cannot be used to sign in.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>PBKDF2 password hashes (ASP.NET Core Identity's hasher: HMAC-SHA512, 100,000 iterations, per-user salt).</summary>
public sealed class PasswordHashing
{
    private readonly PasswordHasher<UserRow> _hasher = new();

    // A fixed, valid hash: verifying against it when the user does not exist makes both cases take the same time.
    private static readonly string DummyHash = new PasswordHasher<UserRow>().HashPassword(new UserRow(), "dummy password for timing");

    public string Hash(string password) => _hasher.HashPassword(new UserRow(), password);

    /// <summary>Returns (valid, needsRehash).</summary>
    public (bool Valid, bool NeedsRehash) Verify(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            _hasher.VerifyHashedPassword(new UserRow(), DummyHash, password);
            return (false, false);
        }
        try
        {
            var result = _hasher.VerifyHashedPassword(new UserRow(), hash, password);
            return (result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
        }
        catch (FormatException)
        {
            return (false, false);
        }
    }

    public void BurnTime(string password) => _hasher.VerifyHashedPassword(new UserRow(), DummyHash, password);

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "12345678", "123456789", "1234567890", "qwertyui", "qwerty123", "iloveyou",
        "11111111", "00000000", "abcdefgh", "abcd1234", "letmein1", "welcome1", "practicalmusic", "practical", "jesus123",
        "changeme", "sunshine", "princess", "football", "baseball", "superman", "trustno1", "passw0rd"
    };

    /// <summary>Null when the password is acceptable, otherwise the reason. Length matters more than character classes (NIST SP 800-63B).</summary>
    public static string? Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "Use at least 8 characters.";
        if (password.Length > 200) return "Use at most 200 characters.";
        if (password.Distinct().Count() < 3) return "That password is too easy to guess.";
        if (Common.Contains(password)) return "That password is too common. Choose another one - a short sentence works well.";
        if (!string.IsNullOrEmpty(email) && password.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase)) return "The password must not be your e-mail address.";
        return null;
    }
}
