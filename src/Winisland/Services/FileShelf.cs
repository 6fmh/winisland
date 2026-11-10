using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Winisland.Services;

public enum UploadState { Local, NeedsConnection, Queued, Uploading, Processing, Uploaded, Failed, Cancelled }

public sealed class ShelfItem : INotifyPropertyChanged
{
    public ShelfItem(string path) => Path = path;
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public string Detail => Directory.Exists(Path) ? "Folder" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension + " file" : "File";
    public UploadState State { get; private set; }
    public double Progress { get; private set; }
    public string ShareUrl { get; private set; } = "";
    public string FileUrl { get; private set; } = "";
    public string ViewerUrl { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool HasLink => State == UploadState.Uploaded && ShareUrl.Length > 0;
    public bool CanRetry => !Directory.Exists(Path) && State is UploadState.Failed or UploadState.Cancelled or UploadState.Local;
    public bool CanCancel => State is UploadState.Queued or UploadState.Uploading or UploadState.Processing or UploadState.NeedsConnection;
    public bool IsUploading => State is UploadState.Uploading or UploadState.Processing;
    public string Status => State switch
    {
        UploadState.NeedsConnection => "Connect in Settings",
        UploadState.Queued => "Waiting to upload",
        UploadState.Uploading => $"Uploading · {Progress:0}%",
        UploadState.Processing => "Finishing on the host…",
        UploadState.Uploaded => "Uploaded · link ready",
        UploadState.Failed => Error,
        UploadState.Cancelled => Error.Length > 0 ? Error : "Cancelled · retry anytime",
        _ => Directory.Exists(Path) ? "Folder · kept locally" : Detail
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetUpload(UploadState state, double progress = 0, string error = "", UploadResult? result = null)
    {
        State = state; Progress = progress; Error = error;
        if (result is not null) { ShareUrl = result.ShareUrl; FileUrl = result.FileUrl; ViewerUrl = result.ViewerUrl; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class FileShelf
{
    private static string ShelfPath => System.IO.Path.Combine(SettingsStore.DirectoryPath, "shelf.json");
    public ObservableCollection<ShelfItem> Items { get; } = new();
    public FileShelf()
    {
        try
        {
            if (File.Exists(ShelfPath))
            {
                if (new FileInfo(ShelfPath).Length > 1024 * 1024) throw new JsonException("File shelf exceeds its size limit.");
                using var document = JsonDocument.Parse(File.ReadAllText(ShelfPath));
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("File shelf is not an array.");
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (Items.Count >= 24) break;
                    var path = NormalizePath(entry.ValueKind == JsonValueKind.String ? entry.GetString() : ReadString(entry, "Path"), requireExisting: false);
                    if (path is null || Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    var share = ReadString(entry, "ShareUrl");
                    // A hosted link is independent of its local original. Keep
                    // it available until the user removes the shelf reference.
                    if (!File.Exists(path) && !Directory.Exists(path) && !UploadClient.IsShareLink(share)) continue;
                    var item = new ShelfItem(path);
                    if (UploadClient.IsShareLink(share))
                    {
                        var direct = ReadString(entry, "FileUrl");
                        var viewer = ReadString(entry, "ViewerUrl");
                        item.SetUpload(UploadState.Uploaded, 100, result: new UploadResult(share!,
                            UploadClient.IsShareLink(direct) ? direct! : "", UploadClient.IsShareLink(viewer) ? viewer! : ""));
                    }
                    Items.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException) { Log.Error("Load file shelf", ex); }
    }

    public int Add(string[] paths)
    {
        var added = 0;
        foreach (var supplied in paths)
        {
            if (Items.Count >= 24) break;
            var path = NormalizePath(supplied);
            if (path is null || Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new(path));
            added++;
        }
        Save();
        return added;
    }

    public void Remove(ShelfItem item) { Items.Remove(item); Save(); }
    public void Clear() { Items.Clear(); Save(); }
    public static void Open(ShelfItem item) => Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    private static string? ReadString(JsonElement entry, string name) => entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NormalizePath(string? path, bool requireExisting = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try
        {
            var normalized = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            return !requireExisting || File.Exists(normalized) || Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var temporary = ShelfPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Items.Select(i => new { i.Path, i.ShareUrl, i.FileUrl, i.ViewerUrl }).ToArray()));
            File.Move(temporary, ShelfPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Save file shelf", ex); }
    }
}
