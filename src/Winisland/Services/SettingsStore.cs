using System;
using System.IO;
using System.Text.Json;
using Winisland.Controls;

namespace Winisland.Services;

public sealed record IslandSettings
{
    public double NotchWidth { get; init; } = NotchMetrics.DefaultWidth;
    public double NotchHeight { get; init; } = NotchMetrics.DefaultHeight;
    public bool ExpandOnHover { get; init; } = true;
    public bool ShowLiveActivity { get; init; } = true;
    public bool HideInFullscreen { get; init; } = true;
    public bool ReducedMotion { get; init; }
    public bool ShowVolumeHud { get; init; } = true;
    public bool ShowNotifications { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public string MonitorDevice { get; init; } = "";
    public int FocusMinutes { get; init; } = 25;
    public int FocusDurationSeconds { get; init; }
    public bool HasLaunched { get; init; }
}

public sealed class SettingsStore
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("WINISLAND_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winisland");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public IslandSettings Current { get; private set; } = new();
    public event EventHandler? Changed;

    public SettingsStore()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = Normalize(JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Load settings", ex);
        }
    }

    public void Update(IslandSettings settings)
    {
        var normalized = Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
        // Task and legacy Run registration roll back if the file cannot commit.
        using var startup = Current.StartWithWindows != normalized.StartWithWindows
            ? StartupRegistration.BeginChange(normalized.StartWithWindows) : null;
        try
        {
            startup?.Apply();
            File.Move(temporary, FilePath, true);
            startup?.Commit();
        }
        catch
        {
            throw;
        }
        Current = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IslandSettings Normalize(IslandSettings settings) => settings with
    {
        NotchWidth = double.IsFinite(settings.NotchWidth) ? Math.Clamp(settings.NotchWidth, 160, 320) : NotchMetrics.DefaultWidth,
        NotchHeight = double.IsFinite(settings.NotchHeight) ? Math.Clamp(settings.NotchHeight, 24, 48) : NotchMetrics.DefaultHeight,
        FocusMinutes = Math.Clamp(settings.FocusMinutes, 1, 120),
        MonitorDevice = settings.MonitorDevice ?? "",
        FocusDurationSeconds = settings.FocusDurationSeconds > 0
            ? Math.Clamp(settings.FocusDurationSeconds, 1, 86400)
            : Math.Clamp(settings.FocusMinutes, 1, 120) * 60
    };

}
