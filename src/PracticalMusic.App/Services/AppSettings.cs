using System.Text.Json;
using PracticalMusic.Core.Networking;

namespace PracticalMusic.App.Services;

/// <summary>Small persisted settings file (JSON) in the app data folder.</summary>
public sealed class AppSettings
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = "";
    public string? LastBookPath { get; set; }
    public List<string> RecentBooks { get; set; } = new();
    /// <summary>Folder of the last book the user saved through the file dialog; the dialog starts there next time.</summary>
    public string? LastSaveFolder { get; set; }
    public bool NetworkEnabled { get; set; } = true;
    /// <summary>TCP port for book transfers (default 47313). When busy the next ports are tried; 0 = any free port.</summary>
    public int TransferPort { get; set; } = TransferService.DefaultPort;
    public bool TapToSeekInPlayer { get; set; } = true;
    public double Volume { get; set; } = 1.0;

    private static string? _path;

    public static AppSettings Load(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        _path = Path.Combine(appDataDir, "settings.json");
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        if (_path == null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    /// <summary>Remembers the folder of a file the user chose in a save dialog.</summary>
    public void RememberSaveFolder(string filePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            LastSaveFolder = folder;
            Save();
        }
        catch { /* ignore */ }
    }

    public void RemoveRecent(string path)
    {
        RecentBooks.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(LastBookPath, path, StringComparison.OrdinalIgnoreCase)) LastBookPath = RecentBooks.FirstOrDefault();
        Save();
    }

    public void AddRecent(string path)
    {
        RecentBooks.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentBooks.Insert(0, path);
        if (RecentBooks.Count > 10) RecentBooks.RemoveRange(10, RecentBooks.Count - 10);
        LastBookPath = path;
        Save();
    }
}
