# macOS and Boring Notch visual research

Historical source evidence. See the [current Apple typography, symbols and color audit](../apple-type-icons-research.md) and [current design specification](../NOTCH-DESIGN.md) for the updated palette and control presentation.

Researched November 7, 2026. The user selected **Boring Notch** as the expanding-panel reference. This note distinguishes documented Apple behavior, observed Boring Notch implementation, and the choices needed to reproduce that appearance in Windows WPF.

## Typography

Apple's [Typography HIG](https://developer.apple.com/design/human-interface-guidelines/typography) documents **SF Pro as the macOS system font**, a **13 pt recommended default size**, and a **10 pt recommended minimum**. It recommends Regular, Medium, Semibold, or Bold for legibility, avoiding thin and light weights at small sizes. Apple's [fonts page](https://developer.apple.com/fonts/) describes SF Pro as a variable font with optical sizes; current guidance does not require a rigid Text-below-20 / Display-above-20 switch. The system adjusts tracking and optical details according to size. Numbers are proportional by default; tabular figures are useful for the timer and playback timestamps.

The license published on Apple's fonts page restricts the downloaded font to Apple-platform interface mockups and prohibits embedding it in software programs. Winisland therefore includes **Inter 4.1**, an independent UI typeface released under the SIL OFL 1.1. Inter's [official site](https://rsms.me/inter/) describes its interface focus, Text and Display optical designs, and tabular-number support. Choosing it as the closest available distributable fit is a visual judgment, not evidence that its metrics equal SF Pro. Its static TrueType faces give WPF dependable real Regular, Medium, SemiBold, and Light weights without requiring a local font installation or variable-font support.

| Use | Evidence or recommended Windows treatment |
| --- | --- |
| Track title | Boring Notch requests native `.headline`, white; Inter 13 DIP SemiBold is a practical macOS-density approximation. |
| Artist | Boring Notch requests the same `.headline` size with Medium weight; gray or an artwork-derived tint. |
| Body and buttons | Inter 13 DIP Regular or Medium follows Apple's default-size guidance. |
| Compact status | Inter 11-12 DIP Medium, with tabular figures for changing times. |
| Elapsed and duration | Boring Notch uses caption-scale timestamps; 10-11 DIP Medium provides a similar hierarchy without going below Apple's recommended minimum. |
| Large timer | Inter Display Light at 44-48 DIP is a chosen display treatment; Apple's small-text light-weight caution does not support using Light for body labels. |

macOS points and Windows DIPs are logical layout units; physical pixel counts depend on each display's scale. Matching logical sizes is the relevant baseline, not treating 13 pt as a guaranteed 13 physical pixels.

## Black surface, colors, and personality

Boring Notch's island is opaque black. Keep the hardware-like silhouette **#000000**, including its top connection. Its text and controls use white and gray rather than tinting the entire panel. Apple's [Color HIG](https://developer.apple.com/design/human-interface-guidelines/color) says that native semantic colors vary with OS release, appearance, vibrancy, and accessibility; the actual macOS accent is the color people choose in System Settings. Fixed grays such as `#1C1C1E` or `#8E8E93`, often quoted from iOS design palettes, are not universal exact macOS semantic values.

For a standalone black Windows overlay, primary white, secondary gray, a gray track, and a quiet gray selected/hover capsule are intentional approximations. They should not be described as exact macOS color exports. The full-black silhouette and relative hierarchy are better-supported targets than one claimed universal macOS gray hex.

Boring Notch has concrete optional effects that make the panel livelier while respecting its black housing:

- Its artist tint samples the artwork's average color and enforces a minimum brightness factor of 0.6.
- With its lighting option enabled, a duplicate album-art image sits behind the cover, scaled to 1.3 by 1.4, rotated 92 degrees, blurred by 40 logical points, and shown at 0.5 opacity while music plays.
- Album art scales to 0.85 while paused and back to 1 while playing. In the reference implementation, this responds to playback state rather than a random decorative pulse.
- Its selected header tab uses a capsule filled with the native `secondarySystemFill`. Header tab content is 26 points high. Media hover buttons use a gray capsule at 0.2 opacity with a 0.3-second smooth transition.

These effects are reference evidence, not a requirement to reproduce every optional setting or Apple-only integration.

## Icons and controls

Apple's [SF Symbols overview](https://developer.apple.com/sf-symbols/) describes symbols that align with text and have matching weights and scales. The Typography HIG also describes matching font and symbol weights. Boring Notch uses SF-symbol music, tray, settings, battery, and playback imagery. Winisland can approximate this coherence with independently drawn vectors and matching rounded stroke weights, instead of mixing unrelated Windows Fluent glyph shapes into the notch.

A 16-DIP outline for small toolbar icons and filled 14-18-DIP playback marks is a chosen Windows approximation. Rounded line caps, consistent apparent weight, and clean centered optical placement matter more than claiming an undocumented exact SF Symbols stroke width. Keep accessible button hit areas larger than the visible glyph or capsule when needed.

The Boring Notch source uses 30-point previous/next hover capsules, a 40-point play capsule, and toolbar spacing of 6 points. Its selected tab capsule is quiet; text and glyphs carry the contrast. Windows keyboard focus, accessible control names, and reduced-motion behavior must remain available even though they are not visible in a static reference screenshot.

## Font assets and integration evidence

Five unmodified TTF files and the complete license are under `src/Winisland/Assets/Fonts/`. [Their provenance and SHA-256 hashes](../../src/Winisland/Assets/Fonts/README.md) document the official source archive and each included face. WPF `GlyphTypeface` inspection confirmed family `Inter` with Normal, Medium, SemiBold, and Light, and family `Inter Display` with Light. The font files total 2,074,692 bytes.

Embed the TTFs as WPF `Resource` items and copy the complete OFL license into published output. Use `pack://application:,,,/Assets/Fonts/#Inter` for text and `pack://application:,,,/Assets/Fonts/#Inter Display` for the large timer. These family strings are confirmed from the files; packaged runtime rendering still needs verification in the actual application build.

## Sources and limits

Primary Apple pages were fetched directly, including the structured HIG content at `developer.apple.com/tutorials/data/design/human-interface-guidelines/typography.json` and `color.json`. Google Custom Search was unavailable with HTTP 403 in this session, so no search snippets were used as evidence. Boring Notch source was inspected from the directly downloaded upstream tree. The comparison pins URLs to commit `b7611bbf32c627a3427408fc142486900d71a23f`:

1. [Apple Typography HIG](https://developer.apple.com/design/human-interface-guidelines/typography) - platform font, default and minimum size, weights, optical sizing, and hierarchy.
2. [Apple Fonts](https://developer.apple.com/fonts/) - SF Pro description and the downloadable-font license.
3. [Apple Color HIG](https://developer.apple.com/design/human-interface-guidelines/color) - semantic and dynamic color behavior, artwork context, accent and contrast.
4. [Apple SF Symbols](https://developer.apple.com/sf-symbols/) - symbol/text weight and scale consistency.
5. [Boring Notch player](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Notch/NotchHomeView.swift) - title/artist styles, artwork lighting, and paused-cover scaling.
6. [Boring Notch header selection](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Tabs/TabSelectionView.swift) - 26-point tabs and native selected capsule.
7. [Boring Notch media hover buttons](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/HoverButton.swift) - 30/40-point capsules and hover treatment.
8. [Inter official site](https://rsms.me/inter/) and [Inter 4.1 release](https://github.com/rsms/inter/releases/tag/v4.1) - distributable alternative, version and provenance.

The MacBook camera notch itself has no typography or media controls; these expanding panels belong to the selected third-party reference. No SF Pro font files or SF Symbols artwork were downloaded or included.
