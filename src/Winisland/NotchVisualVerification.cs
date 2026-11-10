using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Winisland.Controls;
using Winisland.Services;

namespace Winisland;

// Render the real controls as well as the silhouette. A transparent PNG shown on
// a black viewer cannot establish that the notch's shoulders actually look right.
internal static class NotchVisualVerification
{
    private const double CanvasWidth = 700;
    private const double CanvasHeight = 230;
    private sealed record Preview(string Label, BitmapSource Frame, double Height);

    internal static async Task RunAsync(App app, NotchWindow notch, SettingsStore store,
        MediaService media, FocusTimer focus, FileShelf shelf, string output, Action<bool, string> assert)
    {
        var originalSettings = store.Current;
        var previews = new List<Preview>();
        var collectPreviews = true;
        var island = Find<FrameworkElement>("Island");
        var surface = Find<NotchSurface>("Surface");
        var artwork = CreateArtwork();
        var fixture = new MediaSnapshot
        {
            HasSession = true, IsPlaying = true, CanToggle = true, CanPrevious = true,
            CanNext = true, CanSeek = true, Title = "Midnight City", Artist = "M83",
            Artwork = artwork, Duration = TimeSpan.FromMinutes(4), Position = TimeSpan.FromSeconds(42)
        };
        var uploadFile = Path.Combine(SettingsStore.DirectoryPath, "City lights.png");
        Save(artwork, uploadFile);
        var upload = new ShelfItem(uploadFile);
        try
        {
            store.Update(originalSettings with
            {
                ReducedMotion = true, HideInFullscreen = false, ExpandOnHover = false,
                ShowLiveActivity = true, NotchWidth = NotchMetrics.DefaultWidth,
                NotchHeight = NotchMetrics.DefaultHeight
            });
            focus.Reset(25); media.SetSnapshotForTest(new MediaSnapshot()); notch.Collapse();
            await Settle();
            shelf.Items.Add(upload);
            VerifyVariableGlyphs();
            // Make the rendered design reviewable even if a later measurement
            // catches a regression. Screenshots do not declare a check passed.
            await CaptureReview();
            SaveContactSheet(previews, Path.Combine(output, "fidelity-contact-sheet.png"));
            collectPreviews = false;

            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                foreach (var opened in new[] { false, true })
                {
                    var width = opened ? NotchMetrics.ExpandedWidth : NotchMetrics.DefaultWidth;
                    var height = opened ? NotchMetrics.ExpandedHeight : NotchMetrics.DefaultHeight;
                    var bitmap = RenderContour(width, height, opened ? 1 : 0, scale);
                    var name = $"contour-{(opened ? "open" : "closed")}-{scale * 100:0}";
                    Save(bitmap, Path.Combine(output, name + ".png"));
                    VerifyContourRaster(bitmap, width, height,
                        opened ? NotchMetrics.OpenShoulder : NotchMetrics.ClosedShoulder,
                        opened ? NotchMetrics.OpenLowerRadius : NotchMetrics.ClosedLowerRadius, scale, assert, name, output);
                }

                var shortContour = RenderContour(300, 32, 0, scale);
                foreach (var hudHeight in new[] { 48.0, 62.0 })
                {
                    var hudContour = RenderContour(300, hudHeight, 0, scale);
                    assert(TopRowsEqual(shortContour, hudContour, (int)Math.Floor(10 * scale)),
                        $"Custom height and {hudHeight:0}-DIP HUD retain the same closed shoulders at {scale * 100:0}% raster DPI");
                }
            }

            assert(Math.Abs(island.ActualWidth - NotchMetrics.DefaultWidth) < 0.01 && Math.Abs(island.ActualHeight - NotchMetrics.DefaultHeight) < 0.01,
                "Idle camera housing settles at the image-fitted MacBook Air proportions");
            await Capture("Idle", "fidelity-idle");

            var sizes = new[] { (160.0, 24.0), (160.0, 48.0), (320.0, 24.0), (320.0, 48.0), (185.0, 32.0), (NotchMetrics.DefaultWidth, NotchMetrics.DefaultHeight) };
            foreach (var (width, height) in sizes)
            {
                store.Update(store.Current with { NotchWidth = width, NotchHeight = height });
                for (var combination = 0; combination < 8; combination++)
                {
                    var music = (combination & 1) != 0;
                    var timer = (combination & 2) != 0;
                    var uploading = (combination & 4) != 0;
                    focus.Reset(25);
                    if (timer) { focus.Start(width == NotchMetrics.DefaultWidth && height == NotchMetrics.DefaultHeight ? 25 : 120); focus.TogglePause(); }
                    // This tests presentation through the real service's pending
                    // count without enqueuing a request or contacting a host.
                    upload.SetUpload(uploading ? UploadState.Uploading : UploadState.Local, uploading ? 47 : 0);
                    media.SetSnapshotForTest(music ? fixture : new MediaSnapshot());
                    notch.Collapse();
                    await Settle();
                    assert(!notch.IsExpanded && surface.Expansion == 0
                        && Math.Abs(island.ActualHeight - height) < 0.01
                        && (combination != 0 || Math.Abs(island.ActualWidth - width) < 0.01),
                        $"Compact activities retain closed corners and requested height at {width:0} by {height:0}, combination {combination}");
                    if (combination == 0) continue;

                    var camera = Find<FrameworkElement>("CompactCameraSpace");
                    var cameraBounds = Bounds(camera, notch);
                    var leftWing = Find<FrameworkElement>("CompactLeftWing");
                    var rightWing = Find<FrameworkElement>("CompactRightWing");
                    assert(Math.Abs(camera.ActualWidth - (width - 2 * NotchMetrics.ClosedShoulder)) < 0.01
                        && Math.Abs(cameraBounds.Left + cameraBounds.Width / 2 - notch.ActualWidth / 2) < 0.6
                        && Math.Abs(leftWing.ActualWidth - rightWing.ActualWidth) < 0.01,
                        $"Activity wings leave the original camera space centered at {width:0} by {height:0}, combination {combination}");
                    var windowBitmap = Render(notch, 1);
                    assert(IsBlackRegion(windowBitmap, cameraBounds, 1),
                        $"Camera space stays opaque black with music, focus and upload activity at {width:0} by {height:0}, combination {combination}");
                    var islandBounds = Bounds(island, notch);
                    var controlsFit = true;
                    foreach (var name in new[] { "CompactArtwork", "CompactBars", "CompactTimer", "CompactLabel", "CompactPlaybackIcon" })
                    {
                        var control = Find<FrameworkElement>(name);
                        if (!control.IsVisible || control.ActualWidth == 0 || control.ActualHeight == 0) continue;
                        var controlBounds = Bounds(control, notch);
                        controlsFit &= Contains(islandBounds, controlBounds, 0.6);
                        controlsFit &= name == "CompactArtwork"
                            ? controlBounds.Right <= cameraBounds.Left + 0.6
                            : controlBounds.Left >= cameraBounds.Right - 0.6;
                    }
                    assert(controlsFit,
                        $"Compact content fits inside side wings without crossing the camera at {width:0} by {height:0}, combination {combination}");
                    if (width == NotchMetrics.DefaultWidth && height == NotchMetrics.DefaultHeight && combination is 1 or 2 or 3 or 4)
                    {
                        var label = combination switch { 1 => "Music", 2 => "Focus", 3 => "Music + focus", _ => "Uploading" };
                        await Capture(label, $"fidelity-compact-{combination}");
                    }
                }
            }

            upload.SetUpload(UploadState.Local);
            focus.Reset(25); media.SetSnapshotForTest(fixture); notch.SelectTabForTest("music");
            store.Update(store.Current with { NotchWidth = NotchMetrics.DefaultWidth, NotchHeight = NotchMetrics.DefaultHeight });
            notch.Expand(true);
            await Settle();
            assert(Math.Abs(island.ActualWidth - 640) < 0.01 && Math.Abs(island.ActualHeight - 190) < 0.01
                && surface.Expansion == 1,
                "Expanded panel renders at 640 by 190 with its open corner contour");
            var art = Find<FrameworkElement>("AlbumArtwork");
            assert(Math.Abs(art.ActualWidth - 90) < 0.01 && Math.Abs(art.ActualHeight - 90) < 0.01,
                "Expanded artwork uses the reference 90 by 90 logical-point size");
            var expandedBounds = Bounds(island, notch);
            var visibleControlsFit = new[] { "TrackTitle", "TrackArtist", "AlbumArtwork", "SeekSlider", "PreviousButton", "PlayButton", "NextButton", "VolumeSlider" }
                .Select(Find<FrameworkElement>).Where(element => element.IsVisible).All(element => Contains(expandedBounds, Bounds(element, notch), 0.6));
            assert(visibleControlsFit, "Expanded title, artwork and media controls fit the shallow panel without clipping");
            var title = Find<TextBlock>("TrackTitle");
            var face = new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch);
            assert(face.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Any(name => name == "Inter")
                && glyph.FontUri.ToString().Contains("assets/fonts/", StringComparison.OrdinalIgnoreCase),
                "Published notch text resolves to the bundled Inter font instead of an installed-font fallback");
            var bluePixels = new byte[8 * 8 * 4];
            for (var i = 0; i < bluePixels.Length; i += 4) { bluePixels[i] = 64; bluePixels[i + 3] = 255; }
            var blueArtwork = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, bluePixels, 8 * 4);
            blueArtwork.Freeze();
            media.SetSnapshotForTest(fixture with { Artwork = blueArtwork });
            await Settle();
            var artist = Find<TextBlock>("TrackArtist");
            var artistBrush = (SolidColorBrush)artist.Foreground;
            var color = artistBrush.Color;
            var effectiveOpacity = color.ScA * artistBrush.Opacity * artist.Opacity;
            var luminance = (0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB) * effectiveOpacity;
            assert((luminance + 0.05) / 0.05 >= 4.5,
                "Highly saturated blue artwork still gives small artist text at least 4.5 to 1 contrast against black");
            await Capture("Blue artwork contrast", "fidelity-blue-artwork");
            media.SetSnapshotForTest(fixture);
            await Settle();
            var bars = Find<FrameworkElement>("CompactBars");
            assert(Math.Abs(bars.Width - 16) < 0.01 && Math.Abs(bars.Height - 12) < 0.01,
                "Compact spectrogram retains the reference 16 by 12 logical-point footprint");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var bitmap = Render(notch, scale);
                assert(IsBlackRegion(bitmap, new Rect(expandedBounds.Left + 50, 0, expandedBounds.Width - 100, 1 / scale), scale),
                    $"Rendered expanded panel touches the top edge without a seam at {scale * 100:0}% raster DPI");
                Save(Composite(bitmap, island.ActualHeight + 35, scale), Path.Combine(output, $"fidelity-music-{scale * 100:0}-desktop.png"));
            }
            await Capture("Now playing", "fidelity-expanded-music");
            media.SetSnapshotForTest(fixture with { IsPlaying = false });
            await Settle();
            await Capture("Paused", "fidelity-expanded-paused");
            media.SetSnapshotForTest(fixture);
            notch.SelectTabForTest("focus"); focus.Start(25); focus.TogglePause();
            await Settle();
            await Capture("Focus timer", "fidelity-expanded-focus");
            notch.SelectTabForTest("files");
            await Settle();
            await Capture("File shelf", "fidelity-expanded-shelf");
            notch.SelectTabForTest("music"); focus.Reset(25); media.SetSnapshotForTest(new MediaSnapshot());
            await Settle();
            await Capture("Waiting for music", "fidelity-expanded-empty");

            notch.Collapse();
            notch.ShowHudForTest(NotchIconKind.Speaker, 64);
            await Settle();
            assert(surface.Expansion == 0 && Math.Abs(island.ActualHeight - 48) < 0.01,
                "Volume HUD remains a closed contour while increasing its height");
            await Capture("App volume", "fidelity-volume-hud");
            notch.Expand(false); notch.Collapse();
            notch.ShowHudForTest(NotchIconKind.Check, 100);
            await Settle();
            assert(surface.Expansion == 0 && Math.Abs(island.ActualHeight - 62) < 0.01,
                "Completion HUD keeps the small top shoulders independently of its height");
            await Capture("Focus complete", "fidelity-completion-hud");

            SaveContactSheet(previews, Path.Combine(output, "fidelity-contact-sheet.png"));
        }
        finally
        {
            shelf.Items.Remove(upload);
            media.SetSnapshotForTest(new MediaSnapshot()); focus.Reset(25);
            notch.Expand(false); notch.Collapse();
            store.Update(originalSettings);
        }

        T Find<T>(string name) where T : FrameworkElement => (T)notch.FindName(name);
        void VerifyVariableGlyphs()
        {
            var glyph = new NotchIcon { Width = 24, Height = 18 };
            var host = new Grid { Width = 40, Height = 32 };
            host.Children.Add(glyph);
            host.Measure(new Size(40, 32)); host.Arrange(new Rect(0, 0, 40, 32)); host.UpdateLayout();
            var batteries = new List<int>();
            var speakers = new List<int>();
            foreach (var level in new[] { 0.0, 25.0, 50.0, 100.0 })
            {
                glyph.Kind = NotchIconKind.Battery; glyph.BatteryLevel = level;
                var battery = Render(host, 2);
                batteries.Add(Coverage(battery));
                Save(battery, Path.Combine(output, $"glyph-battery-{level:0}.png"));
                glyph.Kind = NotchIconKind.Speaker; glyph.SoundLevel = level;
                var speaker = Render(host, 2);
                speakers.Add(Coverage(speaker));
                Save(speaker, Path.Combine(output, $"glyph-speaker-{level:0}.png"));
            }
            assert(batteries.Zip(batteries.Skip(1), (left, right) => right > left).All(increases => increases),
                "Rendered battery fill grows with the actual percentage rather than showing a fixed charge");
            assert(speakers.Zip(speakers.Skip(1), (left, right) => right > left).All(increases => increases),
                "Rendered speaker progressively adds sound-wave layers from silence through full volume");
            glyph.Kind = NotchIconKind.Battery; glyph.BatteryLevel = 100;
            var switched = Render(host, 2);
            var freshGlyph = new NotchIcon { Width = 24, Height = 18, Kind = NotchIconKind.Battery, BatteryLevel = 100 };
            host.Children.Clear(); host.Children.Add(freshGlyph);
            host.Measure(new Size(40, 32)); host.Arrange(new Rect(0, 0, 40, 32)); host.UpdateLayout();
            assert(Pixels(switched).AsSpan().SequenceEqual(Pixels(Render(host, 2))),
                "Switching between speaker and wide battery symbols refreshes their optical drawing bounds");

            static int Coverage(BitmapSource bitmap)
            {
                var pixels = Pixels(bitmap);
                var total = 0;
                for (var index = 3; index < pixels.Length; index += 4) total += pixels[index];
                return total;
            }
        }
        async Task Settle()
        {
            notch.UpdateLayout();
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(35);
            notch.UpdateLayout();
        }
        async Task Capture(string label, string name)
        {
            await Settle();
            var bitmap = Render(notch, 1);
            var height = Math.Min(bitmap.Height, island.ActualHeight + 35);
            Save(Composite(bitmap, height, 1), Path.Combine(output, name + "-desktop.png"));
            if (collectPreviews) previews.Add(new Preview(label, bitmap, height));
        }
        async Task CaptureReview()
        {
            await Capture("Idle", "fidelity-idle");
            media.SetSnapshotForTest(fixture);
            await Capture("Music", "fidelity-compact-1");
            focus.Start(25); focus.TogglePause();
            await Capture("Music + focus", "fidelity-compact-3");
            media.SetSnapshotForTest(new MediaSnapshot());
            await Capture("Focus", "fidelity-compact-2");
            upload.SetUpload(UploadState.Uploading, 47); media.SetSnapshotForTest(new MediaSnapshot());
            await Capture("Uploading", "fidelity-compact-4");
            upload.SetUpload(UploadState.Local); focus.Reset(25); media.SetSnapshotForTest(fixture);
            notch.SelectTabForTest("music"); notch.Expand(true);
            await Capture("Now playing", "fidelity-expanded-music");
            media.SetSnapshotForTest(fixture with { IsPlaying = false });
            await Capture("Paused", "fidelity-expanded-paused");
            notch.SelectTabForTest("focus"); focus.Start(25); focus.TogglePause();
            await Capture("Focus timer", "fidelity-expanded-focus");
            notch.SelectTabForTest("files");
            await Capture("File shelf", "fidelity-expanded-shelf");
            notch.SelectTabForTest("music"); focus.Reset(25); media.SetSnapshotForTest(new MediaSnapshot());
            await Capture("Waiting for music", "fidelity-expanded-empty");
            notch.Collapse(); notch.ShowHudForTest(NotchIconKind.Speaker, 64);
            await Capture("App volume", "fidelity-volume-hud");
            notch.Expand(false); notch.Collapse(); notch.ShowHudForTest(NotchIconKind.Check, 100);
            await Capture("Focus complete", "fidelity-completion-hud");
            notch.Expand(false); notch.Collapse();
            await Settle();
        }
    }

    internal static BitmapSource CreateArtwork()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(20, 31, 87), Color.FromRgb(215, 63, 119), 90), null, new Rect(0, 0, 180, 180));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 204, 158)), null, new Point(127, 92), 35, 35);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 20, 48)), null, new Rect(0, 132, 180, 48));
            var buildings = new[] { (3, 113, 18), (25, 95, 22), (53, 122, 17), (76, 109, 20), (102, 126, 16), (125, 102, 23), (155, 118, 25) };
            foreach (var (x, y, width) in buildings)
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 24, 55)), null, new Rect(x, y, width, 180 - y));
                for (var windowY = y + 8; windowY < 174; windowY += 11)
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(125, 173, 219)), null, new Rect(x + 6, windowY, 3, 4));
            }
            drawing.DrawText(Text("M83", 20, Brushes.White, FontWeights.SemiBold), new Point(13, 11));
            drawing.DrawText(Text("MIDNIGHT CITY", 8, Brushes.White), new Point(14, 36));
        }
        var bitmap = new RenderTargetBitmap(180, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RenderContour(double width, double height, double expansion, double scale)
    {
        var surface = new NotchSurface
        {
            Width = width, Height = height, Expansion = expansion,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        var host = new Grid { Width = CanvasWidth, Height = CanvasHeight, UseLayoutRounding = false };
        host.Children.Add(surface); host.Measure(new Size(CanvasWidth, CanvasHeight));
        host.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight)); host.UpdateLayout();
        return Render(host, scale);
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double scale)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze();
        return bitmap;
    }

    private static void VerifyContourRaster(BitmapSource bitmap, double width, double height,
        double shoulder, double radius, double scale, Action<bool, string> assert, string name, string output)
    {
        var pixels = Pixels(bitmap);
        var left = (CanvasWidth - width) / 2;
        bool Black(double x, double y) => Pixel(pixels, bitmap.PixelWidth, (int)Math.Floor(x * scale), (int)Math.Floor(y * scale)) is { B: 0, G: 0, R: 0, A: 255 };
        byte Alpha(double x, double y) => Pixel(pixels, bitmap.PixelWidth, (int)Math.Floor(x * scale), (int)Math.Floor(y * scale)).A;
        var opaqueTop = true;
        for (var x = (int)Math.Ceiling((left + shoulder + 2) * scale); x < (left + width - shoulder - 2) * scale; x++)
        {
            var pixel = Pixel(pixels, bitmap.PixelWidth, x, 0);
            opaqueTop &= pixel is { B: 0, G: 0, R: 0, A: 255 };
        }
        assert(opaqueTop && Black(CanvasWidth / 2, height / 2), $"{name}: opaque black reaches the top edge without a one-pixel gap");
        assert(Alpha(left + 1, height / 2) == 0 && Alpha(left + shoulder * 0.35, shoulder * 0.6) < 40
            && Alpha(left + shoulder + radius * 0.2, height - 0.7) < 40,
            $"{name}: inward top shoulders and rounded lower corners leave transparent cutaways");
        var geometry = NotchSurface.CreateGeometry(width, height, shoulder == NotchMetrics.OpenShoulder ? 1 : 0);
        var reflected = geometry.Clone();
        reflected.Transform = new MatrixTransform(-1, 0, 0, 1, width, 0);
        var symmetricArea = Geometry.Combine(geometry, reflected, GeometryCombineMode.Xor, null,
            0.0001, ToleranceType.Absolute).GetArea(0.0001, ToleranceType.Absolute);
        var symmetricCoverage = true;
        var antialiased = false;
        var boundaryOnly = true;
        var solidInterior = true;
        var allBlack = true;
        var maximumCoverageDifference = 0.0;
        var maximumAlphaDifference = 0;
        var partialPixels = 0;
        for (var y = 0; y < Math.Ceiling(height * scale); y++)
        {
            long leftCoverage = 0, rightCoverage = 0;
            byte previousLeft = 0, previousRight = 0;
            for (var x = 0; x < bitmap.PixelWidth / 2; x++)
            {
                var pixel = Pixel(pixels, bitmap.PixelWidth, x, y);
                var reflectedX = bitmap.PixelWidth - 1 - x;
                var reflectedPixel = Pixel(pixels, bitmap.PixelWidth, reflectedX, y);
                var alpha = pixel.A; var mirror = reflectedPixel.A;
                leftCoverage += alpha; rightCoverage += mirror;
                solidInterior &= alpha >= previousLeft && mirror >= previousRight;
                previousLeft = alpha; previousRight = mirror;
                allBlack &= pixel is { B: 0, G: 0, R: 0 } && reflectedPixel is { B: 0, G: 0, R: 0 };
                maximumAlphaDifference = Math.Max(maximumAlphaDifference, Math.Abs(alpha - mirror));
                antialiased |= alpha is > 0 and < 255;
                foreach (var (sampleX, sampleAlpha) in new[] { (x, alpha), (reflectedX, mirror) })
                {
                    if (sampleAlpha is > 0 and < 255) partialPixels++;
                    if (sampleAlpha is > 0 and < 255 || (alpha != mirror && sampleAlpha == 255))
                    {
                        // WPF quantizes left/right edge coverage differently.
                        // Permit a quarter physical pixel around a boundary,
                        // while rejecting partial or mismatched opaque pixels
                        // in the interior, and antialiasing away from the path.
                        var footprint = new Rect(sampleX / scale - left, y / scale, 1 / scale, 1 / scale);
                        footprint.Inflate(0.25 / scale, 0.25 / scale);
                        var intersection = geometry.FillContainsWithDetail(new RectangleGeometry(footprint),
                            0.001 / scale, ToleranceType.Absolute);
                        boundaryOnly &= intersection is IntersectionDetail.Intersects or IntersectionDetail.FullyInside;
                    }
                }
            }
            var difference = Math.Abs(leftCoverage - rightCoverage) / 255.0;
            maximumCoverageDifference = Math.Max(maximumCoverageDifference, difference);
            symmetricCoverage &= difference <= 0.25;
            if (bitmap.PixelWidth % 2 != 0)
            {
                var middleX = bitmap.PixelWidth / 2;
                var middle = Pixel(pixels, bitmap.PixelWidth, middleX, y);
                allBlack &= middle is { B: 0, G: 0, R: 0 };
                if (middle.A is > 0 and < 255)
                {
                    partialPixels++;
                    var footprint = new Rect(middleX / scale - left, y / scale, 1 / scale, 1 / scale);
                    footprint.Inflate(0.25 / scale, 0.25 / scale);
                    var intersection = geometry.FillContainsWithDetail(new RectangleGeometry(footprint),
                        0.001 / scale, ToleranceType.Absolute);
                    boundaryOnly &= intersection is IntersectionDetail.Intersects or IntersectionDetail.FullyInside;
                }
            }
        }
        var audit = new
        {
            name, bitmap.PixelWidth, bitmap.PixelHeight, maximumCoverageDifference,
            maximumAlphaDifference, partialPixels, symmetricArea, boundaryOnly, solidInterior, allBlack
        };
        File.WriteAllText(Path.Combine(output, name + "-metrics.json"),
            JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }));
        assert(symmetricArea < 0.0001 && symmetricCoverage && solidInterior,
            $"{name}: mirrored path and alpha-weighted scanline boundaries agree within a quarter physical pixel");
        assert(antialiased && boundaryOnly && allBlack,
            $"{name}: antialiasing stays at the black contour boundary without interior holes or color contamination");
    }

    private static bool TopRowsEqual(BitmapSource first, BitmapSource second, int rows)
    {
        var a = Pixels(first); var b = Pixels(second);
        return a.AsSpan(0, first.PixelWidth * rows * 4).SequenceEqual(b.AsSpan(0, second.PixelWidth * rows * 4));
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static bool Contains(Rect outside, Rect inside, double tolerance) =>
        inside.Left >= outside.Left - tolerance && inside.Top >= outside.Top - tolerance
        && inside.Right <= outside.Right + tolerance && inside.Bottom <= outside.Bottom + tolerance;

    private static bool IsBlackRegion(BitmapSource bitmap, Rect region, double scale)
    {
        var pixels = Pixels(bitmap);
        var left = Math.Max(0, (int)Math.Ceiling((region.Left + Math.Min(1, region.Width / 4)) * scale));
        var right = Math.Min(bitmap.PixelWidth, (int)Math.Floor((region.Right - Math.Min(1, region.Width / 4)) * scale));
        var top = Math.Max(0, (int)Math.Ceiling(region.Top * scale));
        var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Floor(region.Bottom * scale));
        if (left >= right || top >= bottom) return false;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                if (Pixel(pixels, bitmap.PixelWidth, x, y) is not { B: 0, G: 0, R: 0, A: 255 }) return false;
        return true;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bytes, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3]);
    }

    private static BitmapSource Composite(BitmapSource bitmap, double height, double scale)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            DrawWallpaper(drawing, new Rect(0, 0, CanvasWidth, height));
            // The window can be taller than the contour fixture. Preserve its
            // logical raster size; fitting it to the fixture squashes the notch.
            drawing.DrawImage(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height));
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling(CanvasWidth * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    private static void SaveContactSheet(IReadOnlyList<Preview> previews, string path)
    {
        const int gap = 18;
        var rowHeights = Enumerable.Range(0, (previews.Count + 1) / 2)
            .Select(row => Math.Max(previews[row * 2].Height, row * 2 + 1 < previews.Count ? previews[row * 2 + 1].Height : 0) + 32).ToArray();
        var width = (int)CanvasWidth * 2 + gap * 3;
        var height = (int)Math.Ceiling(rowHeights.Sum() + gap * (rowHeights.Length + 1));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 239, 245)), null, new Rect(0, 0, width, height));
            var y = (double)gap;
            for (var row = 0; row < rowHeights.Length; row++)
            {
                for (var column = 0; column < 2 && row * 2 + column < previews.Count; column++)
                {
                    var preview = previews[row * 2 + column];
                    var x = gap + column * (CanvasWidth + gap);
                    drawing.DrawText(Text(preview.Label, 12, new SolidColorBrush(Color.FromRgb(43, 47, 61)), FontWeights.Medium), new Point(x, y));
                    drawing.PushClip(new RectangleGeometry(new Rect(x, y + 26, CanvasWidth, preview.Height)));
                    DrawWallpaper(drawing, new Rect(x, y + 26, CanvasWidth, preview.Height));
                    drawing.DrawImage(preview.Frame, new Rect(x, y + 26, preview.Frame.Width, preview.Frame.Height));
                    drawing.Pop();
                }
                y += rowHeights[row] + gap;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); Save(bitmap, path);
    }

    private static void DrawWallpaper(DrawingContext drawing, Rect bounds)
    {
        drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(192, 186, 244), Color.FromRgb(86, 156, 207), 25), null, bounds);
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(75, 255, 190, 211)), null,
            new Point(bounds.Left + bounds.Width * 0.22, bounds.Top + bounds.Height * 1.7), bounds.Width * 0.6, bounds.Height * 1.8);
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 90, 81, 172)), null,
            new Point(bounds.Right, bounds.Top + bounds.Height * 1.6), bounds.Width * 0.45, bounds.Height * 1.4);
        drawing.Pop();
    }

    private static FormattedText Text(string value, double size, Brush brush, FontWeight? weight = null) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, 1);

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

}
