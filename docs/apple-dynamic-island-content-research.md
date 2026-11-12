# Dynamic Island content and typography research

Research date: November 11, 2026. This document covers the optional iPhone-inspired appearance. The existing MacBook notch remains the default. Geometry and motion are documented in the companion Dynamic Island research documents.

## Sources and evidence

- [Apple Live Activities HIG](https://developer.apple.com/design/human-interface-guidelines/live-activities), including its [DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/live-activities.json), retrieved HTTP 200.
- [Apple Typography HIG](https://developer.apple.com/design/human-interface-guidelines/typography) and its [DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/typography.json), retrieved HTTP 200.
- [Apple's iPhone 14 Pro announcement](https://www.apple.com/newsroom/2022/09/apple-debuts-iphone-14-pro-and-iphone-14-pro-max/) and [official 1920×1080 three-phone video poster](https://www.apple.com/newsroom/videos/iphone-14-pro-3up/posters/Apple-iPhone-14-Pro-iPhone-14-Pro-Max-Dynamic-Island-demo-3up.jpg.large_2x.jpg).
- [The Verge's original iPhone 14 Pro review](https://www.theverge.com/23351948/apple-iphone-14-pro-review), including its [actual expanded media player photograph](https://platform.theverge.com/wp-content/uploads/sites/2/chorus/uploads/chorus_asset/file/24020026/226270_iPHONE_14_PHO_akrales_0399.jpg). The 2040×1360 source shows a podcast in the system media player, with album art leading, bold title and secondary artist, trailing spectrum, elapsed and remaining time inline with the rail, and larger centered transport. It establishes relative placement, not private pixel constants: the phone is photographed obliquely and the view includes podcast-specific skip icons.
- Official HIG illustrations: [legible text](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-text-correct-size@2x.png), [balanced compact content](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-balanced-content@2x.png), [expanded layout](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-layout-correct@2x.png), [margins](https://developer.apple.com/tutorials/images/com.apple.HIG/live-activities-margins@2x.png), [compact anatomy](https://developer.apple.com/tutorials/images/com.apple.HIG/type-compact@2x.png), and [expanded anatomy](https://developer.apple.com/tutorials/images/com.apple.HIG/type-expanded@2x.png). These are schematic guidance, not screenshots from which to infer all production pixel values.

Manual Google HTTP searches were attempted as requested; the default endpoint returned a Swedish consent interstitial. A separate request with nonpersonal consent cookies reached the search endpoint but received an enable-JavaScript challenge. A dedicated headless Comet process, with an isolated fixture profile and automation enabled, reached Google's unusual-traffic challenge; the process exited and was cleaned up. No usable Google results or challenge bypass are claimed. Direct official Apple documents/images and the independently captured Verge photograph supply the evidence. No interactive browser was launched or attached. Local source captures are in `artifacts/dynamic-island-research/content/` and are excluded from Git.

## Apple-specified properties

The Dynamic Island is **opaque black**, including compact, minimal, and expanded presentations. It does not use a frosted or translucent material. Color belongs to the live content; Apple recommends bold, recognizable color for objects and important information. A tinted key line can distinguish the housing on dark backgrounds.

Compact leading and trailing content belong together visually despite the TrueDepth camera separating them. Both sides use consistent typography and color, remain similarly sized, and fit snugly against the camera reserve. Apple explicitly advises against adding padding between content and the camera. Compact information must stay narrow and glanceable. An album cover or music mark on the left and spectrum on the right follows this structure.

Apple's normative iPhone dimensions are in logical points, not physical display pixels:

| Portrait screen | Each compact side | Minimal | Expanded |
| --- | --- | --- | --- |
| 393×852 | 52.33×36.67 | 36.67–45×36.67 | 371×84–160 |
| 430×932 | 62.33×36.67 | 36.67–45×36.67 | 408×84–160 |

Limited-width presentations must fit in a 28-point circle. The Lock Screen's standard 14-point margin is a different context and must not be presented as the Dynamic Island margin.

Expanded content should preserve the relative placement of compact elements. Rounded shapes need even, concentric margins; content does not reach the housing edge. The HIG illustrates a graphic near the leading corner, a hierarchy of status on the right, and an inset lower action. This informs the native player: album cover remains leading, the spectrum remains trailing, metadata fits between them, and the timeline and transport occupy separate lower rows.

## Typography and legal Windows adaptation

SF Pro is Apple's iOS and macOS system family. The system provides optical sizing and weight-matched SF Symbols. Apple says to obtain system fonts through platform APIs and **not embed system fonts in an app or game**. Winisland therefore keeps its independently designed, OFL-licensed bundled Inter 4.1 family and original vector controls; it does not redistribute SF Pro or SF Symbols. Font assets and license provenance remain documented in `src/Winisland/Assets/Fonts/README.md`.

The default iOS typography table supplies these references:

| Style | Size | Leading | Default weight |
| --- | ---: | ---: | --- |
| Headline | 17 pt | 22 pt | Semibold |
| Body | 17 pt | 22 pt | Regular |
| Callout | 16 pt | 21 pt | Regular |
| Subhead | 15 pt | 20 pt | Regular |
| Footnote | 13 pt | 18 pt | Regular |
| Caption 1 | 12 pt | 16 pt | Regular |
| Caption 2 | 11 pt | 13 pt | Regular |

The Live Activities guidance separately recommends **medium weight or heavier**, sparing use of small text, and larger important information. It does not publish Apple Music's private font/style constants. Winisland's music title and artist use a readable semibold/medium hierarchy; timeline labels use medium tabular numerals so ticks do not shift horizontally. Exact glyph identity remains an approximation on Windows.

## Implementation mapping

`Controls/DynamicIslandPlayer.xaml` is a separate 371×160 expanded music view within the shared black surface. It removes the MacBook header and feature tabs from the music presentation. It uses 48-point artwork with 8-point corners, 20-point side insets, an inline timeline with elapsed and remaining time flanking the thin rail, and larger white transport symbols. The timeline was adjusted to match the actual expanded media photograph. These content dimensions are visual desktop adaptations, not normative values published for Apple Music; the photo's perspective cannot establish all private sizes. The compact and expanded outer geometry is covered by `DynamicIslandMetrics` and the geometry research.

The quiet ellipsis control exposes Winisland's other functions. It is a desktop adaptation and does not imitate an AirPlay button whose action is unavailable on Windows. Album art is the actual media-session image, playback controls honor the session's capabilities, and the spectrum uses the existing real audio-band stream. Empty sessions show a restrained music-ready state. Pointer seek previews, release commits, wheel steps, keyboard steps, and canceled drags retain the existing `ScrubSlider` behavior. Reduced Motion stops decorative spectrum movement and control motion.

Visual verification must render the real WPF view on the private desktop at multiple DPI scales, inspect compact/expanded snapshots, confirm no clipping, and exercise the actual named playback/seek/menu controls. The shared log records the final verification report and source identity.
