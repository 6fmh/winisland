using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

internal static class ProductivityVisualVerification
{
    private sealed record Preview(string Title, BitmapSource Image);

    internal static async Task RunAsync(NotchWindow notch, SettingsStore settings, MediaService media,
        FocusTimer focus, string output, Action<bool, string> assert)
    {
        var savedSettings = settings.Current;
        var savedMedia = media.Current;
        var panel = (OrganizerPanel)notch.FindName("Organizer");
        var island = (FrameworkElement)notch.FindName("Island");
        var content = (FrameworkElement)notch.FindName("ExpandedContent");
        var previews = new List<Preview>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var sampleEvents = new List<Guid>();
        var sampleReminders = new List<Guid>();
        var savedNote = panel.StoreForTest.Note;
        var notesBox = (TextBox)panel.FindName("NotesBox");
        try
        {
            settings.Update(savedSettings with { ReducedMotion = true, HideInFullscreen = false, FocusDurationSeconds = 2730 });
            sampleEvents.Add(panel.StoreForTest.AddEvent("Design review", OrganizerCalendar.LocalInstant(today, new TimeOnly(10, 30)), 45).Id);
            sampleEvents.Add(panel.StoreForTest.AddEvent("A little time outside", OrganizerCalendar.LocalInstant(today, new TimeOnly(16, 0)), 30).Id);
            sampleReminders.Add(panel.StoreForTest.AddReminder("Finish the playlist", DateTimeOffset.Now.AddHours(2)).Id);
            sampleReminders.Add(panel.StoreForTest.AddReminder("Make room for a break").Id);
            notesBox.Text = "A little space to think.\n\n• Finish the first draft\n• Save a song for later\n• Take a walk before sunset";
            panel.FlushEdits();
            panel.SelectDateForTest(today);
            notch.SelectTabForTest("controls");
            notch.Expand(true);
            await Settle();
            assert(Math.Abs(island.ActualHeight - NotchMetrics.ControlCenterExpandedHeight) < .01
                && notch.ActualHeight - island.ActualHeight >= NotchMetrics.ShadowPadding,
                "The tallest Control Center panel retains space for its shadow below the surface");
            notch.SelectTabForTest("calendar");
            notch.Expand(true);
            await Settle();
            assert(Math.Abs(island.ActualHeight - NotchMetrics.OrganizerExpandedHeight) < .01
                && Math.Abs(content.ActualHeight - 320) < .01 && panel.ActualHeight == 280,
                "Calendar grows the surface to fit its full month and agenda without clipping");
            var panelBounds = panel.TransformToAncestor(island).TransformBounds(new Rect(panel.RenderSize));
            assert(panelBounds.Top >= 40 && panelBounds.Bottom <= island.ActualHeight - 11.9,
                "Organizer content keeps the camera header and rounded lower corners clear");

            foreach (var width in new[] { 160.0, 208.0, 320.0 })
            {
                settings.Update(settings.Current with { NotchWidth = width });
                await Settle();
                var camera = (FrameworkElement)notch.FindName("HeaderCameraSpace");
                var cameraBounds = camera.TransformToAncestor(content).TransformBounds(new Rect(camera.RenderSize));
                var leftFits = new[] { "MusicTab", "FilesTab", "FocusTab", "CalendarTab" }.All(name =>
                {
                    var tab = (FrameworkElement)notch.FindName(name);
                    var bounds = tab.TransformToAncestor(content).TransformBounds(new Rect(tab.RenderSize));
                    return bounds.Left >= -.1 && bounds.Right <= cameraBounds.Left + .1;
                });
                var rightTab = (FrameworkElement)notch.FindName("ToolsTab");
                var rightBounds = rightTab.TransformToAncestor(content).TransformBounds(new Rect(rightTab.RenderSize));
                assert(leftFits && rightBounds.Left >= cameraBounds.Right - .1,
                    $"All five navigation targets avoid the camera space at custom width {width:0}");
                var bitmap = Render(notch);
                var centerX = bitmap.PixelWidth / 2;
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(centerX, 12, 1, 1), pixel, 4, 0);
                assert(pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 0 && pixel[3] == 255,
                    $"The calendar header preserves an opaque centered camera area at width {width:0}");
            }
            settings.Update(settings.Current with { NotchWidth = savedSettings.NotchWidth });
            await Capture("Calendar & agenda", "calendar.png");
            var addEvent = (Button)panel.FindName("AgendaAddButton");
            addEvent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            assert(panel.IsEditing && notch.IsActive,
                "Opening a calendar editor pins the notch for real keyboard input");
            ((TextBox)panel.FindName("EventTitleBox")).Text = "A new idea";
            await Capture("Add an event", "calendar-editor.png");
            ((UIElement)panel.FindName("EventEditor")).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(panel)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });

            notch.SelectTabForTest("tools");
            foreach (var tool in new[] { "notes", "reminders", "clocks", "stopwatch", "shortcuts" })
            {
                panel.SelectToolForTest(tool);
                await Settle();
                assert(panel.IsVisible && Math.Abs(island.ActualHeight - NotchMetrics.OrganizerExpandedHeight) < .01,
                    $"The {tool} tool renders inside the expanded notch");
                await Capture(tool switch
                { "notes" => "Quick notes", "reminders" => "Reminders", "clocks" => "World clocks", "stopwatch" => "Stopwatch", _ => "Quick actions" }, tool + ".png");
            }
            panel.SelectToolForTest("clocks");
            await Settle();
            assert(panel.DisplayTimerRunningForTest, "Visible clocks update their presentation");
            notch.SelectTabForTest("focus");
            await Settle();
            assert(!panel.DisplayTimerRunningForTest,
                "Switching away from tools stops clock and stopwatch display timers");
            focus.Reset(TimeSpan.FromSeconds(2730));
            await Capture("Your own focus duration", "focus-custom.png");

            media.SetSnapshotForTest(new MediaSnapshot
            {
                HasSession = true, IsPlaying = true, CanToggle = true, CanSeek = true,
                CanPrevious = true, CanNext = true, Source = "fixture", Title = "Midnight City", Artist = "M83",
                Position = TimeSpan.FromSeconds(50), Duration = TimeSpan.FromMinutes(4), Artwork = NotchVisualVerification.CreateArtwork()
            });
            notch.SelectTabForTest("music");
            await Settle();
            await Capture("Smooth player volume", "player-volume.png");
            settings.Update(settings.Current with { ReducedMotion = false });
            notch.SelectTabForTest("calendar");
            await Task.Delay(40);
            notch.SelectTabForTest("music");
            await Task.Delay(40);
            notch.SelectTabForTest("tools");
            await Task.Delay(700);
            assert(panel.Visibility == Visibility.Visible && panel.IsHitTestVisible && panel.Opacity == 1
                && Math.Abs(island.ActualHeight - NotchMetrics.OrganizerExpandedHeight) < .01,
                "Rapid page reversals settle on the selected tools surface without stale fade completions");
            SaveSheet(previews, Path.Combine(output, "productivity-contact-sheet.png"));
        }
        finally
        {
            foreach (var id in sampleEvents) panel.StoreForTest.RemoveEvent(id);
            foreach (var id in sampleReminders) panel.StoreForTest.RemoveReminder(id);
            notesBox.Text = savedNote;
            panel.FlushEdits();
            settings.Update(savedSettings);
            focus.Reset(TimeSpan.FromSeconds(savedSettings.FocusDurationSeconds));
            media.SetSnapshotForTest(savedMedia);
            notch.SelectTabForTest("music");
            notch.Collapse();
        }

        async Task Settle() { notch.UpdateLayout(); await Task.Delay(70); notch.UpdateLayout(); }
        async Task Capture(string title, string filename)
        {
            await Settle();
            var frame = Composite(Render(notch), island.ActualHeight + 34);
            Save(frame, Path.Combine(output, "productivity-" + filename));
            previews.Add(new Preview(title, frame));
        }
    }

    private static BitmapSource Render(Window window)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); bitmap.Freeze(); return bitmap;
    }
    private static BitmapSource Composite(BitmapSource image, double height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(196, 179, 232), Color.FromRgb(77, 148, 202), 20), null, new Rect(0, 0, 700, height));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 229, 209, 248)), null, new Point(195, height * 1.4), 330, height);
            drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var result = new RenderTargetBitmap(700, (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
    private static void SaveSheet(IReadOnlyList<Preview> previews, string path)
    {
        const int columnWidth = 700, gap = 18, rowHeight = 400;
        var rows = (previews.Count + 1) / 2;
        var width = columnWidth * 2 + gap * 3;
        var height = rows * rowHeight + gap;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 239, 245)), null, new Rect(0, 0, width, height));
            for (var i = 0; i < previews.Count; i++)
            {
                var x = gap + i % 2 * (columnWidth + gap);
                var y = gap + i / 2 * rowHeight;
                var label = new FormattedText(previews[i].Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                    12, new SolidColorBrush(Color.FromRgb(43, 47, 61)), 1);
                drawing.DrawText(label, new Point(x, y));
                drawing.DrawImage(previews[i].Image, new Rect(x, y + 25, previews[i].Image.PixelWidth, previews[i].Image.PixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); Save(bitmap, path);
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
