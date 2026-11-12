namespace Winisland.Controls;

// iPhone 14 Pro proportions: Apple's frontal newsroom raster and the Live
// Activities HIG logical-size table. WPF DIPs preserve those logical dimensions
// at each Windows display scale. Research: docs/apple-dynamic-island-geometry-research.md.
public static class DynamicIslandMetrics
{
    public const double IdleWidth = 126;
    public const double Height = 36.67;
    public const double TopInset = 11;
    public const double CameraWidth = IdleWidth;
    public const double CompactWingWidth = 52.33;
    public const double CompactWidth = CameraWidth + 2 * CompactWingWidth;
    public const double ExpandedWidth = 371;
    public const double ExpandedHeight = 160;
    // Desktop activity chooser: shares the iPhone width and continuous housing.
    public const double MenuHeight = 204;
    // Continuous-corner extent and content tokens are visual fits, rather than
    // hardware specifications published by Apple.
    public const double ExpandedRadius = 44;
    public const double ContentInset = 20;
    public const double ArtworkSize = 48;
    public const double ArtworkRadius = 8;
    public const double CompactArtworkSize = 22;
    public const double CompactArtworkRadius = 4;
    public const double MinimalSize = Height;
    public const double MinimalGap = 8;
}
