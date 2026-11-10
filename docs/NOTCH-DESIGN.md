# Winisland notch design

The current design goal is the closest practical match to Apple's MacBook camera housing and macOS visual language. Agents independently researched official product imagery, typography, symbols, colors and motion, and coordinated with the separate bug-review team in [communication.md](../communication.md). Earlier expanding-panel work used Boring Notch source pinned to [`b7611bbf32c627a3427408fc142486900d71a23f`](https://github.com/TheBoredTeam/boring.notch/tree/b7611bbf32c627a3427408fc142486900d71a23f). Apple provides the hardware silhouette and system design references; its physical MacBook notch does not itself expand into these panels.

## Shape and layout

| Detail | Implemented measurement in Windows DIPs |
| --- | ---: |
| Default closed housing | 190 × 32 |
| Expanded surface | 640 × 190 |
| Control Center surface | 640 × 356 |
| Closed top shoulder / bottom corner extent | 6 / 12 |
| Expanded top shoulder / bottom corner extent | 19 / 24 |
| Expanded horizontal / bottom content inset | 31 / 12 |
| Header height / visible tab capsule height | 32 / 26 |
| Open artwork / corner | 90 × 90 / 13 |
| Compact artwork / corner | 20 × 20 / 4 |
| Compact spectrogram | 16 × 12 |

`NotchSurface` uses quadratic curves, including the inward shoulders that join the side walls to the display edge. Both mirrored boundaries of Apple's official frontal MacBook Air image were fitted and normalized to a 32-DIP depth: the measured top width is about 190.07 DIPs, shoulders 5.97 DIPs and lower quadratic extent 12.16 DIPs. The 190/6/12 tokens reproduce those observed proportions more closely than the previous 185/6/14 fallback. These are image measurements, not published hardware CAD dimensions. The surface draws pure black without a border, seam gap or idle shadow. Its animated `Expansion` controls the corners independently of height, so a HUD or custom tall notch still has the closed shoulders.

Compact activities live in equal side wings around the original black camera space. The left wing holds artwork or a timer/upload icon. The right holds the spectrum, countdown or upload state; progress stays in that wing. Wings grow together when two activities need more room, leaving the camera region centered. Header controls also respect that camera region. Settings retain custom dimensions; new profiles use the measured 190 × 32 proportions.

The 90-DIP expanded cover follows the source's declared sizing token. Boring Notch's current SwiftUI view can also grow artwork with available space, so screenshots of configurations with calendar or camera panes can differ. Winisland preserves its Windows volume, file-upload and focus-timer features in the shallow panel. It does not emulate unsupported Mac services.

## Typography, color and motion

Inter 4.1 is bundled under SIL OFL 1.1; text uses real Regular, Medium, SemiBold and Light faces, with Inter Display Light for the large timer. The title and artist follow Boring Notch's 13-point macOS headline hierarchy. Small timestamps and changing countdowns use tabular figures. SF Pro's downloadable font license excludes embedding it in this Windows app, so an exact Apple-font match is not asserted. Icons are original vector drawings with consistent round strokes, independent of Windows font versions.

The island stays `#000000`. Primary text is white, secondary labels use white at 60% opacity, and controls use shared neutral fills. Current Apple dark accent swatches guide focus orange (`#FF9230`), activity cyan (`#3CD3FE`) and keyboard-focus blue (`#0091FF`). Neutral fills are Windows adaptations because Apple's semantic materials change with background, appearance and accessibility settings. The artist and compact spectrogram use the artwork's average color, brightened to keep small text at least 4.5:1 against black. Playing artwork retains the earlier rotated, blurred glow and scales from 0.85 when paused to 1 when playing. Empty artwork uses a quiet neutral well. Settings use neutral macOS-like surfaces, blue controls, standard traffic-light colors, and a purple-blue preview.

