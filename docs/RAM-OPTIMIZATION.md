# RAM optimization

Verified 11 November 2026. The resource work was coordinated with the concurrent design and notification agents through `communication.md`; a separate service reviewer checked ownership and disposal paths.

## Changes

- The calendar/tools organizer stays unconstructed until first selection. A shared, nonvisual `ReminderScheduler` loads reminder data and delivers due reminders independently; an empty reminder store has no running reminder timer. The organizer and scheduler share one store and avoid duplicate notification subscriptions. Both secondary panels still load normally when opened.
- Spotify's accessibility STA thread starts on its first actual automation request. Constructing or disposing an unused integration creates no worker. Cancellation, handler cleanup and dispatcher shutdown retain their existing guarantees.
- Browser player metadata is normalized when received and reused during matching. Selection, disconnect and pruning avoid temporary candidate/removal arrays while preserving duplicate-title and playing/paused ambiguity rules.
- Artwork is decoded with its longer edge capped at 160 pixels, preserving aspect ratio without enlarging small images. Independently owned, frozen BGRA32 pixels replace a retained `BitmapImage` stream: the final bitmap has at most 100 KiB of raw pixels and releases the original encoded buffer. Encoded input remains capped at 8 MiB and hash-deduplicated. A `WeakReference` regression check confirms collection of a megabyte encoded buffer while decoded pixels remain readable.

Production does not force garbage collection or trim the process working set. Panels remain available after first use to preserve their state; the organizer startup saving applies before those panels are opened.

## Controlled idle comparison

Three alternating baseline/optimized pairs ran as fresh processes on private Windows desktops with empty file-backed profiles and no personal media session. Both candidates used identical `ResourceVerification.cs` bytes and portable Release win-x64 single-file ReadyToRun publish settings. Each run records six natural idle samples; the table uses the median within each run, followed by the median of three runs. MiB means 1,048,576 bytes.

| Metric | Baseline | Optimized | Reduction |
| --- | ---: | ---: | ---: |
| Private committed memory | 47.82 MiB | 45.75 MiB | 4.35% |
| Working set | 114.23 MiB | 110.77 MiB | 3.03% |
| Managed memory before collection | 6.45 MiB | 4.80 MiB | 25.55% |
| Handles | 518 | 512 | 6 handles |
| CPU time over five idle seconds | 46.875 ms | 46.875 ms | No measured change |

Idle managed allocations changed from 11,425.6 to 11,388.8 bytes/second (0.32%); this is too small to claim a meaningful allocation-rate improvement. The principal measured benefit is less startup memory. Artwork-buffer release benefits media workloads with encoded artwork and is verified independently of this empty-media comparison.

The controlled RAM-only candidate excludes the concurrent design and notification changes so their resource costs cannot distort this comparison. Working set includes shared runtime/native pages and varies with system pressure. These figures do not predict totals for active players, large organizer datasets or the user's already-running instance. The separately labelled final forced-collection sample is a retained-memory diagnostic and is excluded from the idle table.

## Verification and evidence

- RAM-only snapshot: `scripts/Build.ps1 -Verify` passed 717 recorded results. Its suite retained the artwork aspect/no-upscale/frozen-pixel/encoded-lifetime checks, actual reminder delivery without organizer UI, Spotify unused/cancelled/used worker lifecycle checks and browser ambiguity checks.
- Current combined source: the design agent's final `Build.ps1 -Verify` passed 824 recorded results (816 assertions), including the resource regression fixtures and current UI/notification tests. All 126 build-input manifest entries match the current tree; normalized enumeration found no omitted source/assets/scripts/license input.
- Standalone `Startup` and `Resources` checks passed against that exact combined candidate, exercising the resource entry point and lazy startup behavior with the current application sources.
- Both runners enforce a private, never switched-to Windows desktop. No user app was stopped or restarted, no global input was injected and no interactive desktop was used.

Local evidence is retained under the ignored `artifacts/ram-optimization/` directory:

- `comparison-summary.json`: method, per-run medians, source/candidate/report SHA256 hashes and all aggregate metrics.
- `comparison-baseline-{1,2,3}/results.json` and `comparison-optimized-final-{1,2,3}/results.json`: raw samples and panel functionality checks.
- `optimized-final-source-manifest.json`, `optimized-final-build.log` and `optimized-final-src/artifacts/verification/results.json`: immutable RAM-only inputs and full suite.
- `current-startup/results.json` and `current-resources/results.json`: standalone checks of the combined candidate.
- `completion-evidence.json`: final source parity, verification report hashes and completion status.

Reproduce the current resource check with `scripts/Invoke-Verification.ps1 -ExecutablePath <verified-candidate> -OutputDirectory <fresh-report-directory> -Suite Resources`. The normal background build remains `scripts/Build.ps1 -Verify`.

## Design references

The required Google Apple-design search was attempted through Google Custom Search; the configured project returned HTTP 403. Apple's official [camera-housing guidance](https://support.apple.com/en-us/102125) was read directly in a background HTTP request. Existing [measured Apple notch geometry](apple-notch-geometry-research.md) and [design research](DESIGN-POLISH.md) remain applicable: the optimization preserves the notch silhouette, controls, motion preferences and reminder behavior.
