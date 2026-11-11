using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Winisland.Services;

/// <summary>
/// Read-only application artwork lookup, called only by the notification MTA
/// worker. Never activates an app, shortcut, package or shell window.
/// </summary>
internal static class NotificationIconResolver
{
    private const int CacheLimit = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();
    private static Dictionary<string, string>? _shortcuts;

    internal static ImageSource? Resolve(string appName)
    {
        var key = Normalize(appName);
        if (key.Length < 2 || key is "notification" or "winisland") return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            // Shell and package metadata must not block WPF's dispatcher.
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA) return null;
            ImageSource? icon = null;
            var apartment = CoInitializeEx(IntPtr.Zero, 0);
            if (apartment < 0) return null;
            try
            {
                _shortcuts ??= IndexShortcuts();
                if (_shortcuts.TryGetValue(key, out var shortcut)) icon = ReadShortcutIcon(shortcut);
                icon ??= ReadPackageIcon(key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException
                or ArgumentException or InvalidOperationException or NotSupportedException)
            { /* Missing app metadata is normal; no notification content is logged. */ }
            finally { CoUninitialize(); }
            if (Cache.Count >= CacheLimit) Cache.Remove(CacheOrder.Dequeue());
            Cache[key] = icon;
            CacheOrder.Enqueue(key);
            return icon;
        }
    }

    private static Dictionary<string, string> IndexShortcuts()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        // The user's entry takes precedence over a shared entry with the same name.
        pending.Push((Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), 0));
        pending.Push((Environment.GetFolderPath(Environment.SpecialFolder.Programs), 0));
        var examined = 0;
        var timer = Stopwatch.StartNew();
        while (pending.Count > 0 && examined < 1500 && index.Count < 512 && timer.ElapsedMilliseconds < 1000)
        {
            var (directory, depth) = pending.Pop();
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++examined > 1500 || index.Count >= 512 || timer.ElapsedMilliseconds >= 1000) break;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < 4 && pending.Count < 128) pending.Push((path, depth + 1));
                    }
                    else if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                        index.TryAdd(Normalize(Path.GetFileNameWithoutExtension(path)), path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* An inaccessible Start menu directory does not prevent other icons. */ }
        }
        return index;
    }

    private static ImageSource? ReadShortcutIcon(string path)
    {
        var info = new ShellFileInfo();
        // SHGFI_ICON returns an owned HICON, including the shortcut's target icon.
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100) == IntPtr.Zero
            || info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(40, 40));
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }

    private static ImageSource? ReadPackageIcon(string key)
    {
        var manager = new PackageManager();
        var timer = Stopwatch.StartNew();
        var examined = 0;
        var candidates = 0;
        // Empty SID queries only the current user's packages, without elevation.
        foreach (var package in manager.FindPackagesForUser(""))
        {
            if (++examined > 160 || candidates >= 4 || timer.ElapsedMilliseconds > 1800) break;
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                if (!Matches(Normalize(package.DisplayName), key) && !Matches(Normalize(package.Id.Name), key)) continue;
                candidates++;
                var appsTask = package.GetAppListEntriesAsync().AsTask();
                if (!appsTask.Wait(Remaining(timer))) continue;
                foreach (var app in appsTask.GetAwaiter().GetResult().Take(8))
                {
                    if (timer.ElapsedMilliseconds >= 1800) break;
                    if (!Matches(Normalize(app.DisplayInfo.DisplayName), key)) continue;
                    var logoTask = app.DisplayInfo.GetLogo(new Windows.Foundation.Size(40, 40)).OpenReadAsync().AsTask();
                    if (!logoTask.Wait(Remaining(timer)))
                    {
                        // A late stream still belongs to this lookup and must be released.
                        _ = logoTask.ContinueWith(task => { if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) task.Result.Dispose(); },
                            System.Threading.Tasks.TaskScheduler.Default);
                        break;
                    }
                    using var stream = logoTask.GetAwaiter().GetResult();
                    if (stream.Size == 0 || stream.Size > 2 * 1024 * 1024) break;
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    var dataTask = reader.LoadAsync((uint)stream.Size).AsTask();
                    if (!dataTask.Wait(Remaining(timer))) break;
                    var bytes = new byte[(int)dataTask.GetAwaiter().GetResult()];
                    reader.ReadBytes(bytes);
                    return DecodeLogo(bytes);
                }
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException
                or IOException or UnauthorizedAccessException or AggregateException or NotSupportedException)
            { /* Installed packages can disappear or have unavailable localized assets. */ }
        }
        return null;
    }

    private static int Remaining(Stopwatch timer) => (int)Math.Clamp(1800 - timer.ElapsedMilliseconds, 0, 350);

    internal static BitmapSource DecodeLogo(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        // Header inspection chooses the long edge before requesting any raster.
        var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var sourceWidth = frame.PixelWidth;
        var sourceHeight = frame.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new InvalidOperationException("Invalid app icon dimensions");
        memory.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (sourceWidth >= sourceHeight) image.DecodePixelWidth = Math.Min(40, sourceWidth);
        else image.DecodePixelHeight = Math.Min(40, sourceHeight);
        image.StreamSource = memory;
        image.EndInit();
        if (image.PixelWidth > 40 || image.PixelHeight > 40) throw new InvalidOperationException("App icon raster exceeds banner dimensions");
        // A raw-pixel copy avoids retaining the original encoded stream through
        // BitmapImage/decoder object graphs in the bounded artwork cache.
        var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            image.Format, image.Palette, pixels, stride);
        detached.Freeze();
        return detached;
    }

    internal static int CachedCount { get { lock (Gate) return Cache.Count; } }
    internal static string? KnownShortcutNameForTest()
    {
        lock (Gate)
        {
            _shortcuts ??= IndexShortcuts();
            return _shortcuts.Values.FirstOrDefault() is { } shortcut ? Path.GetFileNameWithoutExtension(shortcut) : null;
        }
    }
    internal static ImageSource? ReadNativeIconForTest(string path)
    {
        var apartment = CoInitializeEx(IntPtr.Zero, 0);
        if (apartment < 0) return null;
        try { return ReadShortcutIcon(path); }
        finally { CoUninitialize(); }
    }

    private static bool Matches(string candidate, string key) => candidate == key
        || key.Length >= 5 && candidate.EndsWith(key, StringComparison.Ordinal);

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var result = new StringBuilder(Math.Min(name.Length, 100));
        foreach (var character in name.Take(100))
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal IntPtr Icon;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