Original vector symbols have consistent rounded strokes and optical proportions. Control Center uses the current two-toggle menu-bar symbol, individual network capsules, white active circles with blue glyphs, neutral inactive shortcuts and rounded control wells. Wide battery and skip symbols keep their natural proportions. Battery fill reflects the actual percentage; speaker wave layers follow the actual sound level. Playback buttons keep full transparent click regions around their smaller visible hover capsules. A 10-DIP minimum applies to small status labels, with 13-DIP body text, real bundled Inter weights, tabular times and grayscale rendering on the transparent WPF window.

Hover starts opening immediately, including reentry during a close. Pointer exit starts closing immediately, while pinning, editing, dialogs and active drags preserve the open panel. Apple's WWDC spring guidance recommends a versatile zero-bounce spring and continuity of both position and velocity on retargeting. Winisland now uses critical damping in both directions, response 0.42/0.45 and a maximum 600 ms settling clock; interrupted transitions inherit presented width, height and contour velocity and finish at an exact zero-velocity target. Content stays at native scale, with a 4-DIP translation over 280 ms and a 220 ms fade after 60 ms on first entry. Pinning an already-open panel does not replay the entrance. These timings are application tuning choices; Apple publishes no macOS notch-expansion timing. Reduced motion and changed Windows animation preferences finish active transitions immediately and disable button, rail and spectrum motion.

## Evidence and verification

- [Control Center](CONTROL-CENTER.md): grouped quick settings, native device behavior, unavailable states and interaction checks.
- [Geometry research](research/notch-geometry.md): exact source measurements, control points, hardware/API distinctions and DPI mapping.
- [Visual research](research/macos-visuals.md): Apple typography and color guidance, Boring Notch controls, distributable fonts and licenses.
- [Interaction research](research/notch-interaction.md): source layout, compact wings, artwork treatment and measured WPF spring behavior.
- [Apple housing measurements](apple-notch-geometry-research.md): official frontal product image, mirrored boundary fit, crop and overlay.
- [Current typography and icons](apple-type-icons-research.md): Apple HIG, current accent swatches, variable symbol levels and original vector decisions.
- [Apple motion](apple-motion-research.md): HIG and WWDC continuity guidance, documented adaptation timings.

The native self-test renders actual WPF controls and checks the top connection, curved cutaways, 100%, 125%, 150% and 200% raster scales, settled dimensions, compact activity combinations at size limits, centered opaque camera space, and bundled font resolution. Colored desktop composites and a contact sheet preserve the source raster's logical width and height; the previous fixed-height image fitting distorted the panel vertically. Existing checks still exercise Windows audio and media, pointer scrubbing, shelf persistence/uploads, keyboard activation, interrupted transitions, reduced motion and fullscreen behavior.

Final x64 verification passed 662 recorded results, including 654 assertions, in both the portable and self-contained installer payload suites. The targeted Spotify suite passed 94 assertions. An independent SHA-256 audit confirms all 101 current source, script and installer files match the verified snapshot. [Final delivery evidence](../artifacts/apple-design-final/evidence.json) records each report path and hash, the source manifest and the delivered installer checksum.

The [motion trace](../artifacts/verification/apple-motion-runtime.json) contains 79 sampled states across 65 distinct render times, with Windows animations enabled and sampling on a private desktop. Reversal boundaries preserve presented dimensions, contour, content opacity and translation; independent numerical checks confirm velocity continuity. Native-size content, already-open pinning and immediate reduced-motion cancellation also passed. The [rendered states](../artifacts/verification/fidelity-contact-sheet.png), [Control Center](../artifacts/verification/control-center.png) and [official-image comparison](../artifacts/apple-design-reference/geometry-comparison.png) were inspected by root and the final visual/motion auditors.

The [delivered installer](../dist/installer/Winisland-Setup-0.3.0-win-x64.exe) matches the verified snapshot's checksum. Its previous distribution artifact is preserved under the final evidence directory. The user's running application was left untouched. Full install, upgrade and uninstall checks require a disposable Windows VM and were not run on this desktop.

Apple's notch is physical camera housing and its geometry is measured per display. The expanding interface is a third-party reference. Physical pixel extents depend on display scaling, and WPF's rasterizer and Inter's metrics differ from macOS. Google Custom Search returned an access error; research continued through directly accessible official sources.
