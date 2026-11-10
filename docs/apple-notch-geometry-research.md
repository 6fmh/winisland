# Apple MacBook notch geometry

Researched 9 November 2026 for the current Apple-fidelity goal. Google Custom Search was attempted with two precise official Apple notch/AppKit queries; the configured API project returned HTTP 403. Research continued through official Apple product imagery and directly readable AppKit documentation, without opening a browser or touching the interactive desktop.

## Official visual reference

The [MacBook Air product page](https://www.apple.com/macbook-air/) contains a front-facing macOS product image showing the camera housing against a blue menu bar. Its [original image](https://www.apple.com/v/macbook-air/z/images/overview/macos/macos_hero__et3dsrq64iwm_large_2x.jpg) was served at 1960 by 1192 pixels. The notch is centered at approximately x 979.8, or 49.99% of image width. The image shows black, inward upper shoulders, straight side walls, curved lower corners and a flat bottom. It has no light border or wallpaper gap at its top connection.

The source image and measurement artifacts are in `artifacts/apple-design-reference/`, which is ignored by git. `geometry-comparison.png` shows the original crop enlarged four times and the previous/recommended silhouettes over the same source. The product image is a research artifact and is not included in the application payload.

## Measurements and recommended logical dimensions

The green channel separates the blue menu bar from the black housing. For each row from y 45 to y 77, the measurement script samples the exterior and interior colors, finds their midpoint along each edge, and interpolates between adjacent pixels. Rows 52-63 establish the straight side walls. Separate least-squares fits use the same quadratic contour family as `NotchSurface`: rows 45-51 fit the upper shoulders and rows 65-77 fit the bottom corners. The right edge is mirrored during fitting. JPEG sharpening, antialiasing and subpixel placement explain the small left/right asymmetry.

| Measured property | Left edge / right edge | Averaged source pixels | Normalized to 32 DIPs deep |
| --- | ---: | ---: | ---: |
| Upper shoulder extent | 6.46 / 6.25 | 6.355 | 5.966 |
| Lower quadratic corner extent | 13.43 / 12.47 | 12.950 | 12.158 |
| Depth from upper attachment to bottom | 34.34 / 33.83 | 34.085 | 32 |
| Width between straight walls | - | 189.749 | 178.142 |
| Full width including upper shoulders | - | 202.459 | 190.074 |

The recommended default is **190 by 32 DIPs, upper shoulders 6 and lower corner extent 12**. This reproduces the proportions of the official product image more closely than the prior third-party fallback of 185 by 32 with corner extents 6/14. The root agent owns applying these tokens, aligning persisted defaults, the centered camera space and verification assumptions. Existing deliberate custom dimensions remain user choices.

The comparison aligns both candidate contours at the measured center and top position and normalizes both to the same 34.085-pixel depth. It then compares both edges across all 33 measured rows:

| Contour | Root mean square edge error | Maximum absolute edge error |
| --- | ---: | ---: |
| Prior 185 by 32, shoulder 6, lower 14 | 2.999 source pixels | 4.222 source pixels |
| Recommended 190 by 32, shoulder 6, lower 12 | 0.102 source pixels | 0.272 source pixels |

Independent shoulder fits have root mean square error 0.065 px on the left and 0.060 px on the right; lower fits have error 0.090 px and 0.077 px. These figures measure a mathematical contour against one official raster image. They do not establish equivalent error for the actual WPF rasterizer, nor a universal physical Apple hardware dimension. Root's private-desktop verification must still inspect the actual application's settled size, clipping, camera space and 100%-200% DPI raster output.

`ClosedLowerRadius` is a quadratic corner extent, not a circular radius. Replacing the lower curve with a quarter-circle would change its shape. The existing `QuadraticBezierTo` control-point construction is consistent with this reference and should be retained.

## Placement, camera space and macOS behavior

Apple's [`safeAreaInsets`](https://developer.apple.com/documentation/appkit/nsscreen/safeareainsets) states that some Macs have screen space covered by camera housing and guarantees that content inside the safe area remains unobscured. macOS positions system full-screen content in that safe area automatically. [`auxiliaryTopLeftArea`](https://developer.apple.com/documentation/appkit/nsscreen/auxiliarytopleftarea-uglc) and its right-side counterpart describe unobscured upper areas in global screen coordinates. Their per-display results, rather than a universal fixed width, are authoritative on real Mac hardware.

The current Windows placement source uses the selected monitor's physical `Bounds.Top` and horizontal center, accounts for monitor DPI and requests nonactivating topmost placement. It does not use the taskbar work area, so the housing joins the physical display edge. The surface is pure black with no stroke. The clip is generated by the same geometry, and its fractional animated coordinates remain available to the rasterizer.

With the recommended 190-DIP width and 6-DIP shoulders, the central compact/header camera space is 178 DIPs. The existing equal activity wings preserve that space around the horizontal center. Its width should continue to be computed from the configured width minus twice the closed shoulder; hardcoded 173 reflects the superseded 185-DIP default. A taller custom housing or HUD retains `Expansion=0` and closed shoulders/corners. Expanded panel corners 19/24 and dimensions 640 by 190 remain software interface choices; Apple does not publish a built-in expanding music/file-shelf notch interface to copy.

Source inspection confirms these placement/contour invariants. Live private-desktop verification is owned by root. No production geometry files were edited by this research agent during concurrent builds.

## Reproducing the fit and comparison

Run this command in a background shell from the repository root:

```powershell
powershell -NoProfile -File artifacts/apple-design-reference/measure-geometry.ps1
```

The script uses offscreen image processing, samples the original JPEG, writes `raw-edge-measurements.json`, runs the bounded hidden Node fitter `fit-geometry.mjs`, and renders the PNG comparison without creating a window. `geometry-measurements.json` records every measured row, both fitted contours, normalized values, comparison errors and the source SHA-256. `render-geometry-comparison.ps1` reproduces the overlay independently from that report.

The initial source image hash is recorded in the measurement JSON. Image dimension changes abort measurement rather than silently reusing old coordinates. Re-measure a replacement product image or hardware model before asserting the same fitted numbers apply to it.
