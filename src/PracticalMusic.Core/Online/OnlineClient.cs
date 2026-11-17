using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PracticalMusic.Core.Models;

namespace PracticalMusic.Core.Online;

/// <summary>An error answer from the website (or a failure to reach it).</summary>
public sealed class OnlineException : Exception
{
    public HttpStatusCode? Status { get; }
    public string? Code { get; }
    public int? CurrentRevision { get; }

    public OnlineException(string message, HttpStatusCode? status = null, string? code = null, int? currentRevision = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
        CurrentRevision = currentRevision;
    }

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
    public bool IsForbidden => Status == HttpStatusCode.Forbidden;
    public bool IsConflict => Status == HttpStatusCode.Conflict;
}

/// <summary>
/// Talks to a Practical Music website on behalf of the desktop and Android apps: sign in (a bearer token
/// replaces the password from then on), list an organization's books, download a book as a .pmbook and
/// upload one (managers only).
/// </summary>
public sealed class OnlineClient : IDisposable
{
    public const string DefaultServer = "https://practicalmusic.russjudge.com";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public string BaseUrl { get; }
    public string? Token { get; private set; }

    public OnlineClient(string baseUrl, string? token = null, HttpMessageHandler? handler = null)
    {
        if (!TryNormalizeServer(baseUrl, out var normalized, out var problem)) throw new ArgumentException(problem, nameof(baseUrl));
        BaseUrl = normalized;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl + "/");
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // per call: JSON calls time out, large transfers do not
        var version = typeof(OnlineClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PracticalMusic/{version}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        SetToken(token);
    }

    public void SetToken(string? token)
    {
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        _http.DefaultRequestHeaders.Authorization = Token == null ? null : new AuthenticationHeaderValue("Bearer", Token);
    }

    /// <summary>
    /// Accepts "practicalmusic.example.com", "https://practicalmusic.example.com/" …; returns "https://practicalmusic.example.com".
    /// Plain http is only accepted for localhost and private test addresses, because passwords and tokens travel over it.
    /// </summary>
    public static bool TryNormalizeServer(string? input, out string url, out string? problem)
    {
        url = "";
        problem = null;
        var s = (input ?? "").Trim();
        if (s.Length == 0) { problem = "Enter the address of the website."; return false; }
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
        {
            problem = "That is not a valid web address.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLocalAddress(uri.Host))
        {
            problem = "Use https:// - the website address must be secure because your password is sent to it.";
            return false;
        }
        url = OnlineOrigin.NormalizeServer(uri.GetLeftPart(UriPartial.Path));
        return true;
    }

    private static bool IsLocalAddress(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31));
    }

    /// <summary>Address of the sync WebSocket for this website (https → wss).</summary>
    public Uri SyncSocketUri
    {
        get
        {
            var b = new UriBuilder(BaseUrl + "/ws/sync");
            b.Scheme = b.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            return b.Uri;
        }
    }

    // ------------------------------------------------------------------ account

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(request, options: OnlineJson.Options)
        }, cts.Token);
        var login = await ReadJsonAsync<LoginResponse>(response, cts.Token);
        SetToken(login.Token);
        return login;
    }

    /// <summary>Revokes the token on the website (best effort) and forgets it.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (Token == null) return;
        try
        {
            using var cts = Timeout(ct);
            using var response = await _http.PostAsync("api/auth/logout", null, cts.Token);
        }
        catch { /* the token is forgotten locally anyway */ }
        SetToken(null);
    }

    public async Task<UserInfo> GetMeAsync(CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/me"), cts.Token);
        return await ReadJsonAsync<UserInfo>(response, cts.Token);
    }

    // ------------------------------------------------------------------ books

    public async Task<List<BookSummary>> GetBooksAsync(long orgId, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/orgs/{orgId}/books"), cts.Token);
        return await ReadJsonAsync<List<BookSummary>>(response, cts.Token);
    }

    public async Task<BookSummary> GetBookSummaryAsync(long bookId, CancellationToken ct = default)
    {
        using var cts = Timeout(ct);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/books/{bookId}/summary"), cts.Token);
        return await ReadJsonAsync<BookSummary>(response, cts.Token);
    }

    /// <summary>
    /// Downloads the book as a self-contained .pmbook to <paramref name="targetPath"/> (written to a temporary
    /// file first). The package's book.json carries an <see cref="OnlineOrigin"/> pointing back at the website.
    /// </summary>
    public async Task<int> DownloadPackageAsync(long bookId, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/books/{bookId}/package"), ct,
            HttpCompletionOption.ResponseHeadersRead);
        var revision = response.Headers.TryGetValues(OnlineHeaders.Revision, out var values) && int.TryParse(values.FirstOrDefault(), out var r) ? r : 0;
        var total = response.Content.Headers.ContentLength ?? -1;
        var folder = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = targetPath + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(temp, targetPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ignore */ }
            throw;
        }
        progress?.Report(1);
        return revision;
    }

    /// <summary>
    /// Uploads a self-contained .pmbook to an organization (managers only). A book with the same id
    /// (book.json "id") in that organization is updated; otherwise a new book is created.
    /// </summary>
    /// <param name="baseRevision">The revision this copy is based on; when the website has a different one the upload is refused with a conflict unless <paramref name="force"/>.</param>
    /// <param name="asNew">Store as a new book even if one with the same id exists.</param>
    public async Task<SaveBookResult> UploadPackageAsync(long orgId, string packagePath, int? baseRevision, bool force = false, bool asNew = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (baseRevision.HasValue) query.Add("baseRevision=" + baseRevision.Value);
        if (force) query.Add("force=true");
        if (asNew) query.Add("asNew=true");
        var url = $"api/orgs/{orgId}/books/package" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        using var response = await SendAsync(() =>
        {
            var stream = new ProgressStream(File.OpenRead(packagePath), progress);
            var content = new StreamContent(stream, 1 << 16);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        }, ct);
        return await ReadJsonAsync<SaveBookResult>(response, ct);
    }

    // ------------------------------------------------------------------ plumbing

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        return cts;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        using (var request = build())
        {
            try
            {
                response = await _http.SendAsync(request, completion, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OnlineException($"{BaseUrl} did not answer in time.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new OnlineException($"{BaseUrl} could not be reached: {ex.Message}", inner: ex);
            }
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            ApiError? error = null;
            try
            {
                if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                    error = await response.Content.ReadFromJsonAsync<ApiError>(OnlineJson.Options, ct);
            }
            catch { /* not our JSON */ }
            var message = !string.IsNullOrWhiteSpace(error?.Error) ? error!.Error : response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Not signed in, or the sign-in expired. Sign in again.",
                HttpStatusCode.Forbidden => "You do not have permission for this.",
                HttpStatusCode.NotFound => "Not found on the website.",
                HttpStatusCode.RequestEntityTooLarge => "The file is larger than the website accepts.",
                HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
                _ => $"The website answered {(int)response.StatusCode} {response.ReasonPhrase}."
            };
            throw new OnlineException(message, response.StatusCode, error?.Code, error?.CurrentRevision);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(OnlineJson.Options, ct);
            return value ?? throw new OnlineException("The website sent an empty answer.");
        }
        catch (JsonException ex)
        {
            throw new OnlineException("The website sent an answer this app does not understand. Is the address right?", inner: ex);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Reports read progress of an upload.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly IProgress<double>? _progress;
        private long _done;

        public ProgressStream(Stream inner, IProgress<double>? progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await _inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _done += read;
            if (_progress != null && Length > 0) _progress.Report((double)_done / Length);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
