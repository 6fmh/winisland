# Visibility and resource use

Winisland suspends work used only by hidden views and prepares player controls as the pointer approaches the notch. Native mouse movement events trigger preparation without a stationary-pointer timer or input interception. The approach area extends 64 DIPs horizontally and 48 DIPs vertically around the housing. Discovery starts on that event; completion still depends on the player and its accessibility or network provider.

| State | Resource behavior |
| --- | --- |
| Closed, pointer away | No recurring player-volume reads or player timeline rendering. The fullscreen/recovery fallback runs every two seconds. |
| Pointer approaching | Immediately enable player discovery and reset its retry deadline before opening. Moving away suspends it again. |
| Music visible | Refresh player volume at the active cadence and render the visible player. |
| Another tab selected | Suspend hidden player polling and spectrum/timeline rendering. Existing organizer and Control Center timers already follow panel visibility. |
| Notch hidden or minimized | Suspend player polling and both render callbacks. |
| Settings on another page, hidden, minimized or closed | No player-service subscription. Closing also cancels pending Spotify connection operations. |

True backend changes still deliver the closed-notch volume HUD. Accepted volume writes and their final readbacks finish after collapse. Reminders, notifications, device connection events and visible compact live activities retain their configured behavior. Spectrum capture stops while a notification covers the compact activity and resumes when it becomes visible again. Panels retain their state between openings; production does not force garbage collection or trim the working set.

## Settings memory retention

A heap inspection of an isolated fixture found the live notification banner retaining a closed settings window through WPF's `Grid.ExtendedData.TempDefinitions`. WPF shares this layout scratch array between grids and releases a grid's reference during arrange. Notification sizing previously measured a hidden grid without guaranteeing arrange before dismissal; later settings layout could fill the same buffer with settings row definitions.

Notification sizing now completes the manually measured grid's arrange immediately. This uses ordinary WPF layout and releases the scratch reference without reflection or forced collection. A strict `WeakReference` fixture verifies the closed settings window is collectible while the notch and shared player service stay alive, including after notification presentation/dismissal.

## Verification

`scripts/Build.ps1 -PrivateBuild -Verify` runs the lifecycle assertions in the full private-desktop suite. The focused runner is:

```powershell
./scripts/Invoke-Verification.ps1 -ExecutablePath <candidate> -OutputDirectory <fresh-directory> -Suite Visibility
```

It uses the existing `--self-test` isolation guard. Assertions exercise the actual notch and settings windows, an injected counting backend, local screen coordinates, open/closed/hidden states, pending writes, backend notifications, render callback attachment and settings lifetime. Its JSON includes closed/open read counts and fixture prewarm timing. Test-only collection verifies lifetime; it is excluded from performance timing. The Performance suite includes this workload as well.

The final combined private build passed 1,633 recorded results; the focused Visibility run passed 145. That run recorded zero closed-state reads, three reads during 500 ms of visible music controls, a 0.6226 ms injected-backend prewarm, and successful settings collection. All 153 application, asset, script, installer and license inputs matched their pre-build hashes and membership after verification. Reports are `artifacts/visibility-optimization/final-4/full/results.json` and `final-4/visibility/results.json`.

Zero closed-state reads prove removed work; they do not establish a universal CPU or RAM percentage or real Spotify/network response time. Evidence and original diagnostic failures remain in the ignored `artifacts/visibility-optimization/` directory.

## Design references

Google Apple-notch research was attempted through background HTTP search; Google returned a consent interstitial and no general search MCP was exposed. Apple's official [camera-housing guidance](https://support.apple.com/en-us/102125) and the existing [measured notch geometry](apple-notch-geometry-research.md) guide this work. Resource scheduling preserves that housing and the existing motion preferences. WPF layout behavior was checked against Microsoft's [Grid source for .NET 8.0.31](https://github.com/dotnet/wpf/blob/v8.0.31/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Grid.cs).
