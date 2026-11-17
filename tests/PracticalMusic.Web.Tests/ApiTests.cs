using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Online;
using PracticalMusic.Core.Packaging;
using Xunit;

namespace PracticalMusic.Web.Tests;

[Collection("web")]
public sealed class ApiTests(WebFixture web)
{
    private sealed record Setup(PracticalMusic.Web.Data.OrgRow Org, HttpClient Manager, HttpClient Member, HttpClient Outsider);

    private async Task<Setup> SetupAsync()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync();
        var member = await web.CreateUserAsync();
        var outsider = await web.CreateUserAsync();
        await web.AddMemberAsync(manager.Id, org.Id, "manager");
        await web.AddMemberAsync(member.Id, org.Id, "member");
        return new Setup(org, await web.AppClientAsync(manager.Email), await web.AppClientAsync(member.Email), await web.AppClientAsync(outsider.Email));
    }

    /// <summary>A book as the web editor builds it: uploaded media + book JSON.</summary>
    private static async Task<(Book Book, Dictionary<string, long> Media, MediaInfo Pdf, MediaInfo Audio)> NewBookAsync(HttpClient client, long orgId, string title = "Sunday")
    {
        var pdf = await WebFixture.UploadAsync(client, orgId, WebFixture.MinimalPdf(3), "Charts.pdf");
        var audio = await WebFixture.UploadAsync(client, orgId, WebFixture.FakeMp3(seed: Random.Shared.Next()), "Amazing Grace.mp3");
        var book = new Book { Title = title };
        book.Documents.Add(new PdfDocumentRef { Name = "Charts", PageCount = 99, Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "media/Charts.pdf", FileName = "Charts.pdf" } });
        var rec = new Recording { Name = "Band", Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "media/Amazing Grace.mp3", FileName = "Amazing Grace.mp3" } };
        rec.PageTimings.Add(new PageTiming { PageOffset = 1, TimeMs = 30000 });
        rec.Markers.Add(new TimeMarker { PageOffset = 0, TimeMs = 1000, X = 0.2, Y = 0.3 });
        book.Songs.Add(new Song { Title = "Amazing Grace", StartPage = 0, PageCount = 2, Recordings = { rec } });
        return (book, new Dictionary<string, long> { ["media/Charts.pdf"] = pdf.Id, ["media/Amazing Grace.mp3"] = audio.Id }, pdf, audio);
    }

    [Fact]
    public async Task Apps_sign_in_and_see_their_organizations()
    {
        var s = await SetupAsync();
        var me = (await s.Member.GetFromJsonAsync<UserInfo>("/api/me", OnlineJson.Options))!;
        var org = Assert.Single(me.Organizations);
        Assert.Equal(s.Org.Id, org.Id);
        Assert.False(org.CanEdit);
        var managerOrg = Assert.Single((await s.Manager.GetFromJsonAsync<UserInfo>("/api/me", OnlineJson.Options))!.Organizations);
        Assert.True(managerOrg.CanEdit);

        var bad = await web.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = "nobody@example.com", Password = "x" }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("invalid_login", (await bad.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!.Code);

        // signing out revokes the token
        Assert.Equal(HttpStatusCode.NoContent, (await s.Outsider.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Outsider.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Managers_create_books_that_members_can_play_but_not_change()
    {
        var s = await SetupAsync();
        var (book, media, pdf, audio) = await NewBookAsync(s.Manager, s.Org.Id);
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal("audio", audio.Kind);

        var created = await s.Manager.PostAsJsonAsync($"/api/orgs/{s.Org.Id}/books", new SaveBookRequest { Book = book, Media = media }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = (await created.Content.ReadFromJsonAsync<SaveBookResult>(OnlineJson.Options))!;
        Assert.Equal(1, result.Revision);

        // canonical: page count from the stored PDF, no local paths
        var doc = (await s.Member.GetFromJsonAsync<BookDocument>($"/api/books/{result.BookId}", OnlineJson.Options))!;
        Assert.False(doc.CanEdit);
        Assert.Equal(3, doc.Book.Documents[0].PageCount);
        Assert.Equal(2, doc.Media.Count);
        Assert.All(doc.Book.AllMediaSources(), m => Assert.Null(m.Path));

        // members: list, play (range requests), page images
        var list = (await s.Member.GetFromJsonAsync<List<BookSummary>>($"/api/orgs/{s.Org.Id}/books", OnlineJson.Options))!;
        Assert.Contains(list, b => b.Id == result.BookId && b.SongCount == 1);
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{audio.Id}/file");
        range.Headers.Range = new RangeHeaderValue(0, 99);
        var partial = await s.Member.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(100, (await partial.Content.ReadAsByteArrayAsync()).Length);
        var page = await s.Member.GetAsync($"/api/media/{pdf.Id}/pages/1?w=500");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/png", page.Content.Headers.ContentType!.MediaType);
        var png = await page.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, png[..4]);
        Assert.Equal(640, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))); // 500 → 640 bucket
        Assert.Equal(HttpStatusCode.NotFound, (await s.Member.GetAsync($"/api/media/{pdf.Id}/pages/3")).StatusCode);

        // members cannot change anything
        doc.Book.Title = "Hacked";
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Member.PutAsJsonAsync($"/api/books/{result.BookId}", new SaveBookRequest { Book = doc.Book, Media = media, BaseRevision = 1 }, OnlineJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Member.DeleteAsync($"/api/books/{result.BookId}")).StatusCode);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(WebFixture.MinimalPdf()), "file", "x.pdf" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Member.PostAsync($"/api/orgs/{s.Org.Id}/media", form)).StatusCode);

        // outsiders cannot even look
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.GetAsync($"/api/books/{result.BookId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.GetAsync($"/api/media/{pdf.Id}/pages/0")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Outsider.GetAsync($"/api/orgs/{s.Org.Id}/books")).StatusCode);
    }

    [Fact]
    public async Task Saving_makes_revisions_and_detects_conflicts()
    {
        var s = await SetupAsync();
        var (book, media, _, _) = await NewBookAsync(s.Manager, s.Org.Id);
        var created = (await (await s.Manager.PostAsJsonAsync($"/api/orgs/{s.Org.Id}/books", new SaveBookRequest { Book = book, Media = media }, OnlineJson.Options))
            .Content.ReadFromJsonAsync<SaveBookResult>(OnlineJson.Options))!;
        var doc = (await s.Manager.GetFromJsonAsync<BookDocument>($"/api/books/{created.BookId}", OnlineJson.Options))!;
        var refs = doc.Media.ToDictionary(kv => kv.Key, kv => kv.Value.Id);

        // unchanged content → no new revision
        var same = await PutAsync(s.Manager, created.BookId, doc.Book, refs, 1);
        Assert.True(same.Unchanged);
        Assert.Equal(1, same.Revision);

        // a change → revision 2
        doc.Book.Songs[0].Recordings[0].Markers.Add(new TimeMarker { PageOffset = 1, TimeMs = 35000, X = 0.5, Y = 0.5 });
        var second = await PutAsync(s.Manager, created.BookId, doc.Book, refs, 1);
        Assert.Equal(2, second.Revision);

        // another edit based on revision 1 is a conflict…
        doc.Book.Title = "Stale edit";
        var conflict = await s.Manager.PutAsJsonAsync($"/api/books/{created.BookId}", new SaveBookRequest { Book = doc.Book, Media = refs, BaseRevision = 1 }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var error = (await conflict.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!;
        Assert.Equal("conflict", error.Code);
        Assert.Equal(2, error.CurrentRevision);
        // …unless forced
        var forced = await s.Manager.PutAsJsonAsync($"/api/books/{created.BookId}", new SaveBookRequest { Book = doc.Book, Media = refs, BaseRevision = 1, Force = true }, OnlineJson.Options);
        Assert.Equal(3, (await forced.Content.ReadFromJsonAsync<SaveBookResult>(OnlineJson.Options))!.Revision);
    }

    private static async Task<SaveBookResult> PutAsync(HttpClient client, long bookId, Book book, Dictionary<string, long> refs, int baseRevision)
    {
        var response = await client.PutAsJsonAsync($"/api/books/{bookId}", new SaveBookRequest { Book = book, Media = refs, BaseRevision = baseRevision }, OnlineJson.Options);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SaveBookResult>(OnlineJson.Options))!;
    }

    [Fact]
    public async Task Books_from_the_app_round_trip_and_keep_their_fingerprint()
    {
        var s = await SetupAsync();
        var dir = Path.Combine(web.DataDir, "app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // the app's book: local files, saved "with media" exactly like the app does
        var pdfPath = Path.Combine(dir, "Charts.pdf");
        await File.WriteAllBytesAsync(pdfPath, WebFixture.MinimalPdf(2));
        var mp3Path = Path.Combine(dir, "Vocals.mp3");
        await File.WriteAllBytesAsync(mp3Path, WebFixture.FakeMp3(seed: Random.Shared.Next()));
        var book = new Book { Title = "From the app" };
        book.Documents.Add(new PdfDocumentRef { Name = "Charts", PageCount = 2, Source = MediaSource.FromFile(pdfPath) });
        var rec = new Recording { Name = "Vocals", Source = MediaSource.FromFile(mp3Path) };
        rec.PageTimings.Add(new PageTiming { PageOffset = 1, TimeMs = 12345.5 });
        book.Songs.Add(new Song { Title = "Song", StartPage = 0, PageCount = 2, Recordings = { rec } });
        var package = Path.Combine(dir, "book.pmbook");
        await BookPackage.SaveAsync(book, package, embedMedia: true);

        // upload as the app does (raw body)
        var created = await UploadPackageAsync(s.Manager, s.Org.Id, package, "");
        Assert.True(created.Created);

        // the same book again without saying which revision it is based on: "it exists" (409)
        var again = await s.Manager.PostAsync($"/api/orgs/{s.Org.Id}/books/package", new ByteArrayContent(await File.ReadAllBytesAsync(package)));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("exists", (await again.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!.Code);
        var unchanged = await UploadPackageAsync(s.Manager, s.Org.Id, package, "?baseRevision=1");
        Assert.True(unchanged.Unchanged);
        var copy = await UploadPackageAsync(s.Manager, s.Org.Id, package, "?asNew=true");
        Assert.True(copy.Created);
        Assert.NotEqual(created.BookId, copy.BookId);

        // download it like the app: the package points back at the website, with the revision and fingerprint
        var download = await s.Member.GetAsync($"/api/books/{created.BookId}/package");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("1", download.Headers.GetValues(OnlineHeaders.Revision).Single());
        var downloaded = Path.Combine(dir, "downloaded.pmbook");
        await File.WriteAllBytesAsync(downloaded, await download.Content.ReadAsByteArrayAsync());
        var loaded = await BookPackage.LoadAsync(downloaded, Path.Combine(dir, "cache"));
        Assert.NotNull(loaded.Origin);
        Assert.Equal("https://pm.test", loaded.Origin!.Server);
        Assert.Equal(s.Org.Id, loaded.Origin.OrgId);
        Assert.Equal(created.BookId, loaded.Origin.BookId);
        Assert.Equal(1, loaded.Origin.Revision);
        // the app computes the same fingerprint, so it can tell "unchanged since download" from "changed locally"
        Assert.Equal(loaded.Origin.Fingerprint, BookFingerprint.Compute(loaded));
        Assert.False(BookFingerprint.HasLocalChanges(loaded));
        Assert.Equal(BookFingerprint.Compute(book), BookFingerprint.Compute(loaded)); // same music as the original
        Assert.True(loaded.AllMediaSources().All(m => m.IsResolved));
        loaded.Songs[0].Title = "Changed in the app";
        Assert.True(BookFingerprint.HasLocalChanges(loaded));
    }

    private static async Task<SaveBookResult> UploadPackageAsync(HttpClient client, long orgId, string path, string query)
    {
        using var content = new StreamContent(File.OpenRead(path));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await client.PostAsync($"/api/orgs/{orgId}/books/package{query}", content);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SaveBookResult>(OnlineJson.Options))!;
    }

    [Fact]
    public async Task Broken_or_incomplete_packages_are_refused_with_an_explanation()
    {
        var s = await SetupAsync();
        var notZip = await s.Manager.PostAsync($"/api/orgs/{s.Org.Id}/books/package", new ByteArrayContent(new byte[] { 1, 2, 3 }));
        Assert.Equal(HttpStatusCode.BadRequest, notZip.StatusCode);
        Assert.Contains("not a Practical Music book", (await notZip.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!.Error);

        // links-only book whose files are not inside
        var book = new Book { Title = "Links only" };
        book.Documents.Add(new PdfDocumentRef { Name = "Gone", PageCount = 1, Source = new MediaSource { Kind = MediaSourceKind.File, Path = "/nowhere/gone.pdf", FileName = "gone.pdf" } });
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var w = new StreamWriter(zip.CreateEntry("book.json").Open());
            await w.WriteAsync(BookJson.Serialize(book));
        }
        var missing = await s.Manager.PostAsync($"/api/orgs/{s.Org.Id}/books/package", new ByteArrayContent(ms.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("Save (with media)", (await missing.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!.Error);

        // a web page renamed to .mp3 is not a recording
        using var form = new MultipartFormDataContent { { new ByteArrayContent("<html>not audio</html>"u8.ToArray()), "file", "song.mp3" } };
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Manager.PostAsync($"/api/orgs/{s.Org.Id}/media", form)).StatusCode);
    }

    [Fact]
    public async Task Books_cannot_use_files_of_another_organization()
    {
        var a = await SetupAsync();
        var b = await SetupAsync();
        var (book, media, _, _) = await NewBookAsync(a.Manager, a.Org.Id);
        var steal = await b.Manager.PostAsJsonAsync($"/api/orgs/{b.Org.Id}/books", new SaveBookRequest { Book = book, Media = media }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, steal.StatusCode);
    }

    [Fact]
    public async Task Browser_changes_need_the_anti_forgery_token()
    {
        var org = await web.CreateOrgAsync();
        var manager = await web.CreateUserAsync();
        await web.AddMemberAsync(manager.Id, org.Id, "manager");
        var browser = await web.SignedInBrowserAsync(manager.Email);
        var token = await WebFixture.FormTokenAsync(browser, "/App");

        var book = new Book { Title = "x" };
        var without = await browser.PostAsJsonAsync($"/api/orgs/{org.Id}/books", new SaveBookRequest { Book = book }, OnlineJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal("csrf", (await without.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options))!.Code);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orgs/{org.Id}/books") { Content = JsonContent.Create(new SaveBookRequest { Book = book }, options: OnlineJson.Options) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        var with = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, with.StatusCode);

        // reading needs no token
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/api/orgs/{org.Id}/books")).StatusCode);
    }

    [Fact]
    public async Task The_music_app_page_carries_the_users_organizations()
    {
        var org = await web.CreateOrgAsync();
        var member = await web.CreateUserAsync(name: "Maria </script><script>alert(1)</script>");
        await web.AddMemberAsync(member.Id, org.Id, "member");
        var browser = await web.SignedInBrowserAsync(member.Email);
        var html = await browser.GetStringAsync($"/App?org={org.Id}");
        Assert.Contains(org.Name, html);
        Assert.DoesNotContain("<script>alert(1)</script>", html); // names are encoded inside the JSON block
        Assert.Contains("\"canEdit\":false", html);
    }
}
