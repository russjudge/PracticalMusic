using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PracticalMusic.Core.Online;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using Xunit;

namespace PracticalMusic.Web.Tests;

/// <summary>
/// Runs the real website in-process. By default with the in-memory store; set PM_TEST_MARIADB to a connection
/// string of an empty test database that has database/02-schema.sql applied to run the same tests against MariaDB.
/// </summary>
public sealed class WebFixture : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "pmweb_" + Guid.NewGuid().ToString("N"));
    public static string? MariaDb => Environment.GetEnvironmentVariable("PM_TEST_MARIADB");

    static WebFixture()
    {
        // Program reads these before the host is built, so they are set as environment variables (process-wide, once).
        var mariaDb = MariaDb;
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Database__Provider", string.IsNullOrEmpty(mariaDb) ? "Memory" : "MariaDb");
        if (!string.IsNullOrEmpty(mariaDb)) Environment.SetEnvironmentVariable("PRACTICALMUSIC_ConnectionStrings__Default", mariaDb);
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Demo__Seed", "false");
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Email__Mode", "Disabled");
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Site__RequireEmailVerification", "true");
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Site__BaseUrl", "https://pm.test");
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Site__AuthRequestsPerMinute", "100000"); // every test request comes from one address
    }

    public WebFixture()
    {
        Environment.SetEnvironmentVariable("PRACTICALMUSIC_Site__DataDir", DataDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, recursive: true); } catch { /* files may still be open */ }
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    public static string Unique(string prefix) => $"{prefix}{Guid.NewGuid().ToString("N")[..8]}";

    // ------------------------------------------------------------------ people and organizations

    /// <summary>Creates a confirmed user directly in storage.</summary>
    public async Task<UserRow> CreateUserAsync(string? email = null, string password = "correct horse battery", bool admin = false, string? name = null)
    {
        email ??= Unique("user") + "@example.com";
        var users = Service<IUserRepository>();
        var now = DateTime.UtcNow;
        var id = await users.CreateAsync(new UserRow
        {
            Email = email, FullName = name ?? email.Split('@')[0], PasswordHash = Service<PasswordHashing>().Hash(password), IsSiteAdmin = admin,
            EmailVerifiedAt = now, SecurityStamp = Tokens.NewSecurityStamp(), CreatedAt = now, UpdatedAt = now
        });
        return (await users.GetAsync(id))!;
    }

    public async Task<OrgRow> CreateOrgAsync(string? name = null)
    {
        var orgs = Service<IOrganizationRepository>();
        var id = await orgs.CreateAsync(new OrgRow { Name = name ?? Unique("Org "), Kind = "church", AllowJoinRequests = true, IsActive = true, CreatedAt = DateTime.UtcNow });
        return (await orgs.GetAsync(id))!;
    }

    public async Task AddMemberAsync(long userId, long orgId, string role)
    {
        await Service<IOrganizationRepository>().UpsertMembershipAsync(new MembershipRow
        {
            UserId = userId, OrgId = orgId, Role = role, Status = MembershipStatus.Active, RequestedAt = DateTime.UtcNow, DecidedAt = DateTime.UtcNow
        });
        Service<UserCache>().Invalidate(userId);
    }

    /// <summary>A client signed in the way the apps sign in (bearer token).</summary>
    public async Task<HttpClient> AppClientAsync(string email, string password = "correct horse battery")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, DeviceName = "Test", Platform = "Tests" }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(OnlineJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    /// <summary>A browser-like client with cookies, not following redirects.</summary>
    public HttpClient BrowserClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>The anti-forgery token of a page's first form.</summary>
    public static async Task<string> FormTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!m.Success) m = Regex.Match(html, "<meta name=\"csrf-token\" content=\"([^\"]+)\"");
        Assert.True(m.Success, "no anti-forgery token on " + url);
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        var token = await FormTokenAsync(client, tokenFrom ?? url);
        fields["__RequestVerificationToken"] = token;
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    public async Task<HttpClient> SignedInBrowserAsync(string email, string password = "correct horse battery")
    {
        var client = BrowserClient();
        var response = await PostFormAsync(client, "/Account/Login", new() { ["Email"] = email, ["Password"] = password, ["RememberMe"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>The newest e-mail to an address (the outbox; nothing is sent in tests).</summary>
    public async Task<EmailRow?> LastEmailToAsync(string address)
    {
        var outbox = Service<IEmailOutboxRepository>();
        foreach (var e in await outbox.ListRecentAsync(500, null))
            if (string.Equals(e.ToAddress, address, StringComparison.OrdinalIgnoreCase))
                return await outbox.GetAsync(e.Id);
        return null;
    }

    public static string LinkWithToken(EmailRow email, string path)
    {
        var m = Regex.Match(email.BodyText, Regex.Escape(path) + @"\?token=([A-Za-z0-9_%\-]+)");
        Assert.True(m.Success, $"no {path} link in \"{email.Subject}\"");
        return path + "?token=" + m.Groups[1].Value;
    }

    // ------------------------------------------------------------------ files

    public static byte[] MinimalPdf(int pages = 2)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = "";
        for (var i = 0; i < pages; i++)
        {
            var content = $"BT /F1 24 Tf 72 700 Td (Page {i + 1}) Tj ET 72 600 m 540 600 l S";
            kids += $"{objects.Count + 1} 0 R ";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Bytes that look like an MP3 (ID3 header) - enough for the upload checks.</summary>
    public static byte[] FakeMp3(int size = 4096, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        bytes[0] = (byte)'I'; bytes[1] = (byte)'D'; bytes[2] = (byte)'3';
        return bytes;
    }

    public static async Task<MediaInfo> UploadAsync(HttpClient client, long orgId, byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        var response = await client.PostAsync($"/api/orgs/{orgId}/media", form);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MediaInfo>(OnlineJson.Options))!;
    }
}

[CollectionDefinition("web")]
public sealed class WebCollection : ICollectionFixture<WebFixture>;
