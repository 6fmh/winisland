using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winisland.Services;

namespace Winisland;

internal static class NotificationIconVerification
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var results = await Task.Run(() =>
        {
            var checks = new List<(bool Passed, string Message)>();
            var native = Environment.ProcessPath is { } executable
                ? NotificationIconResolver.ReadNativeIconForTest(executable) as BitmapSource : null;
            checks.Add((native is { IsFrozen: true, PixelWidth: <= 40, PixelHeight: <= 40 },
                "Notification artwork reads the actual private candidate executable icon without launching a shell or app"));
            var known = NotificationIconResolver.KnownShortcutNameForTest();
            if (known is not null)
            {
                var first = NotificationIconResolver.Resolve(known) as BitmapSource;
                var second = NotificationIconResolver.Resolve(known);
                checks.Add((first is { IsFrozen: true, PixelWidth: <= 40, PixelHeight: <= 40 } && ReferenceEquals(first, second),
                    "Installed app notification artwork is a frozen bounded raster reused from the real shortcut cache"));
            }
            else checks.Add((native is not null,
                "No installed Start menu shortcut is available; real candidate icon verifies native artwork lookup"));

            // A tall valid asset catches width-only thumbnail bounds and retained
            // encoded-stream object graphs without using screen captures or UI.
            var pixels = new byte[8 * 2048 * 4];
            var source = BitmapSource.Create(8, 2048, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var detached = NotificationIconResolver.DecodeLogo(stream.ToArray());
            checks.Add((detached.IsFrozen && detached.PixelWidth <= 40 && detached.PixelHeight == 40
                && detached is not BitmapImage && detached is not BitmapFrame,
                "Tall application logos are bounded on their long edge and cached as detached raw pixels"));

            var prefix = "WinislandMissingIcon" + Guid.NewGuid().ToString("N");
            checks.Add((NotificationIconResolver.Resolve(prefix) is null,
                "Missing app artwork preserves the original generic notification fallback"));
            for (var index = 0; index < 36; index++) NotificationIconResolver.Resolve(prefix + index);
            checks.Add((NotificationIconResolver.CachedCount == 32,
                "Distinct notification app artwork lookups retain no more than32 positive or negative cache entries"));
            return checks;
        });
        foreach (var check in results) assert(check.Passed, check.Message);
    }
}
