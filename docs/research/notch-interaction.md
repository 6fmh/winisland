# Boring Notch interaction reference

Historical source evidence. The current closed silhouette follows [official Apple imagery](../apple-notch-geometry-research.md), and [Apple-guided motion](../apple-motion-research.md) supersedes the bouncing spring and content-scale adaptation described below.

Research and source inspection: 2026-11-07. Reference commit: `b7611bbf32c627a3427408fc142486900d71a23f`.

Apple's MacBook notch is a physical camera housing. The expanding player and shelf described here are features of **Boring Notch**, the reference selected by the user, rather than built-in macOS features.

## Confirmed source measurements

| Detail | Boring Notch source value |
| --- | --- |
| Expanded surface | 640 × 190 logical points |
| External-display fallback | 185-point width; height comes from preferences |
| Open contour | 19-point inward top shoulders, 24-point lower corners |
| Closed contour | 6-point inward top shoulders, 14-point lower corners |
| Main content inset | 19 + 12 = 31 points horizontally; 12 points at the bottom |
| Header height at a 32-point closed height | 32 points |
| Tab visible height | 26 points |
| Album-art sizing tokens | Open: 90 × 90, radius 13; closed: 20 × 20, radius 4 |
| Compact music extension at 32-point height | `2 × (32 - 12) + 20 = 60` extra points |
| Compact spectrogram mask | 16 × 12 inside a 20 × 20 box |
| Previous/next hover capsule | 30 × 30 points |
| Play/pause hover capsule | 40 × 40 points |
| Toolbar spacing | 6 points |
| Slider visible rail | 5 points at rest, 9 points while dragging |
| Music content entrance | Smooth animation with a 0.35-second duration |

These values come from [sizing/matters.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/sizing/matters.swift), [ContentView.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/ContentView.swift), [HoverButton.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/HoverButton.swift), [NotchHomeView.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Notch/NotchHomeView.swift), and [TabSelectionView.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Tabs/TabSelectionView.swift) at that commit. The album-art dimensions are declared design tokens; the latest `AlbumArtView` also responds to the available layout height, so they do not prove that every rendered state has an exactly 90-point image.

The compact layout deliberately leaves a black central region for the camera housing, with artwork on the left and the visualizer on the right. The real Mac notch width is calculated from `NSScreen.auxiliaryTopLeftArea` and `auxiliaryTopRightArea`, with a 4-point overlap. A Windows display has no equivalent housing measurement, so the fallback is a design reference rather than a universal MacBook hardware measurement.

## Spring and hover behavior

[ContentView.swift](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/ContentView.swift) specifies:

- Opening: `spring(response: 0.42, dampingFraction: 0.8, blendDuration: 0)`.
- Closing: `spring(response: 0.45, dampingFraction: 1.0, blendDuration: 0)`.
- Shared movement/resizing: `interactiveSpring(response: 0.38, dampingFraction: 0.8, blendDuration: 0)`.

[Apple's API documentation](https://developer.apple.com/documentation/swiftui/animation/spring(response:dampingfraction:blendduration:)) defines `response` as spring stiffness expressed through an approximate duration. `dampingFraction` is drag relative to an estimate of critical damping. These are parameters for SwiftUI's solver, not a guaranteed final frame time. Apple does not document an exact cross-framework easing curve for them.

`SpringEase` implements an independent damped-oscillator step response for WPF, using a frequency mapping of `2π / response`, plus a small Hermite correction that makes the finite animation end at exactly 1 with zero velocity. This is a measured design approximation, not a claim to reproduce Apple's private animation solver pixel for pixel. Dependency properties allow the animation's actual duration to be supplied separately from the reference response.

Recommended open configuration is `DampingRatio = 0.8`, `ResponseSeconds = 0.42`, `AnimationSeconds = 0.60`, with a 600 ms WPF clock. Sampling this curve produces a peak of approximately **1.013705** at **349 ms**: a small 1.37% overshoot of the travelled distance, followed by an exact, smooth landing. A 420 ms hard stop with the same endpoint correction reduces the bounce to approximately 0.09%, so it is visually less like the intended spring.

Recommended close configuration is `DampingRatio = 1`, `ResponseSeconds = 0.45`, `AnimationSeconds = 0.60`. This is monotonic, has no overshoot, and also lands with zero velocity. Animation reversal should begin from the current rendered size, as Winisland already does. When an animation duration is shortened, pass that actual duration to `AnimationSeconds` so the stiffness mapping remains consistent. Reduced motion must bypass the clock and button-scale animation entirely.

The reference's [HoverButton](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/HoverButton.swift) fades a gray-at-20% capsule using `.smooth(duration: 0.3)`. It does not scale the button on hover. Winisland's 1.025 hover scale and 0.94 press scale are small, explicitly chosen Windows interaction details to meet the user's request for a livelier feel. They preserve layout bounds, maintain press feedback until release/capture loss, and reset immediately when reduced motion is enabled.

## Verification

The native WPF easing implementation compiled and passed direct verification with **60,001 samples** over its full range: exact endpoints, finite values, bounded opening overshoot of **1.013704890**, a zero-velocity landing, monotonic closing, and preservation of the configurable properties when a WPF `Freezable` is cloned. Button transforms also reset immediately when motion is disabled. This verification compiled the actual control source into an isolated .NET 8 WPF executable; it did not implement a second copy of the spring formula. The application still needs on-screen verification after integration, including rapid open/close reversal and reduced motion.

Google Custom Search was unavailable (HTTP 403: the configured project lacks Custom Search API access). Research therefore used direct reads of the pinned GitHub source and Apple developer documentation. Exact source values and the Windows-specific approximations are separated above.
