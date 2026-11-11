using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Winisland.Services;

internal static class ArtworkDecoder
{
    internal const int MaximumEdge = 160;

    internal static BitmapSource Decode(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        // Query dimensions before selecting the decode edge. Width alone would
        // expand a narrow portrait into an arbitrarily tall decoded bitmap.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, MaximumEdge);
        else bitmap.DecodePixelHeight = Math.Min(height, MaximumEdge);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        // BitmapImage coerces StreamSource back to its original value after
        // EndInit. Copy only the bounded decoded pixels so neither its decoder
        // nor its encoded stream can escape with the published artwork.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return detached;
    }
}
