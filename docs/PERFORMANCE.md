# Measured performance improvements

Measured 12 November 2026 with three alternating baseline/optimized pairs. The additional changes reduce browser-helper startup and memory, upload-processing work, and timeline allocations. They did **not** demonstrate lower idle RAM or CPU for the main application.

The baseline includes the current bug, UI and updater-cadence fixes. Both candidates contain byte-identical measurement helpers and use .NET SDK 8.0.427, Release, win-x64, framework-dependent single-file publishing, ReadyToRun and embedded private-build metadata. Measurements ran sequentially on private Windows desktops on an Intel i5-11400F with 12 logical processors. Calibration runs and concurrent installer builds are excluded from the comparison. The main personal app and browser companion stayed running throughout.

Numbers below are medians of three independent run medians. Each run contains five repetitions per timeline/spectrum workload, three upload batches, five new native hosts, and three browser-state batches. Raw samples, run ranges, passing checks, input manifests and complete candidate-payload hashes remain under local ignored `artifacts/measured-performance/`.

| Browser native host, per process | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Private memory at handshake | 11.91 MiB | 7.32 MiB | 38.5% lower |
| Working set at handshake | 48.01 MiB | 27.89 MiB | 41.9% lower |
| Startup to framed handshake | 181.94 ms | 85.93 ms | 2.12× faster |
| Process CPU through handshake | 171.875 ms | 78.125 ms | 54.5% lower |
| Handles at handshake | 324 | 288 | 36 fewer |

`Program` routes valid native-host invocations after the isolation/worker guards and before WPF application construction. Separate non-inlined UI methods keep WPF initialization out of this path. Existing framed hello/ready, 128 state relays, invalid-input and disconnect checks pass. Startup run medians ranged from 159.86–211.56 ms before and 65.91–90.11 ms after. Both candidates' median one-second host idle CPU samples were zero at the available resolution; this does not imply literal zero CPU use.

| Upload processing, two sequential 24 MiB files | Baseline | Optimized | Change |
| --- | ---: | ---: | ---: |
| Wall time | 50.94 ms | 27.13 ms | 1.88× faster |
| Process CPU | 93.75 ms | 31.25 ms | 66.7% lower |
| Allocated bytes | 1,017,376 | 197,920 | 80.5% lower |
| Service change notifications | 774 | 10 | 98.7% fewer |
| Property notifications | 774 | 34 | 95.6% fewer |
| Representative binding reads | 10,836 | 74 | 99.3% fewer |

The real multipart client streams to an injected counting sink without network access or whole-file buffering. These are local upload-processing savings, **not** a prediction of faster internet transfers. Wall-time run medians ranged from 40.93–54.35 ms before and 22.41–28.03 ms after; CPU medians ranged from 78.125–109.375 ms and 31.25–46.875 ms respectively.

The byte producers sample progress before `Progress<T>` posts callbacks: the first sample, intermediate samples at most every 100 ms, and immediate final 100%. They create no additional timers or workers. A stalled stream retains its last reported value until another chunk arrives. The same producer limiter is used for installer downloads, whose network throughput was not benchmarked. Shelf items cache immutable path text and non-local status strings, use specific property notifications, and suppress identical updates. File/folder classification remains live so replacing an original at the same path preserves existing behavior. Queue cancellation, late successful responses, genuine direct links and original file hashes remain verified.

| Timeline method computation, 6,000 fractional frames | Baseline allocation | Optimized allocation | Reduction |
| --- | ---: | ---: | ---: |
| Main MacBook timeline | 2,980,800 B | 1,606,400 B | 46.1% |
| Main island timeline | 3,508,800 B | 2,134,400 B | 39.2% |
| Dynamic Island control directly | 2,256,000 B | 1,070,400 B | 52.6% |

Timeline labels format only when the displayed second changes. Remaining time retains its independent upward rounding; tooltip duration and seek availability update even within the same second. Fractional slider values, pending user seeks, appearance changes and animation cadence remain intact. The Dynamic Island control also avoids setting unchanged slider properties.

Main MacBook method wall time changed from 168.89 to 143.25 ms and direct Dynamic Island control time from 43.62 to 33.27 ms. Direct-control run medians were 38.43–52.67 ms before and 32.79–37.07 ms after. The main island method changed from 160.15 to 153.36 ms, with overlapping ranges. These are control/controller computation timings, not rendered FPS. Main island process CPU medians actually rose from 250 to 265.625 ms, with broadly overlapping 218.75–281.25 and 187.5–312.5 ms ranges; no CPU reduction is claimed for that path. Allocations are the repeatable timeline result.

