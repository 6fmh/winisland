# Apple Dynamic Island geometry

Researched 11 November 2026 for the optional iPhone appearance. The default MacBook contour and its tokens remain unchanged. Manual Google searches were made through background HTTP requests, including an isolated consent rejection; the search endpoint returned an enable-JavaScript/challenge page. Google responses are preserved in `artifacts/dynamic-island-research/geometry/`. The measurements below use directly retrieved official Apple references, rather than unsupported search snippets or remembered dimensions.

## Official sources and measured idle island

- [Apple's iPhone 14 Pro announcement](https://www.apple.com/newsroom/2022/09/apple-debuts-iphone-14-pro-and-iphone-14-pro-max/) describes Dynamic Island as an interface that fluidly expands for alerts, notifications and activities.
- The [frontal Lock Screen product image](https://www.apple.com/newsroom/images/product/iphone/standard/Apple-iPhone-14-Pro-iPhone-14-Pro-Max-iOS-16-Lock-Screen-220907_inline.jpg.large_2x.jpg) shows a detached black capsule with semicircular ends, centered horizontally near the display's top. Its hardware camera lens is visible inside the black region; software on Windows does not add a decorative lens.
- [Live Activities HIG](https://developer.apple.com/design/human-interface-guidelines/live-activities), retrieved through [official DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/live-activities.json), supplies normative logical activity dimensions and compact/expanded/minimal examples.
- Official illustrations: [compact](https://developer.apple.com/tutorials/images/com.apple.HIG/type-compact@2x.png), [expanded](https://developer.apple.com/tutorials/images/com.apple.HIG/type-expanded@2x.png), [minimal](https://developer.apple.com/tutorials/images/com.apple.HIG/type-minimal@2x.png), [balanced content](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-balanced-content@2x.png) and [default compact content](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-ios-dynamic-island-default@2x.png). Apple highlights the TrueDepth region in red for explanation; the application keeps this region black.

The downloaded frontal photograph is 1306 by 1828 pixels, SHA-256 `64332488e17c3dc91312ed2e2565ffeb3813effaa299bf8e4e77735144804209`. Its visible display spans x 302 through 1003 at straight sidewalls, giving 702 raster pixels. Normalizing this to the HIG's 393-point portrait display gives 1.7863 raster pixels per point. The central wallpaper begins at y 154. A mean-RGB threshold of 22 in the isolated camera rectangle identifies the housing from y 174 through 238, with a maximum row width of 223 pixels.

| Property | Observed source pixels | Normalized logical points | Application token |
| --- | ---: | ---: | ---: |
| Idle housing width | 223 | 124.84 | 126 |
| Housing height | 65 | 36.39 | 36.67 |
| Top display gap | 20 | 11.20 | 11 |

These are approximate raster measurements: JPEG sharpening, boundary antialiasing and a manually identified visible display edge affect the result. The HIG's explanatory camera diagram independently supports a width near 126 points. The application retains 126 for the center region and uses Apple's normative **36.67-point activity height**, rather than rounding that height to an integer. Apple does not publish one universal hardware housing dimension for every iPhone model.

`measure-idle.ps1`, `idle-measurement.json`, `idle-crop.png` and the source files in the ignored research directory reproduce and retain the measured rows without creating a window. The measurement supports this iPhone 14 Pro reference; it does not establish an exact physical dimension for all Apple displays or the final WPF rasterizer.

## Normative activity sizes

The retrieved HIG table specifies these dimensions in logical points:

| Portrait screen | Each compact leading/trailing region | Minimal region | Expanded region |
| --- | --- | --- | --- |
| 393 × 852 | 52.33 × 36.67 | 36.67–45 × 36.67 | 371 × 84–160 |
| 430 × 932 | 62.33 × 36.67 | 36.67–45 × 36.67 | 408 × 84–160 |

Winisland follows the 393-point reference: a 126-point reserved central region plus two 52.33-point compact activity regions, for a 230.66-point active island. A second activity uses the 36.67-point minimal circle, separated by an 8-DIP visual gap. The expanded music view is 371 by 160 DIPs. The 11-DIP top inset is measured from the monitor's actual top bounds, rather than the taskbar work area. WPF DIPs keep these logical proportions at 100%, 125%, 150% and 200% Windows scaling.

The diagrams explain regions and visual balance; their illustrative device frame is not a replacement for the HIG's normative sizing table. The expanded corner extent, interior spacing and detached secondary gap are visual-fit tokens rather than Apple-published hardware specifications. A wider panel for Winisland's existing organizer and controls is an application adaptation, while the music/compact/minimal presentation follows the iPhone reference.

## Contour, material and implementation

Apple's references show black fill throughout the island, without a light border. Idle and compact modes have a complete convex capsule contour and a wallpaper gap above. The MacBook silhouette instead has inward upper shoulders touching the display edge; it remains the default and its exact existing `NotchMetrics` values and quadratic geometry path are preserved.

`NotchSurface.IsDynamicIsland` is an opt-in dependency property with a false default. `CreateGeometry(width, height, expansion, dynamicIsland)` keeps all previous calls valid. Mode and expansion changes both notify the owning window to regenerate its clip. The same frozen geometry draws the pure black surface and clips content; no separate rectangular hit mask should include the transparent corners.

The idle contour uses two circular Bézier segments per quarter, with radius equal to half the height. Expanded corners interpolate toward a 44-DIP continuous-corner extent. Their two Bézier segments have tangent and zero-curvature joins to the straight edges, and a symmetric midpoint based on a fourth-power superellipse. This is an explicit visual approximation of Apple's continuous corners, not a claim that Apple exposes this control-point formula. Interpolation preserves a fully convex surface during expansion and reversal, while finite-dimension guards preserve the existing empty-geometry behavior for malformed inputs.

The implementation adds `DynamicIslandMetrics`; it does not repurpose the legacy dimension sliders or persisted MacBook preferences. Source-level compatibility and source measurements have been checked by this agent. The root's final `Build.ps1 -Verify` and private-desktop Dynamic Island suite are authoritative for rendered DPI, clipping, gap, live switching, reversal and behavior verification; this research document alone does not claim those runtime checks passed.
