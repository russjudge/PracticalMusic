using System.Security.Cryptography;
using System.Text;
using PracticalMusic.Core.Models;

namespace PracticalMusic.Core.Packaging;

/// <summary>Makes sure a media source has a readable local file, downloading URL sources into the cache.</summary>
public static class MediaResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string CachePathFor(string cacheRoot, MediaSource src)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(src.Url ?? src.FileName)))[..16];
        return Path.Combine(cacheRoot, "downloads", key + "_" + BookPackage.Sanitize(src.FileName));
    }

    /// <summary>Returns true when <see cref="MediaSource.ResolvedPath"/> points at an existing file afterwards.</summary>
    public static async Task<bool> EnsureResolvedAsync(MediaSource src, string cacheRoot,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (src.IsResolved) return true;

        switch (src.Kind)
        {
            case MediaSourceKind.File:
                if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
                {
                    src.ResolvedPath = src.Path;
                    return true;
                }
                return false;

            case MediaSourceKind.Url when !string.IsNullOrEmpty(src.Url):
            {
                var target = CachePathFor(cacheRoot, src);
                if (File.Exists(target))
                {
                    src.ResolvedPath = target;
                    return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".part";
                try
                {
                    using var response = await Http.GetAsync(src.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (IsWebPage(mediaType))
                        throw new InvalidDataException($"{src.Url} is a web page, not an audio or PDF file. Practical Music needs a direct link to the file itself (for example https://example.com/song.mp3).");
                    var total = response.Content.Headers.ContentLength ?? -1L;
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
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
                catch
                {
                    try { File.Delete(temp); } catch { /* ignore */ }
                    throw;
                }
                File.Move(temp, target, overwrite: true);
                src.ResolvedPath = target;
                src.SizeBytes = new FileInfo(target).Length;
                return true;
            }

            default:
                return false;
        }
    }

    public static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".flac", ".wma", ".opus" };

    public static bool IsWebPage(string? mediaType) =>
        mediaType != null && (mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                              mediaType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase));

    public static bool HasAudioExtension(string? name) =>
        !string.IsNullOrEmpty(name) && AudioExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public sealed record UrlProbe(bool IsAudio, string? ContentType, string FileName, long SizeBytes, string? Problem);

    /// <summary>
    /// Checks what an http(s) address points at without downloading it: a direct audio file is accepted,
    /// a web page (such as a Soundslice or YouTube page) is rejected with an explanation.
    /// </summary>
    public static async Task<UrlProbe> ProbeUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new UrlProbe(false, null, "", 0, "Enter a complete http:// or https:// address.");

        var pathName = "";
        try { pathName = System.IO.Path.GetFileName(uri.AbsolutePath); } catch { /* ignore */ }
        if (uri.Host.EndsWith("soundslice.com", StringComparison.OrdinalIgnoreCase))
            return new UrlProbe(false, null, pathName, 0,
                "Soundslice plays its audio inside the Soundslice website only; a slice link does not give access to the recording. " +
                "If you have the rights to the audio, download or export it from Soundslice and add the MP3 file instead.");

        HttpResponseMessage? response = null;
        try
        {
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, uri);
                response = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) { response.Dispose(); response = null; }
            }
            catch (HttpRequestException) { response = null; }

            response ??= await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new UrlProbe(false, null, pathName, 0, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var size = response.Content.Headers.ContentLength ?? 0;
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (string.IsNullOrWhiteSpace(fileName)) fileName = pathName;
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "recording";

            if (IsWebPage(mediaType))
                return new UrlProbe(false, mediaType, fileName, size,
                    "This address is a web page, not an audio file. Practical Music needs a direct link to the MP3 itself (for example https://example.com/song.mp3).");

            var looksLikeAudio = (mediaType != null && mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                                 || HasAudioExtension(fileName);
            if (!looksLikeAudio)
                return new UrlProbe(false, mediaType, fileName, size,
                    $"The address does not look like an audio file (type: {mediaType ?? "unknown"}).");

            if (!HasAudioExtension(fileName))
                fileName += mediaType switch
                {
                    "audio/mpeg" or "audio/mp3" => ".mp3",
                    "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
                    "audio/ogg" => ".ogg",
                    "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
                    "audio/flac" or "audio/x-flac" => ".flac",
                    "audio/aac" => ".aac",
                    _ => ".mp3"
                };
            return new UrlProbe(true, mediaType, fileName, size, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new UrlProbe(false, null, pathName, 0, "The address could not be reached: " + ex.Message);
        }
        finally
        {
            response?.Dispose();
        }
    }
}
