# UI/UX audit — 12 November 2026

Four agents reviewed the complete native WPF interface, with separate bug and optimization teams reviewing the supporting services, browser companion, startup and installer. File ownership and exact changes were coordinated in the local shared agent log. The established C#/WPF stack, Apple camera safe area and background verification rules were retained.

## Confirmed defects and fixes

| Area | Trigger and corrected behavior | Verification |
| --- | --- | --- |
| Calendar keyboard actions | Enter on the focused Cancel button previously saved an event. Buttons now retain their native Enter/Space actions; text-field Enter still saves and notes retain multiline input. | Actual focused controls and routed keyboard events in OrganizerVerification. |
| Reminder and world-clock save failures | A refused disk write left a checkbox or city selection showing an unsaved value. Both now restore the confirmed value and allow retry. | Isolated refused writes, native control/binding/store/disk assertions, one error notification per failure and successful retry. |
| Calendar readability | Neighboring-month dates, selected dates and unchecked reminder outlines had insufficient contrast. Scoped muted and selected-red colors now meet the applicable text/control contrast targets. | Independent standard sRGB luminance calculation and existing rendered calendar review. |
| Settings on smaller displays | Fixed navigation and horizontal action rows overlapped or clipped when the window shrank to the logical work area. Navigation scrolls; actions and constrained labels wrap. | Actual containment, hit targets, focus and Tab traversal at 640×480, 820×600 and 950×740. |
| Notification/accessory keyboard access | Inner Tab cycles prevented reaching sibling footer actions from the main controls. The island now has one outer cycle. Dismissing a focused footer restores focus to the next queued action or the main controls; unfocused dismissal and activation callbacks retain their focus. | Native forward/reverse traversal from player/header/chooser, wrap, actual dismissal, queued payloads, no-stealing and callback assertions. |
| Hidden accessory timeout | The chooser hid an accessory while consuming its reading interval. The timer pauses while neither card nor header is visible and resumes when presented. | Real hidden interval followed by Back restoration and real visible expiry. |
| Pinned overlay recovery | Display/session recovery reapplied a nonactivating window style to a pinned notch. Recovery now restores the pinned activation policy. | Actual private-desktop HWND flags for pinned/collapsed cases with foreground preservation. |
| Shelf path handling | A noncanonical path could be added but fail the subsequent upload selection. Add and upload selection now use the same canonical paths. | Actual AddAndUpload route, deduplication and disconnected-upload waiting state without network requests. |
| Restored share-only shelf references | Direct-link copy remained enabled without a valid direct URL. Capability, binding and handler now require a valid HTTPS direct URL; share actions remain available. | Real native menu binding for missing, present and invalid direct URLs; guarded unavailable copy without accessing the clipboard. |

## Review scope

The UI team read all notch window partials, both appearance players/menus, notification banner, settings/tray/update views, organizer and Control Center panels, shared styles/geometry, sliders, drawing and motion controls. The primary agent separately reviewed shelf handlers, timer editing, associated persistence/upload behavior, native recovery and existing verification entry points. An independent coordinated coverage audit reconciled nine review manifests and reread remaining surfaces: all 101 production/browser/script/build-configuration files have complete-read and matching-source evidence, including 68 production C# and 9 XAML files. Supporting service/browser/installer findings are coordinated with the separate project bug audit; optimization proposals are documented separately and are not presented as confirmed UI defects.

Local review manifests and reports under `artifacts/ui-ux-audit/` preserve inspected sources, reasoning, contrast calculations, failures and rendered evidence. The audit does not claim that a finite review proves the absence of every possible future bug.

## Design references

Background Google queries were attempted, but returned the consent page; no general search MCP was available. Official [MacBook Pro](https://www.apple.com/macbook-pro/), [camera-housing guidance](https://support.apple.com/en-us/102125) and [Apple accessibility guidance](https://developer.apple.com/design/human-interface-guidelines/accessibility) were fetched. Apple recommends accessible keyboard operation and WCAG AA contrast. The changes preserve camera clearance, the native housing, established typography and motion. React/shadcn components are incompatible with this native WPF application.

## Verification status

The corrected immutable candidate passed `scripts/Build.ps1 -PrivateBuild -Verify` on the mandatory private Windows desktop: **1,507 assertions, zero failures**, plus eight informational records. The actual report confirms embedded private-build updater metadata `true`. The first candidate's failed focused-dismissal assertion is preserved; it drove the final focus fix and was not weakened.

The exact report, candidate, 144 copied build-input hashes, auxiliary SDK/solution inputs, complete-read coverage manifest and native PNGs are preserved under `artifacts/ui-ux-audit/gate2/` and the coordinated bug-audit artifacts. `verified-ui-inputs.json` confirms all 17 changed UI/fixture inputs match both the passing candidate and the current worktree; `requirement-evidence.json` maps 17 requirement groups to actual passing checks. Calendar raster pixels confirm the darker selection and readable neighboring dates. Native settings/action/focus renders were inspected; actual containment and Tab checks cover every action at all three sizes. Static XAML and whitespace checks passed.

The subsequent update-cadence team's complete 146-input integration also passed **1,507 assertions with zero failures** and embedded private-build metadata `true`; all 17 UI inputs match that candidate too. Its report is `artifacts/updater-cadence/source/artifacts/verification/results.json`. Later measured-performance harness additions and mapping changes require that separate task's own verification; they are not relabeled as tested by these earlier frozen gates.

No normal application, personal browser, file picker, global input or interactive desktop screenshot was used. The running user instance was preserved. This task does not deploy, restart, commit, push or publish a release.

Physical multi-monitor/DPI changes, live accessory reconnection, personal Spotify/browser accounts and installer execution require their appropriate hardware or disposable-VM checks; isolated fixtures do not claim those outcomes.
