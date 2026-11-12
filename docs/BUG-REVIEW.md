# Project bug review - 9 November 2026

The bug-review team audited the native C#/WPF application, shipped browser companion, verification harnesses, build scripts and installer. Three subagents reviewed UI/runtime, media/browser integrations, and data/device services; the primary agent reviewed startup, native integration, verification isolation and packaging. The separate Apple fidelity team coordinated its concurrent changes through the local-only `communication.md` log.

Review complete for the verified current source. All confirmed defects found in this review are fixed, and the final portable, Spotify and self-contained payload gates passed. The final audit matched all 101 application/assets/script/installer files in the isolated verification copy against the current worktree and verified the installer checksum. Exact evidence paths are recorded in `artifacts/bug-review/final-evidence.json`.

## Fixed defects

| Area | Problem and corrected behavior |
| --- | --- |
| Playback | Pending seeks and drag capture now cancel when closing or leaving music; hidden tabs stop rendering. Artist changes distinguish otherwise identical tracks. Nonfinite seek positions are refused, provider timeline-start changes reset the elapsed-time anchor, and timestamps preserve hours beyond 24. |
| UI/runtime | Clicking an already hover-expanded panel pins it. External application launch failures give inline feedback. The tallest Control Center panel retains its shadow space. Verification workers cannot restore a foreground-window snapshot. |
| Browser companion | Expired players release cache capacity; navigation removes stale documents and frames. Commands target exact documents and late acknowledgements cannot revive old targets. One broken page player cannot suppress others, and metadata clipping preserves UTF-16 surrogate pairs. |
| Spotify | Malformed device, volume and token responses fail safely. Rejected refresh results cannot overwrite a disconnected account. Protected credential parsing is validated, temporary plaintext is cleared, and disconnecting a fresh profile succeeds when its directory is absent. |
| Audio | Disappearing or inactive routed endpoints fall back to the default output; failed endpoint references are disposed and retry checks remain throttled. |
| Shelf/uploads | Paths are canonicalized and deduplicated; malformed saved rows cannot discard later valid ones. Successful hosted links survive deletion of their original file. Upload deadlines include streamed response bodies, malformed host/ShareX responses are rejected, and cancellation prevents late success. Credential files are bounded, native DPAPI output is cleared, and fresh-profile upload disconnect is harmless. |
| Settings | A failed settings commit restores the exact previous startup registration. Null monitor values normalize safely. |
| Calendar/organizer | Calendar component structure, date types and bounded RFC durations are validated. All-day ranges retain date semantics across DST and preserve multiple days when edited. Later updates remain possible at event capacity; nonexistent local midnights and maximum-date reminder parsing do not crash. |
| Control Center | Slider writes are coalesced without unbounded pending tasks. Duplicate mute writes are suppressed, and failed mute operations restore the control. |
| Apple devices | Later recognizable profiles can identify the physical device; startup announcements stay quiet and subsequent announcements deduplicate. Container metadata migration and multiple known product IDs are handled. |
| Verification/packaging | Isolation errors use the selected suite's report name. Quoted output paths retain trailing separators. Hidden helper processes preserve exit codes, capture logs and have bounded lifetimes. Architecture checks accept the matching x64/ARM64 host and reject mismatches before publishing. Browser checks reject duplicate profile switches and junction escapes. |

Meaningful regressions were added to the existing native verification suites and an isolated harness executes the actual shipped browser scripts. No normal app, visible browser, installer, screenshot viewer or personal browser profile was launched for verification. Owned test processes were cleaned up.

## Coverage and evidence

- The assigned production and verification C#/XAML files were read across all services, controls, application startup and window partials. Project/manifest resources, bundled assets/licenses, all shipped JavaScript, build/start/verification scripts and Inno Setup configuration were reviewed.
- `scripts/Build.ps1 -Verify` passed the final Apple-integrated suite with 662 recorded results (654 assertions passed, none failed), including both fresh-profile fixes and the new motion/glyph verifiers. Motion sampling ran on the private desktop with Windows animations enabled. An earlier host-enabled run passed 615 results, including native upload through the actual Fastify route/storage, SVG rejection and revoked-token rejection using disposable data and synthetic credentials.
- `scripts/Invoke-Verification.ps1 -Suite Spotify` passed 94 assertions against the final exact-source candidate. This explicitly covers timeline/presentation checks omitted by the default suite.
- `node scripts/Verify-BrowserCompanion.mjs` passed 13 checks; report: `artifacts/browser-companion/results.json`. Browser safety passed seven unsafe-endpoint rejection cases and dedicated-profile acceptance without launching a browser.
- PowerShell parse checks and architecture mismatch guards passed. Standalone Control Center verification passed with spaces and a trailing separator in its output path.
- `scripts/Build-Installer.ps1 -Verify` compiled the final self-contained installer and its exact payload passed 662 recorded results (654 assertions, none failed). The produced installer SHA-256 matches its checksum file. The previous missing-directory failure is superseded by this fresh-profile pass.
- Apple geometry, glyph/palette, camera binding and motion changes received a read-only cross-review. The integrated Control Center raster and motion samples were inspected. Final outputs live in the isolated checkout identified by `artifacts/bug-review/final-checkout-path.txt`; the three report paths and installer checksum are in `artifacts/bug-review/final-evidence.json`. Shared build outputs and the running app were preserved.

## Practical limits

This is a local engineering bug review. Live Spotify accounts and personal browser playback were not exercised; protocol/API behavior used isolated fixtures. Full install/upgrade/uninstall and startup/Drop tray rollback checks require the disposable Windows VM specified by `AGENTS.md`; this review compiles the installer and checks its exact self-contained payload in the background. ARM64 runtime behavior requires an ARM64 machine. Apple notch dimensions vary by hardware; the Apple fidelity team's cited measurements and documented approximations govern the visual changes.

Google research was attempted by every review agent and returned HTTP 403. Official Apple documentation and the project's cited references were fetched through background HTTP instead. The native stack and desktop isolation requirements were preserved.
