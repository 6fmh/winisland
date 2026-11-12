# Notification and accessory moments

Research retrieved on 12 November 2026. The two Google searches in
`artifacts/popup-redesign/research/google-report.json` reached Google's
unusual-traffic page, so no Google result is claimed. Official Apple guidance
and screenshots were fetched directly, without an interactive browser.

References:

- [Apple HIG: Notifications](https://developer.apple.com/design/human-interface-guidelines/notifications)
  specifies concise titles and messages, prominent leading app artwork, avoiding
  repeated alerts for the same event, and tapping the notification to open its
  related content without a redundant Open action button.
- [Apple HIG: Live Activities](https://developer.apple.com/design/human-interface-guidelines/live-activities)
  calls for opaque black Dynamic Island surfaces, stable relative placement,
  content that fits around the camera, and margins that follow the housing.
  Its margin and layout illustrations are saved locally for visual comparison.
- [Apple Mac User Guide: notifications](https://support.apple.com/guide/mac-help/get-notifications-mchl2fb1258f/mac)
  shows prominent leading artwork, strong title hierarchy, quieter secondary
  content and circular dismissal affordances. The saved reference is
  `artifacts/notifications/research/apple-macos-banner.png`.
- [MacBook Pro](https://www.apple.com/macbook-pro/) provides the display/camera
  housing reference, saved in `artifacts/popup-redesign/research/macbook.txt`.
- [AirPods](https://www.apple.com/airpods/) provides accessory appearance context.
  Product silhouettes remain original native vector assets.

## Checkable design contract

The surface is a short-lived moment inside the existing native WPF notch. Its
job is to let someone identify an event or accessory quickly, read it, and
dismiss or open supported notification content. Tone: quiet, compact, clear.
The recognizable detail is artwork at the leading edge of an uninterrupted
black housing. Existing MacBook and Dynamic Island contours and camera spaces
remain authoritative. WPF cannot consume shadcn's React components.

Notifications use a 44-DIP app image, 12-DIP source, 16-DIP semibold title and
13-DIP preview. Titles and previews wrap to at most two lines. Content occupies
the native measured height of the artwork and text, with a 44-DIP minimum;
the housing follows that size. Closed popups use identical camera-height-plus-8-DIP
insets above and below the content so the complete black surface is balanced.
Expanded footers use 16 DIPs above and below. Dismiss targets align vertically
with the content instead of the top edge of an oversized row.
The trailing circular close control has a 44-by-44-DIP hit target. An actual
activation callback enables keyboard opening; informational moments expose
only dismissal. The close control has an explicit circular blue keyboard focus
outline inside its template. Full content remains available through automation
and tooltips.

Accessories use an 80-by-64-DIP native product illustration, a 16-DIP name and
a 13-DIP, two-line confirmed status. Personalized names trim with full tooltips.
The header status uses bounded text with full hover/automation information.
Model labels come from the hardware identity service. Missing battery data
does not create a battery gauge; USB connections remain explicitly identified.

These sizes are desktop layout choices informed by the reference hierarchy,
not unpublished Apple constants. Windows fonts, original accessory vectors and
Winisland's notch placement mean the result is not a pixel-identical copy of
macOS Notification Center or Apple's proprietary connection sheet.

## Interaction and states

Arrivals do not acquire focus. Existing housing springs and native-size content
fades remain; reduced motion resolves them immediately. Hover or keyboard focus
pauses reading time. Updated duplicate IDs refresh payload and timeout without
adding another moment. Bursts keep their existing bounded queues, and stale
notification entries are discarded. Notifications suspend an obscured accessory
timer; dismissal restores the accessory. Explicit dismissal advances the queue.
Escape dismisses the active moment, preserving the existing panel behavior.

Success is the confirmed event/device and its supported open/dismiss action.
Partial data uses real artwork or the existing bell fallback, and generic device
identity when a model is unavailable. Empty preview collapses its row. Loading
and error are not separate states of this confirmed-event surface; discovery,
notification permission, and application errors retain their existing service
and settings flows. No discovery or permission request is invented here.

## Verification targets

Actual native renders at 100%, 125%, 150% and 200%, in both MacBook and Dynamic
Island appearance; configured notch widths 160, 190 and 320 DIPs; expanded
player, menu and Control Center footers; long title/message/device metadata;
title-only and missing-artwork content; native focus/Tab/open/dismiss; real
hover/focus timers; duplicate payload updates and notification/device interruption;
motion completion and reduced motion. All verification uses the registered
self-test on the mandatory private desktop and preserves JSON/PNG evidence.

## Verification evidence

The final isolated candidate was built with `Build.ps1 -PrivateBuild -Verify`.
Its `artifacts/popup-spacing/gate4-source/artifacts/verification/results.json`
passes all 1,193 assertions with zero failures, including the production updater
check for embedded private-build metadata `true`. All 140 build input hashes
match both the compiled snapshot and current source.

Native renders cover both appearances at 100%, 125%, 150% and 200%, long and
title-only notifications, personalized device names, other accessory types,
expanded layouts and keyboard focus. Actual raster checks require visible blue
pixels in the focused open and dismiss controls. Runtime checks cover supported
activation, Tab navigation, reading timers, latest queued payloads, interruption
recovery and reduced motion. Eight native spacing fixtures check equal visible
content gaps: 40/40 DIPs in the configured MacBook layout and 44.67/44.67 DIPs in
Dynamic Island mode, including short and long notifications and accessory artwork.
Source, title and preview bounds must each fit within the measured content row.
Refreshing the text and row measurements immediately prevents a previous
notification's cached height from clipping newly wrapped text. Fractional layout
dimensions use a 0.01-DIP tolerance with independent containment and event
identity assertions.

`artifacts/popup-spacing/design-evidence.json` passes the frontend design
direction skill's local evidence checker; `final-audit.json` preserves the source
inventory and candidate checksum. `popup-preview.png` compares the final
notification and AirPods layouts. Earlier failed gates remain available.

Scale evidence comes from native offscreen raster rendering; no physical
multi-monitor DPI changes or accessory reconnections are claimed. The verified
candidate is separate from the user's running app.
