# Notch geometry research

Historical software reference, superseded for the closed housing by the [9 November Apple image measurements](../apple-notch-geometry-research.md). Current defaults are190 × 32 DIPs and6/12 corner extents. The table below records earlier185/14 tokens at the pinned Boring Notch revision; it is not the current Winisland specification.

Checked 7 November 2026. The user's selected expanding-panel reference is **Boring Notch**. Source measurements below are pinned to commit [`b7611bbf32c627a3427408fc142486900d71a23f`](https://github.com/TheBoredTeam/boring.notch/tree/b7611bbf32c627a3427408fc142486900d71a23f), and were read directly from that revision.

## Measured software reference

| Token | Boring Notch value in logical points | Winisland token in DIPs |
| --- | ---: | --- |
| Closed width without hardware measurements | 185 | `DefaultWidth` |
| Default closed height | 32 | `DefaultHeight` |
| Expanded panel | 640 × 190 | `ExpandedWidth`, `ExpandedHeight` |
| Closed inward shoulder | 6 | `ClosedShoulder` |
| Closed lower corner | 14 | `ClosedLowerRadius` |
| Expanded inward shoulder | 19 | `OpenShoulder` |
| Expanded lower corner | 24 | `OpenLowerRadius` |
| Expanded horizontal content inset | 19 + 12 = 31 | `ContentInset` |
| Expanded bottom content inset | 12 | `ContentBottomInset` |
| Expanded header minimum height | 32 | `HeaderHeight` |
| Expanded artwork sizing token / corner radius | 90 / 13 | `ArtworkSize`, `ArtworkRadius` |
| Compact artwork / corner radius | 20 / 4 | `CompactArtworkSize`, `CompactArtworkRadius` |
| Compact spectrum mask | 16 × 12 | `CompactSpectrogramWidth`, `CompactSpectrogramHeight` |
| Extra bottom space for the source's shadow | 20 | `ShadowPadding` |

Sources: [`sizing/matters.swift`](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/sizing/matters.swift), [`models/Constants.swift`](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/models/Constants.swift), and [`ContentView.swift`](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/ContentView.swift).

The 90-point artwork size is a declared source token. The latest `AlbumArtView` also uses the size offered by its parent, so it is not a guarantee that every live Boring Notch screenshot contains a 90-point image. Winisland deliberately uses the declared token.

## Exact contour

[`NotchShape.swift`](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Notch/NotchShape.swift) draws **quadratic curves**, not circular arcs. With width `w`, height `h`, shoulder `s`, and lower radius token `r`, the contour is:

1. Start at `(0, 0)` and curve through control `(s, 0)` to `(s, s)`.
2. Continue vertically to `(s, h − r)`.
3. Curve through control `(s, h)` to `(s + r, h)`.
4. Continue along the bottom and mirror those curves on the right.
5. Close with a straight top edge at `y = 0`.

The curves meet the straight edges tangentially. Their numerical radius tokens describe their extent; they are not quarter-circle radii. The expanded panel has a smaller lower radius than Winisland's previous 32-DIP corner, giving the reference's wider, shallower contour. `NotchSurface.CreateGeometry` uses WPF's `QuadraticBezierTo` with these control points and clamps corner extents only when dimensions are too small to contain them.

The source draws pure black and overlays a 1-point black rectangle at the top, inset horizontally by its current shoulder radius. It has no stroke or light border. Winisland's black fill also begins at `y = 0`; placement at the physical display's top edge and matching the drawing to its clip are essential for avoiding a wallpaper seam. Antialiasing stays enabled for the curved contour.

`NotchSurface.Expansion` interpolates the corner tokens separately from panel height. A volume HUD or a user choosing a taller closed notch therefore keeps the closed contour. `ContourChanged` lets the containing window update its clip at each animation step, including steps when its dimensions do not change.

## Real camera housing and exact pixels

Apple's MacBook notch is physical camera housing. Expanding music, timer, and shelf interfaces come from third-party apps. Apple's [`safeAreaInsets`](https://developer.apple.com/documentation/appkit/nsscreen/safeareainsets) describes the area covered by the camera housing, while [`auxiliaryTopLeftArea`](https://developer.apple.com/documentation/appkit/nsscreen/auxiliarytopleftarea-uglc) and its right-hand counterpart describe the unobscured upper areas. These APIs report the individual display's geometry; they do not establish a universal hardware notch width or corner radius.

Boring Notch calculates hardware width as `screen.frame.width − auxiliaryTopLeftArea.width − auxiliaryTopRightArea.width + 4`. The added 4 points are software overlap, not an Apple camera-housing measurement. Depending on the selected height mode, the app uses the safe-area top inset, the menu-bar height, or its custom height. **185 × 32 is the software fallback chosen for the Windows implementation, not a claimed exact measurement of every MacBook.**

Apple's [`backingScaleFactor`](https://developer.apple.com/documentation/appkit/nsscreen/backingscalefactor) defines backing-store pixels per linear screen-space unit. A point measurement cannot be called a physical pixel measurement without the display's scale. Windows DIPs similarly use display scaling; the nominal mapping for the 185 × 32 DIP fallback is:

| Windows scale | Nominal physical extent before raster rounding |
| --- | ---: |
| 100% | 185 × 32 pixels |
| 125% | 231.25 × 40 pixels |
| 150% | 277.5 × 48 pixels |
| 200% | 370 × 64 pixels |

Winisland should preserve fractional animated coordinates for smooth curves, keep the top location on the display's physical top pixel, and round only native window placement. This retains the reference's proportions across DPI settings.

## Evidence and limits

Confidence is high for the pinned Boring Notch tokens, contour control points, black fill, and Apple API semantics. A source-independent universal Apple housing width or physical corner-pixel measurement was not found and is not asserted. Google Custom Search's configured project returned HTTP 403; research continued through directly readable official Apple documentation and pinned GitHub source files.
