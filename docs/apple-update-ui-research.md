# Software update presentation

Researched 12 November 2026 for the native update tab. Two Google searches ran in a headless browser with automation enabled and a dedicated fixture profile. Google returned its unusual-traffic challenge; no results were available and no challenge was bypassed. The design decisions below use freshly downloaded official Apple documentation and screenshots. Research files are retained in the ignored `artifacts/update-redesign/research/` directory.

## References and observations

- [Apple: Update macOS on Mac](https://support.apple.com/en-us/108382). Its current [available-update screenshot](https://cdsassets.apple.com/live/7WUAS350/images/macos/tahoe/macos-tahoe-26-system-settings-general-software-update-update-now.png) places the product/version and download size together, distinguishes the install action, separates the release explanation, and identifies the installed version. The [up-to-date screenshot](https://cdsassets.apple.com/live/7WUAS350/images/macos/tahoe/macos-tahoe-26-system-settings-general-software-update-check-for-update-mac-is-up-to-date.png) uses a compact positive status with a recheck action.
- [Live Activities HIG](https://developer.apple.com/design/human-interface-guidelines/live-activities), retrieved from [official DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/live-activities.json). Apple recommends information that is legible at a glance, medium or heavier weights for key information, consistent margins, efficient use of space, and changing the height when the amount of information changes. Its [expanded Dynamic Island diagram](https://developer.apple.com/tutorials/images/com.apple.HIG/type-expanded@2x.png) shows black fill and content inset from the continuous outer corners. The red camera area in the explanatory diagram is not a software decoration.
- [Typography HIG](https://developer.apple.com/design/human-interface-guidelines/typography). Use readable sizes and distinguish information through size, weight, and color. Avoid thin weights for small text.
- [Buttons HIG](https://developer.apple.com/design/human-interface-guidelines/buttons). Emphasize the most likely action through its style, keep labels specific, provide press feedback, and give actions enough space. The redesign uses 44-DIP action targets.
- [Progress indicators HIG](https://developer.apple.com/design/human-interface-guidelines/progress-indicators). Show truthful operation feedback, use determinate progress when the work is measurable, and remove the progress indicator when the operation ends. Download percentages come from the existing byte-progress backend; checking and installing use explicit status text.
- [Official MacBook Air frontal image](https://www.apple.com/v/macbook-air/z/images/overview/macos/macos_hero__et3dsrq64iwm_large_2x.jpg). Its black camera housing is centered and attached to the display edge, with inward upper shoulders and curved lower corners. The existing measured geometry remains authoritative; see [MacBook geometry research](apple-notch-geometry-research.md) and [Dynamic Island geometry research](apple-dynamic-island-geometry-research.md).

The software-update screenshot's SHA-256 is `28aa549ec3374cbbaaa4142882cb16fa0f26958e7525876ed130306d0f353063`. The up-to-date screenshot's SHA-256 is `edccfe3735ed408693fc1ed2269af297e71df7f9d260a22b9335dae774b597ea`. These are research references, not application assets.

## Design contract

The surface is Winisland's expanded Software update tab. A person should quickly understand which version is offered, read what changed, and choose whether to install and restart. The primary action must remain visible during reading and downloading.

| Constraint | Implementation and review target |
| --- | --- |
| First information | 18-DIP semibold version/title; 12-DIP download size and installed-version metadata |
| Icon | Existing 36-DIP vector glyph with no oversized decorative container |
| Changelog | 13-DIP selectable read-only native text; 20-DIP line height; 14-DIP section headings; hanging list markers; emphasis and code formatting |
| Hierarchy | Release header, readable notes region, then a separate fixed status/action footer |
| Primary action | One white 44-DIP pill, labeled “Update and restart”; progress-specific labels during work |
| Secondary actions | Quiet recheck, Cancel during downloading, and an explicit full-release link |
| Content boundaries | Notes scroll inside their region. Status wraps. Titles and buttons stay inset from the housing's rounded corners. |
| Scrolling | Thin dark scrollbar scoped to the notes, with native wheel/page/drag behavior; full-release link stays in the header to preserve reading space |
| Available-update target | MacBook: 640 × 376 DIPs; Dynamic Island desktop feature panel: 640 × 392 DIPs, including the existing 16-DIP feature inset |
| Short-state target | MacBook: 640 × 236 DIPs; Dynamic Island feature panel: 640 × 252 DIPs |
| Raster review | Both appearances at 100%, 125%, 150%, and 200%; inspect actual native captures against the references |
| Accessibility | Readable contrast, visible keyboard focus, native text selection and Tab traversal, meaningful control names, polite live status, reduced-motion support |

The wider, taller desktop release reader is an application adaptation. Apple's iPhone expanded Live Activity dimensions remain 371 × 84–160 points for the existing reference device; this desktop update page is not presented as a hardware-exact iPhone island. The measured closed MacBook and iPhone silhouettes, camera reservation, top attachment/gap, existing music dimensions, and pure black material retain their existing behavior.

## Content and interaction states

| State and trigger | Visible feedback and available action |
| --- | --- |
| Ready / opening | Installed version and automatic-check explanation; Check for updates |
| Checking | Explicit checking status and disabled recheck; no invented percentage |
| Update available | Offered version, verified asset size, installed version, formatted notes; Update and restart |
| No release / up to date | Smaller positive status with Check for updates; no install offer |
| Missing notes | Clear “No release notes were provided” text; available update and full-release link remain usable |
| Bounded notes preview | Explicit preview label and full-release link; truncation never silently implies the complete notes are shown |
| Downloading | Actual byte percentage, progress bar, Cancel; duplicate installs disabled; notes remain readable without resetting their scroll |
| Verifying / installing | Explicit phase text; cancel is available until installer handoff begins |
| Canceled | Readable canceled outcome; update retry available |
| Error | Wrapping error and supported retry. Browser-open failures are inline and preserve later status updates. |
| Installer handed off | “Restarting…” feedback; further check/install attempts are disabled |
| Private beta | Smaller private-beta explanation, no public update offer, disabled check/install; embedded private-build behavior remains enforced |
| Permission | No new permission state: the existing per-user installer and user-initiated release links retain their established access model. Automated runs never invoke a real installer or external browser. |

`ReleaseNotesView` constructs WPF document objects instead of interpreting HTML or XAML. It supports ATX/setext headings, paragraphs, bullet and ordered lists with bounded nesting, emphasis, inline/fenced code, and HTTPS links. Remote images never load. HTML and uncommon Markdown constructs are not a full CommonMark implementation; the complete original release remains accessible on GitHub. Existing GitHub metadata and installer checks retain their size/checksum/address validation, and release bodies remain bounded to 6,000 characters with explicit preview feedback.

## Verification

The existing `--self-test` / `--updates-only` entry point supplies private-desktop isolation. Update fixtures exercise actual native buttons and links using local HTTP/installer/browser callbacks, so tests cannot replace the user's running application or open their browser. They cover loading, errors/retry, up-to-date and both private appearances; rich, long, missing and truncated notes; scroll/focus/layout; and actual download/cancel/verified installer handoff.

The required build is `scripts/Build.ps1 -PrivateBuild -Verify`, published into a separate ignored candidate directory. Runtime JSON and PNGs under the verification output's `update-ui/` directory, the full `results.json`, final source hashes, and the frontend-design-direction evidence record provide the completion evidence. Rendered raster-scale checks do not claim a physical test on four different monitors. Implementation and source review alone do not establish a passing runtime result.
