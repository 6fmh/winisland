# Dynamic Island motion and interaction research

Research date: 2026-11-11. Scope: the optional iPhone appearance in native WPF;
the original MacBook notch remains the default. All retrieval and video
processing ran offscreen. Google was requested with ordinary HTTP tools because
the Google search MCP is unavailable according to the user. Google returned
consent and JavaScript retry pages, so no search-result snippets are treated as
evidence. The accessible official Apple sources below provide the evidence.

## Official sources and preserved evidence

1. [Live Activities — Apple Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/live-activities).
   Retrieved the [DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/live-activities.json),
   including the presentation, interactivity and transition guidance.
2. [Motion — Apple Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/motion).
   Retrieved the [DocC JSON](https://developer.apple.com/tutorials/data/design/human-interface-guidelines/motion.json).
3. [View Live Activities in the Dynamic Island — iPhone User Guide](https://support.apple.com/guide/iphone/view-live-activities-in-the-dynamic-island-iph28f50d10d/ios).
4. [Animate with springs — WWDC23, session 10158](https://developer.apple.com/videos/play/wwdc2023/10158/).
   The page includes the complete transcript and examples. It explains
   position and velocity continuity, inherited velocity when retargeting,
   perceptual versus settling duration, and zero bounce as a general-purpose
   starting point.
5. [Apple debuts iPhone 14 Pro and iPhone 14 Pro Max](https://www.apple.com/newsroom/2022/09/apple-debuts-iphone-14-pro-and-iphone-14-pro-max/).
   Downloaded its [three-phone demonstration](https://www.apple.com/newsroom/videos/iphone-14-pro-3up/large_2x.mp4)
   and [Face ID demonstration](https://www.apple.com/newsroom/videos/iphone-14-pro-dynamic-island-face-id/large_2x.mp4).
   The former is 10.01 seconds at approximately 29.97 frames/second. The latter
   shows an authentication presentation extending down and returning to the
   pill. Offscreen `ffmpeg` extraction at two frames/second produced reviewable
   contact sheets.

Raw HTML/JSON, videos and the contact sheets are stored locally under
`artifacts/dynamic-island-research/motion/`. These are research evidence, not
redistributed app assets. The call demonstration includes compact music on the
left phone; neither clip shows the expanded Apple Music player. They therefore
cannot establish Apple Music's private font or transport-button metrics.

## States and actions

| Presentation | Apple behavior | Desktop application mapping |
| --- | --- | --- |
| Idle | A black detached pill containing the physical camera region | An empty detached pill at the researched iPhone proportions |
| Compact | One Live Activity, with separate leading/trailing elements around the TrueDepth camera | Artwork or activity identity on the left; waveform, timer or progress on the right; empty central camera-width region |
| Minimal | Additional activities use circular or oval detached presentations | A detached timer bubble when music and a timer are both active |
| Expanded | Touch and hold compact/minimal to expand; preserve relative positions | Existing hover expansion when enabled; clicking pins open, keyboard shortcut opens; native iPhone-sized music content |
| Dismiss expanded | Swipe the expanded activity up | Existing pointer-leave dismissal for unpinned content, Escape/collapse controls for pinned content |
| Open related details | Tap compact/minimal to open the related app screen | A timer bubble opens Focus; the expanded player's menu reaches the existing app feature panels |

Apple's current HIG and iPhone User Guide document up to three simultaneous
Live Activities on supported newer iPhones. The 2022 iPhone 14 Pro visual model
used for this appearance has the familiar pill plus one additional bubble. The
app's desktop mapping deliberately retains its existing discoverable mouse and
keyboard interactions; it does not impose a touchscreen hold on mouse users.

## Shape, layout and visual continuity

The HIG explicitly specifies a **black opaque background** for the compact,
minimal and expanded Dynamic Island. A keyline may distinguish it from dark
surroundings. This is a separate presentation from a translucent notification
or Liquid Glass control.

Compact leading and trailing views should read as one unit, with consistent
color and typography, similarly sized wings, and content snug against the
camera region. Expanded content is an enlarged version of compact/minimal,
with relative placement maintained. Existing elements should animate to their
new positions rather than all disappearing and being recreated. Where moving
elements would overlap, the HIG permits fade-out/fade-in transitions.

The newsroom call demonstration expands laterally into a rounded black capsule;
the Face ID presentation grows down into a taller rounded shape. Both return
to the same detached housing, with no MacBook shoulder attachment. Content is
clipped by the current contour throughout the morph. These are qualitative
observations from the actual video frames; two frames/second is insufficient
to derive exact spring constants.

## Motion implementation recommendations

The HIG permits system/custom Live Activity animations up to **two seconds**.
This is a maximum, not the duration of every Dynamic Island transition. Apple
does not publish the private Dynamic Island spring stiffness, damping ratio,
per-property start times or content-fade duration in these sources.

WWDC23 recommends preserving both position and velocity, including when a
running spring receives a new destination. A spring does not need to bounce:
zero bounce is Apple's recommended general-purpose starting point. The motion
HIG additionally says that trackpad input should receive a more subdued effect
than direct touch and that people should be able to cancel motion.

Winisland already has a critically damped `SpringEase` with velocity-preserving
retargeting and an exact finite landing. Reuse that architecture for the new
surface width, height and contour. Keep text/control rendering at native scale,
with a short opacity transition and small translation, so the surface carries
the spatial change without stretching text. A roughly 450 ms surface morph,
180 ms content entrance and 100 ms exit are reasonable **desktop tuning
estimates**, not measured Apple constants. The final implementation may reuse
existing pacing to keep state transitions consistent across the application.

Retargeting must not show an unpresented destination in a same-dispatcher-turn
expand/collapse reversal. Completion callbacks need generation guards so an
old closing clock cannot hide newly opened content. Dismissal should remove
content input immediately rather than wait for the visual fade.

## Reduced Motion and activity lifetime

The motion HIG requires optional motion and supplementary state feedback.
Live Activities do not animate on Always-On displays with reduced luminance.
For this desktop adaptation, respect both the app's Reduced Motion setting and
Windows' animation preference. Stop waveform/feedback animation and settle the
current surface, content opacity and offsets at their final values. A style
switch, canceled scrub or finished animation must not leave a delayed seek,
mouse capture, old animation clock or hidden interactive content behind.

The HIG says to end a Live Activity immediately when its task/event ends and to
update only when underlying content changes. Use the real media, Focus and
upload state for compact/minimal displays; an idle pill must return when the
last activity ends. Existing explicit controls for disabling live activity,
hover, volume HUDs and notifications remain applicable to both appearances.

## Verification

`DynamicIslandVerification` runs in the existing private-desktop `--self-test`
suite. It checks old settings/default compatibility, persisted selection,
actual Appearance controls/preview, convex opaque detached raster contours at
100/125/150/200%, native music timeline and scrub behavior, multiple activity
bounds, feature access, switching styles with media/custom-size preservation,
interrupted motion and Reduced Motion cancellation. PNGs and bounds/timeline
JSON support visual review; screenshots by themselves do not declare success.
