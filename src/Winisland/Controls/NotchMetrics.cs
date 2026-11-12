namespace Winisland.Controls;

// Closed proportions fitted to Apple's official frontal MacBook Air imagery.
// Expanded layout tokens retain the earlier Boring Notch panel adaptation.
// Measurements and model limits: docs/apple-notch-geometry-research.md.
public static class NotchMetrics
{
    public const double DefaultWidth = 190;
    public const double DefaultHeight = 32;
    public const double ExpandedWidth = 640;
    public const double ExpandedHeight = 190;
    public const double OrganizerExpandedHeight = 332;
    public const double ControlCenterExpandedHeight = 356;
    public const double UpdateExpandedHeight = 376;
    public const double UpdateCompactHeight = 236;
    public const double ClosedShoulder = 6;
    public const double ClosedLowerRadius = 12;
    public const double DefaultCameraWidth = DefaultWidth - 2 * ClosedShoulder;
    public const double OpenShoulder = 19;
    public const double OpenLowerRadius = 24;
    public const double ContentInset = 31;
    public const double ContentBottomInset = 12;
    public const double HeaderHeight = 32;
    public const double ShadowPadding = 20;
    public const double ArtworkSize = 90;
    public const double ArtworkRadius = 13;
    public const double CompactArtworkSize = 20;
    public const double CompactArtworkRadius = 4;
    public const double CompactSpectrogramWidth = 16;
    public const double CompactSpectrogramHeight = 12;
}
