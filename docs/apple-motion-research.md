# Apple motion and desktop interaction research

Researched 9 November 2026. This applies Apple's published motion guidance to
Winisland's interactive overlay. Apple's MacBook camera housing is a fixed part
of the display. Apple does not publish a macOS notch expansion animation or its
timing, so the overlay transitions below are explicitly Winisland adaptations.

## Sources and retrieval

Google Custom Search MCP was queried for Apple's HIG motion guidance and WWDC
spring animation guidance before edits. It returned HTTP 403 `PERMISSION_DENIED`
because the configured project lacks Custom Search JSON API access. Background
HTTP requests to Google Search returned a consent page or a JavaScript redirect;
these were not treated as search results. Official Apple documents were then
retrieved directly, without opening a browser or touching the interactive desktop.

- [Motion — Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/motion).
  The official DocC content is available at
  `https://developer.apple.com/tutorials/data/design/human-interface-guidelines/motion.json`.
  It says to add motion purposefully, make it optional, aim for brevity and
  precision, allow cancellation, and avoid unnecessary motion for frequent
  interactions. It also describes more subdued effects for trackpad input than
  direct touch input.
- [Animate with springs — WWDC23](https://developer.apple.com/videos/play/wwdc2023/10158/).
  Apple's transcript explains why springs preserve both position and velocity,
  including when interrupted and retargeted. A spring need not bounce. Apple
  recommends zero bounce when uncertain, calling it the most versatile general
  purpose spring. Spring response/perceptual duration is different from settling
  duration; an application should not block interaction until a spring settles.

## Implementation decisions

| Detail | Implemented behavior | Evidence and interpretation |
| --- | --- | --- |
| Surface spring | Critically damped, no intentional bounce | Apple's versatile zero-bounce recommendation; restrained desktop feedback |
| Retargeting | Read the previous WPF animation clock's presented time and carry width, height, and contour velocities into the new spring | Apple's position-and-velocity continuity discussion |
| Same-frame reversals | Pending clocks expose the previous presented dimensions rather than a future destination | Prevent a visible jump when an interaction reverses before the next render |
| Finite landing | A Hermite tail correction preserves initial velocity and ends at the exact target with zero velocity | WPF needs a finite timeline; follows Apple's gradual resting motion without pixel snapping |
| Content entrance | Text stays at native scale; 4-DIP vertical movement over 280 ms and a 220 ms fade after a 60 ms delay | Subdued desktop feedback; these values are adaptation choices, not published Apple constants |
| Content exit | 3-DIP upward movement and 120 ms fade | Brief, reversible dismissal toward its top-edge origin |
| Already-open panel | Pinning or refocusing does not replay the content entrance | Avoid redundant motion for frequent interactions |
| Reduced motion | Instant final surface/content state; no button, rail, compact-fade, or drop-highlight transition; also respond to a changed Windows animation preference | Optional motion and accessibility preferences |
| Controls during transition | Remain usable while opening; closing content yields input immediately; new targets interrupt existing clocks | Apple's cancellation and responsiveness guidance |

The surface retains the existing 600 ms maximum settling window and 0.42/0.45 s
response values. Those are tuned application values, not claims about an Apple
notch's undisclosed animation. Width, height, and contour share the same temporal
response while inheriting their own velocities.

## Verification

Use `scripts/Build.ps1 -Verify` for the integrated candidate. Existing private
desktop smoke checks cover prompt hover opening/closing, bounded surface motion,
settled dimensions, reopening during collapse, pinned behavior, and reduced
motion. Directly inspect the captured frames and results before claiming fidelity.
No normal Winisland instance is launched or restarted for this work.
