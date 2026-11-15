# Apple interaction and motion refinement

Researched 15 November 2026. The native WPF interface already has a fitted
MacBook camera-housing contour and interrupted surface springs. This pass
extends the same interaction quality to activity changes and button feedback.

## References

Google queries for Apple's motion and WWDC spring guidance were attempted using
background HTTP requests. They returned a consent page; no search results are
claimed. No general web-search tool was exposed. Official sources were read
directly without opening an interactive browser:

- [Motion, Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/motion),
  including its structured DocC content: brief, purposeful, cancellable motion;
  optional animation; subdued pointer feedback; consistent entry/exit direction.
- [Animate with springs, WWDC23](https://developer.apple.com/videos/play/wwdc2023/10158/):
  continuous position and velocity when retargeting, gradual resting motion and
  springs without bounce.
- [MacBook camera-housing guidance](https://support.apple.com/en-us/102125):
  keep app controls clear of the physical camera area.

Apple does not publish a macOS expanding-notch interface or its animation
constants. These transitions are Winisland adaptations of Apple's published
principles. The existing measured notch geometry and native-scale text remain
the visual reference. React/shadcn components do not fit the native C#/WPF stack.

## Changes

| Interaction | Previously | Refined behavior |
| --- | --- | --- |
| Rapid activity changes | Independent tab and chooser clocks; a pending clock could reveal the destination before its first frame | One panel transition path retains presented opacity and translation, including same-dispatcher-turn reversals |
| Moving panel reversal | Translation restarted with a new easing curve | A spring without bounce inherits the rendered translation velocity |
| Menu on a pinned island | Re-entering expansion settled the chooser before its entrance | Already-open menus transition without replaying the window expansion |
| Returning to the current activity | Choosing the already-selected activity skipped the chooser dismissal transition | Current and different activity choices share the same smooth return |
| Departing controls | Mouse input stopped while keyboard focus could remain in fading content | Focus moves only when it belonged to departing content; fading controls immediately stop accepting keyboard actions |
| Completed activity changes | Incoming tab clocks remained attached after settling | Both opacity and translation clocks are released; only the selected activity remains visible |
| Button press and release | A new cubic easing restarted velocity and could expose the old base scale before rendering | Both scale axes retain presented position and spring velocity, then settle at native scale |
| Accessibility | Surface motion already supported reduced motion | Panel and button cancellation also settle immediately and release their clocks |

Panel entrances use a four-DIP translation over 180 ms; exits move three DIPs
toward the top over 90 ms. Button press/release retain the existing restrained
97% scale and 80/150 ms durations. These are application tuning choices, not
Apple measurements. Actions remain available during entrance. Unrelated header,
footer and external-app focus is preserved.

Activity and chooser routes also integrate the visibility agent's resource-demand
hooks so hidden music rendering stops when another activity takes over.

## Verification

The existing private-desktop self-test exercises actual WPF panels and controls.
New checks cover pending and rendered reversals, independent numerical velocity
measurements, focus handoff, retained header focus, chooser-to-activity changes,
reduced-motion cancellation and released clocks. Rendered panel PNGs are saved
with the report. `--self-test --motion-only` selects these diagnostics through
the same isolation guard; it does not replace the full verification gate.

Use `scripts/Build.ps1 -PrivateBuild -Verify` for a separate personal candidate,
and `scripts/Invoke-Verification.ps1 -Suite Motion` for focused diagnostics.
Exact-source reports and input hashes are retained locally under
`artifacts/experience-polish/`. The final integrated candidate passed **1,633
recorded results with zero failures**; its focused motion run passed **159
recorded results with zero failures**. The full report confirms private-build
updater metadata `true` and private-desktop isolation. All 203 captured input
files matched the current sources at the final gate. Verification documentation
was updated afterward; application and build inputs remained unchanged.

A diagnostic candidate with the original three motion implementation files and
the new fixtures failed the initial presented-opacity regression. Its report is
retained alongside the passing candidate. Native panel PNGs and the official
geometry comparison were inspected. The user's running app was preserved; this
pass does not deploy, restart, commit, push or publish a release.
