using Avalonia.Platform.Storage;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Packaging;

namespace PracticalMusic.App.Services;

/// <summary>File picker helpers and import logic shared by the view models.</summary>
public static class FileImporter
{
    public static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".flac", ".wma", ".opus" };

    public static readonly FilePickerFileType PdfType = new("PDF sheet music")
    {
        Patterns = new[] { "*.pdf" },
        MimeTypes = new[] { "application/pdf" }
    };

    public static readonly FilePickerFileType AudioType = new("Audio files")
    {
        Patterns = AudioExtensions.Select(e => "*" + e).ToArray(),
        MimeTypes = new[] { "audio/*" }
    };

    public static readonly FilePickerFileType BookType = new("Practical Music book")
    {
        Patterns = new[] { "*" + BookPackage.Extension },
        MimeTypes = new[] { "application/zip", "application/octet-stream", "*/*" }
    };

    public static readonly FilePickerFileType AnyType = new("All files") { Patterns = new[] { "*" } };

    public static bool IsPdf(string name) => name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsAudio(string name) => AudioExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsBook(string name) => name.EndsWith(BookPackage.Extension, StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<IStorageFile>> PickAsync(string title, bool multiple, params FilePickerFileType[] types)
    {
        var storage = AppServices.Storage;
        if (storage == null || !storage.CanOpen) return Array.Empty<IStorageFile>();
        return await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types.Concat(new[] { AnyType }).ToList()
        });
    }

    public static async Task<IStorageFile?> PickSaveAsync(string title, string suggestedName, FilePickerFileType type)
    {
        var storage = AppServices.Storage;
        if (storage == null || !storage.CanSave) return null;
        return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = type.Patterns?.FirstOrDefault()?.TrimStart('*'),
            FileTypeChoices = new[] { type },
            ShowOverwritePrompt = true
        });
    }

    /// <summary>
    /// Turns a picked file into a MediaSource. On desktop the original path is referenced; on Android
    /// (content URIs) or when <paramref name="copy"/> is set the file is copied into the app library.
    /// </summary>
    public static async Task<MediaSource> ImportAsync(IStorageFile file, bool? copy = null, CancellationToken ct = default)
    {
        var local = file.TryGetLocalPath();
        var shouldCopy = copy ?? AppServices.Platform.CopyImportedFiles;
        if (!string.IsNullOrEmpty(local) && File.Exists(local) && !shouldCopy)
            return MediaSource.FromFile(local);

        var target = Path.Combine(AppServices.LibraryDir, UniqueName(file.Name));
        await using (var input = await file.OpenReadAsync())
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await input.CopyToAsync(output, ct);
        }
        var src = MediaSource.FromFile(target);
        src.FileName = file.Name;
        return src;
    }

    /// <summary>Copies a picked file to a temp location and returns the path (used to open packages from content URIs).</summary>
    public static async Task<string> ToLocalPathAsync(IStorageFile file, CancellationToken ct = default)
    {
        var local = file.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local) && File.Exists(local)) return local;
        var target = Path.Combine(AppServices.TempDir, UniqueName(file.Name));
        await using var input = await file.OpenReadAsync();
        await using var output = File.Create(target);
        await input.CopyToAsync(output, ct);
        return target;
    }

    private static string UniqueName(string name)
    {
        var safe = BookPackage.Sanitize(name);
        return DateTime.Now.ToString("yyyyMMdd_HHmmss_") + safe;
    }
}
