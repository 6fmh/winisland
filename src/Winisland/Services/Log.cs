using System;
using System.IO;

namespace Winisland.Services;

internal static class Log
{
    public static void Error(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.AppendAllText(Path.Combine(SettingsStore.DirectoryPath, "winisland.log"),
                $"{DateTimeOffset.Now:u} {context}: {exception}\n");
        }
        catch (IOException) { /* Logging must not prevent startup or shutdown. */ }
        catch (UnauthorizedAccessException) { }
    }
}
