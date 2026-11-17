using Microsoft.Extensions.Options;
using PracticalMusic.Web.Data;

namespace PracticalMusic.Web.Services;

/// <summary>
/// Hourly housekeeping: removes uploads no book uses any more (after a grace period, so a book being edited in a
/// browser keeps its fresh uploads), trims the page-image cache, deletes expired tokens, old e-mail and old audit
/// entries, and leftovers in the temporary folder.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, DataPaths paths, IOptions<SiteOptions> site, ILogger<MaintenanceService> log)
    : BackgroundService
{
    public static readonly TimeSpan UnusedMediaGrace = TimeSpan.FromDays(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var now = DateTime.UtcNow;

        // 1. media no book refers to and nobody used for a while; the delete re-checks both conditions, and the file
        //    lock keeps an upload of the same content from slipping in between
        var media = sp.GetRequiredService<IMediaRepository>();
        var cutoff = now - UnusedMediaGrace;
        var removed = 0;
        foreach (var m in await media.ListUnreferencedAsync(cutoff, 500))
        {
            ct.ThrowIfCancellationRequested();
            await MediaStorage.FileLock.WaitAsync(ct);
            try
            {
                if (!await media.DeleteIfUnusedAsync(m.Id, cutoff)) continue;
                removed++;
                if (!await media.IsShaInUseAsync(m.Sha256)) DeleteMediaFiles(m.Sha256);
            }
            finally
            {
                MediaStorage.FileLock.Release();
            }
        }
        if (removed > 0) log.LogInformation("Removed {Count} unused media file(s)", removed);

        // 1b. files without any media row (e.g. after an organization was deleted)
        var checkedFiles = 0;
        foreach (var file in SafeEnumerateTree(paths.Media))
        {
            if (++checkedFiles > 5000) break;
            var sha = Path.GetFileName(file);
            if (sha.Length != 64 || File.GetLastWriteTimeUtc(file) > cutoff) continue;
            await MediaStorage.FileLock.WaitAsync(ct);
            try
            {
                if (!await media.IsShaInUseAsync(sha)) DeleteMediaFiles(sha);
            }
            finally
            {
                MediaStorage.FileLock.Release();
            }
        }

        // 2. tokens, e-mail, audit
        await sp.GetRequiredService<ITokenRepository>().DeleteExpiredAsync(now);
        await sp.GetRequiredService<IEmailOutboxRepository>().PurgeAsync(now.AddDays(-30), now.AddDays(-90));
        await sp.GetRequiredService<IAuditRepository>().PurgeAsync(now.AddYears(-2));

        // 3. packages of books that no longer exist or of old revisions
        var books = sp.GetRequiredService<IBookRepository>();
        foreach (var file in SafeEnumerate(paths.Packages, "*.pmbook"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf("-r", StringComparison.Ordinal);
            if (dash <= 0 || !long.TryParse(name[..dash], out var bookId)) { TryDelete(file); continue; }
            var book = await books.GetAsync(bookId);
            if (book == null || !name.StartsWith($"{bookId}-r{book.Revision}-", StringComparison.Ordinal)) TryDelete(file);
        }

        // 4. page cache size
        TrimPageCache(site.Value.PageCacheMegabytes * 1024L * 1024L);

        // 5. temporary files older than a day
        foreach (var file in SafeEnumerate(paths.Temp, "*"))
            if (File.GetLastWriteTimeUtc(file) < now.AddDays(-1)) TryDelete(file);
    }

    private void DeleteMediaFiles(string sha)
    {
        TryDelete(paths.MediaFile(sha));
        TryDeleteDirectory(Path.Combine(paths.PageCache, sha[..2], sha));
    }

    private static IEnumerable<string> SafeEnumerateTree(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList() : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private void TrimPageCache(long maxBytes)
    {
        if (!Directory.Exists(paths.PageCache)) return;
        var files = new DirectoryInfo(paths.PageCache).EnumerateFiles("*.png", SearchOption.AllDirectories)
            .Select(f => (f.FullName, f.Length, Used: f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
            .ToList();
        var total = files.Sum(f => f.Length);
        if (total <= maxBytes) return;
        foreach (var f in files.OrderBy(f => f.Used))
        {
            if (total <= maxBytes * 9 / 10) break;
            TryDelete(f.FullName);
            total -= f.Length;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).ToList() : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* next time */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* next time */ }
    }
}