Browser equal-state handling refreshes target liveness without replacing identical player data or notifying UI consumers. In each 4,096-duplicate batch plus one meaningful change, notifications fell from 4,097 to 1 and allocations from 15,953,064 to 13,993,456 B (12.3% lower). Real title, artist, volume, mute and playback changes still notify, and the actual 30-second expiry remains covered. JSON framing/parsing still runs for every packet. Bridge timing was inconclusive: median wall time rose from 402.38 to 477.33 ms, with overlapping run ranges of 333.07–597.28 and 457.26–549.92 ms; CPU medians were 828.125 and 875 ms. The fixture includes its packet producer and readback consumer. No bridge throughput or CPU speedup is claimed.

Spectrum analysis now precomputes its 13 fixed bin intervals once per analyzer. The measured two-second stereo PCM workload remained allocation-free; wall time changed from 12.23 to 12.07 ms and CPU stayed at 15.625 ms. This difference is within variation. Dual-channel FFT, band math, hop cadence and out-of-phase behavior remain unchanged; no meaningful spectrum CPU gain is claimed. Hidden compact spectrum updates retain their original behavior.

| Main application, natural idle without media | Baseline | Optimized |
| --- | ---: | ---: |
| Private memory | 47.47 MiB | 47.96 MiB |
| Working set | 113.41 MiB | 113.98 MiB |
| Managed memory | 6.48 MiB | 6.48 MiB |
| Allocation rate | 11,446 B/s | 11,427 B/s |
| Process CPU over five seconds | 93.75 ms | 109.375 ms |

No reliable idle improvement was measured. CPU run medians ranged from 31.25–125 ms before and 31.25–109.375 ms after; memory ranges overlapped. Working set varies with system pressure and includes shared pages; private memory is a separate measure. Process CPU is quantized in 15.625 ms increments and may include other work inside the fixture. Forced-GC retained-memory diagnostics are excluded from all natural idle and workload comparisons. The fresh idle fixture has real deferred services but no personal media, automatic update request, tray registration or real upload/browser network activity.

The highest remaining CPU investigations are audio-session discovery on the rendering path, browser DOM/media discovery, and media-provider event/artwork bursts. They need representative active-session or dedicated headless-browser profiling, including route changes, delayed providers and frame-time tails. Background persistence and settings writes can also affect interaction latency. The remaining live shelf classification checks can block on slow paths; moving them off WPF requires explicit refresh/invalidation coverage. Font/resource removal was not applied: a small payload hypothesis does not establish worthwhile RAM savings or identical font fallback. The broader ranked proposals and required evidence are in [PROJECT-OPTIMIZATION.md](PROJECT-OPTIMIZATION.md).

The final application candidate passed **1,535 assertions, zero failures, and eight informational records** (1,543 total), including the new timeline, progress, cancellation, browser-state and bidirectional file/folder checks. Each of the six controlled Performance reports passed all 98 checks on its private desktop. New measurement dispatch uses the existing registered `--resource-test` isolation path; no standalone UI entry point bypasses it. Actual native music rasters were inspected. Apple camera-housing and motion references from the design audit remain applicable; geometry, typography, accessible interactions and render cadence were preserved. The native stack remains C#/WPF.

To reproduce a candidate's application gate and one performance run:

```powershell
& scripts/Build.ps1 -PrivateBuild -Verify -OutputDirectory artifacts/performance-candidate
if (!$?) { throw 'Application verification failed.' }
& scripts/Invoke-Verification.ps1 -ExecutablePath "$PWD/artifacts/performance-candidate/Winisland.exe" -Suite Performance -OutputDirectory artifacts/performance-run
if (!$?) { throw 'Performance verification failed.' }
```

Preserve an immutable baseline and optimized candidate with identical helper bytes and build settings, then alternate at least three fresh run pairs without other builds or timing suites. Aggregate the actual reports:

```powershell
$baseline = 1..3 | ForEach-Object { 'artifacts/measured-performance/controlled/baseline{0:00}' -f $_ }
$optimized = 1..3 | ForEach-Object { 'artifacts/measured-performance/controlled/optimized{0:00}' -f $_ }
& scripts/Compare-Performance.ps1 -BaselineDirectories $baseline -OptimizedDirectories $optimized -OutputPath artifacts/measured-performance/comparison.json
```

The local baseline is `baseline1`, the final candidate is `optimized3`, and controlled reports are in `controlled/`. `final-evidence.json` records current input parity, single-file bundle hashes, report hashes and the comparison hash. An optional post-measurement DLL query in the original replay script emitted missing-path messages because the assemblies are bundled into the EXE; the complete payload was independently hashed before and after every run and never changed. Original reports and logs are preserved. No app deployment, restart, commit, push or release publication was performed for this performance task.
