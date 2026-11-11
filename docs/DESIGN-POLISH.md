# Notch and settings design refinement

This pass refines the native WPF interface across the notch, settings, Apple-device connection moments, typography and interaction feedback. It preserves the measured black camera housing and its display-edge placement.

## References

Google Custom Search was attempted before work on 11 November 2026. The configured project returned HTTP 403, so no Google search results are claimed. Official sources were retrieved directly in background requests:

- [MacBook camera housing and unobscured app controls](https://support.apple.com/en-us/102125).
- [Apple motion guidance](https://developer.apple.com/design/human-interface-guidelines/motion): purposeful, brief, cancellable motion; restrained pointer effects; reduced-motion support.
- [Apple typography guidance](https://developer.apple.com/design/human-interface-guidelines/typography): optical text/display designs and legible text hierarchy.
- [Apple buttons guidance](https://developer.apple.com/design/human-interface-guidelines/buttons): clear actions, usable surrounding hit regions, press states and explanatory tooltips.
- [Apple settings guidance](https://developer.apple.com/design/human-interface-guidelines/settings) and [System Settings](https://support.apple.com/guide/mac-help/change-system-settings-mh15217/mac): predictable sidebar selection, grouped preferences and system preference respect.
- [AirPods automatic switching](https://support.apple.com/en-us/104988) and [AirPods Pro imagery](https://www.apple.com/airpods-pro/): distinguish confirmed device status from connection actions and use recognizable model drawings.

The HIG structured content was read at `https://developer.apple.com/tutorials/data/design/human-interface-guidelines/{motion,typography,buttons,settings}.json`. Existing [geometry measurements](apple-notch-geometry-research.md), [font and icon provenance](apple-type-icons-research.md) and [surface motion research](apple-motion-research.md) remain applicable.

Apple's notch is physical housing. The expandable music, shelf and connection UI are Winisland features; Apple does not publish an equivalent macOS notch panel to copy. Bundled Inter and Inter Display remain the distributable Windows approximation of Apple's text and display typography. San Francisco is not embedded.

## Changes and verification scope

| Area | Refined behavior | Required evidence |
| --- | --- | --- |
| Notch | Original fitted 190 by 32 silhouette and central camera space; header tabs accept input across the complete existing slot | Real WPF contour checks at 100–200% raster scale, header hit tests and camera-space checks |
| Idle music | Explanatory text replaces disabled playback controls; focus and shelf actions route directly to real panels; media arrival restores playback | No-session/session transition and control-local action checks, idle render |
| Settings | Persistent blue sidebar selection, grouped rows, native-size type on smaller windows, clear input/focus/disabled states, per-page scroll restoration and inline save feedback | Selection/font/scroll/persistence checks; all six pages at normal and 800 by 600 sizes |
| AirPods and Apple devices | More room for personalized names, confirmed connection status, one status indicator, full-name hover help and native-scale text | Model renders, long-name bounds/help, USB status, rapid interruption and reduced-motion checks |
| Motion and fonts | Stable hover labels, restrained press feedback for pointer and keyboard, interrupted transitions and completed clocks cleaned up; real semibold display headings and light tabular timer numerals | Existing private control and surface motion fixtures, keyboard/cancellation checks and actual published glyph family/weight resolution |

All UI checks run through `scripts/Build.ps1 -Verify` or the guarded existing-candidate verifier on a private Windows desktop. They use fixture data and control-local input. They do not launch the user's app, alter personal data or drive the interactive desktop.

Final integrated `Build.ps1 -Verify` passed **824 recorded results / 816 assertions** on the private Windows desktop. All 126 source, asset, script and license inputs match the tested snapshot. [The evidence record](../artifacts/design-polish/evidence.json) contains the exact source manifest, report and candidate hashes, desktop identity and reviewed screenshot hashes.

The runtime checks prove actual semibold display glyph selection, light tabular timer figures, full footer control bounds and pointer hit regions, six settings pages at 800 by 600, persistent sidebar selection, per-page scroll, saved preference readback, idle actions, camera geometry at 100–200% raster scales, native-scale device transitions and released control/device animation clocks. The combined suite also verifies detached bounded artwork, notification fixtures and physical audio/mixer preservation.

Normal and small-window settings, idle/music/focus notch states, personalized AirPods names and 100%/200% device images were visually reviewed. The review caught and corrected the clipped footer and missing semibold display face before the final gate. Screenshot output alone is not a passing assertion or proof of exact macOS rasterization. The user's running instance was left untouched; the verified portable candidate is under `artifacts/design-polish/run6-candidate/`.
