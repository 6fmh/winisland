using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winisland.Services;

namespace Winisland;

internal static class ShelfInteractionVerification
{
    internal static async Task RunAsync(NotchWindow notch, SettingsStore settings, FileShelf shelf,
        string output, Action<bool, string> assert)
    {
        BackgroundVerification.RequirePrivateDesktop();
        var originalSettings = settings.Current;
        var originalItems = shelf.Items.ToArray();
        var credentials = new UploadCredentialStore();
        assert(!credentials.IsConnected, "Shelf interaction fixture uses disconnected uploads without network access");
        var directory = Path.Combine(SettingsStore.DirectoryPath, "shelf-interaction");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Canonical upload.txt");
        File.WriteAllText(path, "Native shelf interaction fixture.");
        var supplied = Path.Combine(directory, ".", "Canonical upload.txt");
        try
        {
            shelf.Clear();
            settings.Update(originalSettings with { ReducedMotion = true, ExpandOnHover = false });
            notch.SelectTabForTest("files");
            notch.Expand(true);
            var add = typeof(NotchWindow).GetMethod("AddAndUpload", BindingFlags.NonPublic | BindingFlags.Instance)!;
            add.Invoke(notch, new object[] { new[] { supplied, path } });
            var item = shelf.Items.Single();
            assert(item.Path == path && item.State == UploadState.NeedsConnection,
                "Equivalent dropped file paths create one reference and enter the upload queue");
            add.Invoke(notch, new object[] { new[] { supplied } });
            assert(shelf.Items.Count == 1 && item.State == UploadState.NeedsConnection,
                "Dropping an existing reference through an equivalent path retains its upload request");

            item.SetUpload(UploadState.Uploaded, 100,
                result: new UploadResult("https://uploads.example.test/share/fixture", "", ""));
            await Settle();
            var host = (ItemsControl)notch.FindName("ShelfItems");
            var button = Find<Button>(host, candidate => ReferenceEquals(candidate.Tag, item) && candidate.ContextMenu is not null);
            var menu = button.ContextMenu!;
            // Open only this fixture's native menu on the private desktop;
            // its real placement-target context must supply the item bindings.
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            await Settle();
            var share = (MenuItem)menu.Items[0];
            var direct = (MenuItem)menu.Items[1];
            assert(share.IsEnabled && !direct.IsEnabled && item.HasLink && !item.HasDirectLink,
                "Share-only uploads keep share actions available while disabling direct-link copy");
            // Exercising the actual guarded route must not touch the clipboard.
            typeof(NotchWindow).GetMethod("OnShelfCopyDirect", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(notch, new object[] { direct, new RoutedEventArgs(MenuItem.ClickEvent) });
            item.SetUpload(UploadState.Uploaded, 100, result: new UploadResult(
                "https://uploads.example.test/share/fixture", "https://uploads.example.test/file/fixture", ""));
            await Settle();
            assert(direct.IsEnabled && item.HasDirectLink,
                "A later direct upload URL enables the real direct-link menu binding");
            item.SetUpload(UploadState.Uploaded, 100, result: new UploadResult(
                "https://uploads.example.test/share/fixture", "javascript:invalid", ""));
            await Settle();
            assert(!direct.IsEnabled && !item.HasDirectLink,
                "Invalid direct URLs cannot expose a copy action");
            menu.IsOpen = false;
            var frame = new RenderTargetBitmap((int)Math.Ceiling(notch.ActualWidth),
                (int)Math.Ceiling(notch.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            frame.Render(notch);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = File.Create(Path.Combine(output, "shelf-interaction.png"));
            encoder.Save(stream);
        }
        finally
        {
            shelf.Clear();
            foreach (var item in originalItems) shelf.Items.Add(item);
            shelf.Save();
            settings.Update(originalSettings);
            notch.SelectTabForTest("music");
            notch.Collapse();
        }

        async Task Settle()
        {
            await notch.Dispatcher.InvokeAsync(() => notch.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            await Task.Delay(20);
        }
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value && match(value)) return value;
            try { return Find(child, match); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Shelf fixture control was not generated.");
    }
}
