# Winisland

Your music, files and day, right at the top of your Windows desktop. Winisland is a native WPF app with the MacBook's inward-curved top shoulders, rounded lower corners, and a Boring Notch-inspired expanding panel. Artwork glow, a live spectrogram, smooth volume feedback, and gentle springs give the black silhouette a little life. A calendar, editable focus timer, notes, reminders, world clocks and stopwatch keep useful things close by. It uses Windows APIs directly and can upload shelf files to an HTTPS server you choose. No embedded browser runtime or telemetry.

## Run

[Download the latest installer](https://github.com/6fmh/winisland/releases/latest), then run **`Winisland-Setup-0.4.0-win-x64.exe`**. It includes the .NET runtime, installs for your Windows account without administrator rights, and offers Start menu and desktop shortcuts. It also turns off Windows 11's Drop tray so the Windows panel does not cover the notch while dragging files. On recent Windows versions this setting is under **Settings → System → Multitasking → Drop tray**; older versions put it under **Nearby sharing**. Uninstall restores the previous setting unless you changed it afterward. Settings, file references and upload credentials are kept when uninstalling.

Double-click **`dist/win-x64/Winisland.exe`**, or run:

```powershell
.\Start-Winisland.ps1
```

The small portable build needs the **.NET 8 Desktop Runtime (x64)** and Windows 10 version 2004 or later, or Windows 11. [Download the desktop runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8).

Settings open on the first direct launch. The PowerShell launcher uses `--background`, so it does not open first-launch settings; pass `-Settings` to open them explicitly. Closing settings leaves the notch running. Move your pointer over it to expand immediately, click to pin it open, or use **Ctrl+Alt+N**. **Esc** closes a pinned panel. Open settings with **Ctrl+Alt+S**, or double-click the tray icon. The tray menu includes **Quit Winisland**.

Hover starts opening immediately when your pointer enters the notch. The silhouette expands to 640 × 190 DIPs with a restrained, critically damped spring and an exact landing by 600 ms. Content fades in with a slight vertical movement at its native size. Leaving starts closing immediately; interrupted springs retain their current position and velocity. Pinned panels and active editing, dialogs or drags keep the panel open. Quadratic top shoulders and lower corners morph independently of height, without rounding animated sizes to whole pixels. Original vector icons, capsule tabs and subtle button motion keep the details consistent across Windows versions. Reduced motion makes transitions instant and disables button and audio-bar motion.

## What it does

- **Control Center:** click the sliders icon in the notch header, or press **Ctrl+6** while pinned open. Rounded dark cards group Wi-Fi, Bluetooth, brightness and Windows system sound. Radio buttons toggle the actual adapters where Windows permits it; arrows open network selection, pairing and output-device settings. Brightness controls supported built-in displays through Windows WMI; external displays show guidance to use their own controls. Airplane mode, Night light, display, screen capture and power open their Windows controls; Focus timer opens the notch's timer. System volume and mute control the default Windows output independently of the music player's own volume. Device states refresh only while the menu is open, with confirmed readback, coalesced slider writes and inline failure feedback.
- **Now playing:** real Windows media sessions, album art, title and artist, previous/next, play/pause, and seeking. Compact artwork occupies the left wing, with a five-bar 16 × 12 DIP spectrogram on the right; the original camera area stays black. All 13 analyzed frequency bands contribute to the five displayed groups, from bass on the left to treble on the right. Fast attack and a short release let kicks, vocals and cymbals react independently. Expanded artwork uses a 90-DIP cover with 13-DIP corners, a subtle glow while playing and a smaller paused cover. Artist text and the compact spectrum pick up a readable tint from the artwork. WASAPI loopback samples the selected media app's output device, with the default output as a fallback; other audio on that same device contributes too. Audio is analyzed only in memory, with no microphone access or saved recordings. Rendering follows WPF display cadence; pausing, opening, fullscreen hiding and reduced motion stop the spectrum animation. Middle-click the notch to play/pause. Unsupported playback controls are disabled.
- **Player volume:** changes the player's own volume control, preserving Windows app mixer and master levels. Comet and other supported Chromium browsers use the companion extension below; HTML5 audio/video and YouTube's own player are supported, including embedded players. The selected media title/artist identifies the target; ambiguous players stay unavailable. Spotify desktop uses its accessible volume slider when exposed. Its Chromium accessibility control can declare 10% steps, which the notch respects. Volume rail/HUD changes ease over 130 ms, while dragging follows the pointer directly. Presentation animation never generates volume commands. Background writes are coalesced and followed by actual player readback. Unsupported or unconnected players display setup guidance and never fall back to mixer volume.
- **File shelf:** drop files or folders onto the notch, add files with the picker, open them, or drag them into another app. Dropped files upload once connected; folders stay local. Each upload shows queue/progress/result/error states, with copy-link, retry and cancel controls. Right-click for a direct link, the uploaded file, or Explorer. Up to 24 references and successful links persist between launches. Removing a reference leaves the original and hosted file untouched.
- **Focus timer:** click the large countdown and type minutes (`45`), `mm:ss` (`25:00`), or `h:mm:ss` (`1:30:00`), from one second to 24 hours. Enter applies and Escape cancels; ticking never overwrites an edit. Retiming preserves running/paused state, and custom durations persist. Presets, an orange activity ring, progress, pause/resume/reset, compact countdown, and a completion notification remain available. Music and the timer can appear together while the camera area stays centered. The timer uses real elapsed time.
- **Calendar:** browse months, jump to Today, and add/edit/remove local agenda events. Import/export `.ics` files with explicit limits and feedback for unsupported recurring events. The surface expands to 640 × 332 DIPs to fit the month and agenda. This is a local calendar, without calendar-account synchronization.
- **Tools:** quick notes with debounced saving, reminders with optional due times and completion, world clocks with selectable cities and daylight-saving rules, and an elapsed-time stopwatch with pause/reset/laps. Quick actions open Downloads, Documents, screenshot capture, Sound/Network settings and Task Manager. Clock/stopwatch display timers stop when hidden; reminders still notify while the notch is closed. See [organizer behavior and limits](docs/ORGANIZER.md).
- **System status:** smooth player volume-change overlays and battery/charging status on laptops. The volume slider has a 32-DIP hit area, percentage feedback, and a handle on hover/focus/drag. Click anywhere and drag beyond the panel. Scroll uses 2% steps and Shift-scroll/arrow keys 1%, or the player's larger declared step. Desktop PCs omit the battery indicator.
- **Preferences:** notch size, display selection, hover behavior, compact activities, reduced motion, fullscreen hiding, and optional startup at sign-in.
- **Desktop behavior:** topmost, DPI-aware, hidden from the taskbar, and nonactivating while closed. Windows foreground and window-change events immediately slide the notch and its shadow up into the top edge with a quick 100 ms fade when an app enters fullscreen, with a 120 ms slide and fade back when fullscreen ends. Interrupted transitions reverse from the current position. Pointer input passes through as soon as fullscreen starts; reduced motion hides and restores instantly. The notch stays visible during taskbar previews and shell-panel interactions; fullscreen hiding applies to apps rather than Start, Search, or system panels. Transparent areas pass clicks through to the apps underneath. Pinning enables keyboard navigation.

The playback timeline uses a thin 5-DIP rail with a 22-DIP hit area. Its handle appears on hover, focus or drag, and the rail widens to 9 DIPs during a drag. Click anywhere and drag to preview a timestamp, then release to seek once. The panel stays open while scrubbing outside its bounds. Scroll seeks in 5-second steps; Shift-scroll and arrow keys use 1-second steps. Incoming player updates do not interrupt a preview, and closing the panel cancels an unfinished drag.

Startup is **enabled by default in the installer**, with a checkbox to opt out. Portable builds keep it off until enabled in Settings. It uses an account-specific Task Scheduler logon trigger with no delay, network requirement, battery restriction or elevation. The old app-specific Run entry is migrated after first paint. A second instance exits to avoid duplicate notches.

While pinned open, **Ctrl+1–6** switch between music, files, focus, calendar, tools and Control Center; **Ctrl+7** opens Software update. Text fields and clock pickers activate/pin the notch for keyboard input. Page fades reverse safely, and the surface smoothly changes height for the calendar and tools. Reduced motion immediately finishes transitions, including active volume animations.

## Connect Comet player volume

Open **Settings → Player controls**. Winisland packages its companion in `browser-extension/` and copies it to `%LOCALAPPDATA%\Winisland\BrowserExtension`. It registers a per-user native messaging host; it does not change browser developer policy or install the extension silently.

In Comet's `chrome://extensions` page, turn on Developer mode, choose **Load unpacked**, and select the companion folder opened from settings. **Reload any already-open media tabs** after loading or updating the companion. The same folder works with Chrome, Edge, Brave and compatible Chromium browsers. The extension popup shows the local connection status.

After updating Winisland, use the companion's **Reload** button on Comet's extensions page before reloading your media tabs. Uninstall unregisters the native bridge only while it still belongs to that installation; a later portable launch keeps its registration.

The companion reads media metadata/volume and receives volume commands through native messaging and a pipe restricted to the current Windows user. It makes no network requests and persists no browsing or playback history. Spotify desktop connects locally when its accessible controls are available; use **Connect Spotify** in the same settings panel if needed. Spotify volume notifications refresh the notch immediately, and changing tracks or pausing keeps the volume connection intact. Some desktop builds expose only 10% accessibility steps.

For **1% Spotify volume changes**, open **Settings → Player controls → Connect your Spotify account**. Spotify's Web API requires Premium and a registered app. Open the Developer Dashboard from Settings, create an app with Web API enabled, add your account to its allowed users, and register `http://127.0.0.1:43827/spotify/callback/` exactly. Paste its Client ID and choose **Sign in to Spotify**. No client secret is needed. Playback must be selected on this PC in Spotify's device menu. API volume commands work with Spotify minimized; local notifications provide fast feedback when available, with account polling as a fallback. Refresh tokens are encrypted for your Windows account. See [player-volume.md](docs/player-volume.md) for details.

## Connect file uploads

Open **Settings → Uploads** and enter your server's full **HTTPS upload URL** and optional Bearer token, or import a compatible `.sxcu` ShareX uploader. **No server is preconfigured.** Older token-only profiles also require selecting a server again; successful shelf links are retained.

Manual setup sends a multipart POST with a `file` field. ShareX import supports a custom file field, static request headers and form fields, and a JSON response URL such as `$json:data.url$` or `{json:url}`. Unsupported ShareX variables and request types report a setup error. The server URL and credentials are encrypted together with Windows DPAPI for this account, outside settings JSON. Changing servers does not silently carry the old token to the new destination.

Files dropped before setup wait until you save a connection during that session. Interrupted or waiting uploads do not restart automatically after reopening the app; use the file row's upload button. Disconnecting stops current and queued requests. Uploads stream sequentially, do not follow redirects with your credentials, and accept HTTPS links returned by your chosen host or its CDN. Common `url`, `link`, `data.url`, `file` and `viewer` response fields, plain HTTPS responses and ShareX Markdown links are supported.

Winisland limits each file to 90 MB; your server decides supported formats, permissions and any lower limit. Server errors appear on the file row. Cancellation can arrive after a server has stored a file, so check your host before retrying. Original files and folders remain local.

## Updates

Winisland checks [this repository's stable releases](https://github.com/6fmh/winisland/releases) after startup and every four hours. When a newer installer is available, the closed notch shows **Update** and the header gains a **Software update** tab. Choose **Update and restart** to download, verify and install it. Progress and **Cancel** remain available during the download; errors stay in the tab and allow retry.

Use the tray's **Check for updates**, **Settings → About → Check for updates**, or **Ctrl+7** while pinned to check manually. Updates preserve settings, file references and encrypted connections, and restart in the background. See [update and startup behavior](docs/UPDATES.md) for implementation and verification details.

Apple's actual MacBook notch is a physical camera cutout. Its expanding music panels and file shelves are third-party additions. Winisland matches the silhouette and implements those kinds of interactions using Windows services. It does not provide macOS menu-bar safe-area changes, AirDrop/Continuity, phone mirroring, calendar-account integration, webcam preview, external-monitor brightness controls, or arbitrary Windows notification interception. The notch overlays desktop content rather than reserving screen space. Fullscreen hiding uses foreground-window bounds and may need to be disabled for unusual borderless games. Timer state resets when the app exits.

## Build and verify

Build the self-contained `.exe` installer with [Inno Setup 6](https://jrsoftware.org/isdl.php) installed:

```powershell
.\scripts\Build-Installer.ps1 -Verify
```

Pass `-CompilerPath` if `ISCC.exe` is installed elsewhere, or `-Runtime win-arm64` to build an ARM64 installer. The installer and a SHA-256 checksum are written to `dist/installer/`. The `-Verify` option runs native checks against the exact self-contained payload on a private Windows desktop, without installing it or changing shell settings. Reports are under `artifacts/installer-verification/payload/`. Full install/upgrade/uninstall and Drop tray restoration checks belong in a disposable Windows VM: run `scripts/Verify-Installer.ps1 -InstallerPath <setup.exe> -AllowDesktopChanges` there. That separate check temporarily changes the VM account's Drop tray setting and startup registration, restores them afterward, and refuses to overwrite an existing installation. The generated installer is unsigned; distribution signing needs a code-signing certificate.

For the portable build:

```powershell
.\scripts\Build.ps1 -Verify
```

The script uses an installed SDK or installs .NET 8 SDK locally into `.tools/`. It publishes a portable executable with ReadyToRun precompiled code. The application bundle is uncompressed to avoid unpacking it at launch; Inno Setup compresses the installer download separately. With `-Verify`, it publishes to `artifacts/verification/candidate/<runtime>/` so an app you are using can keep running. To include the runtime for a computer without .NET:

```powershell
.\scripts\Build.ps1 -SelfContained
```

For ARM64 Windows, use `-Runtime win-arm64`. Verification must run on the matching architecture; ARM64 has not been tested on this machine.

Verification always runs on a private Windows desktop that is never switched onto your screen. Test windows, settings pages, keyboard focus and fullscreen fixtures stay on that desktop. It registers no global hotkeys, sends no global mouse/keyboard input, and opens no prompts or screenshot viewers. The same control input logic receives local coordinates for volume and playback dragging; hover and button checks use routed WPF events. Foreground changes use test-local fixture state through the same queued event handler; native window bounds and resize/hide events remain checked. Native capture of the physical mouse and actual session foreground switching require an interactive desktop and are outside this background suite. Rendering, persistence, elapsed timers, native window styles, transparent hit testing, fullscreen behavior, interrupted transitions and reduced motion remain covered. Test data is isolated under `artifacts/verification/test-data`; JSON reports and rendered PNGs are written to `artifacts/verification/`.

To verify an existing candidate, use `scripts/Invoke-Verification.ps1 -ExecutablePath <Winisland.exe> -OutputDirectory <artifacts-directory>`. Direct `Winisland.exe --self-test --output <directory>` uses the same isolation. If Windows refuses isolation, verification fails into `results.json`; it never falls back to your desktop. The launcher bounds the run to three minutes and uses a Windows job to clean up fixture processes on exit, timeout or cancellation.

Agent workflow defaults are documented in [AGENTS.md](AGENTS.md). Browser integration checks require headless automation and a dedicated browser profile inside their fixture directory. They reject personal browser endpoints and close only tabs they created. Run `node scripts/Verify-BrowserSafety.mjs` to exercise those guards against synthetic endpoints without opening a browser.

Spectrum verification checks bass, midrange and treble tone placement, loudness response, stereo phase preservation and silence. Audio verification starts two isolated processes with silent audio sessions. It checks app targeting, slider/mute/scroll interactions, switching media sources, and disabled controls when audio is unavailable, while verifying that another app and Windows master volume remain unchanged. The fixture sessions' settings are restored and their processes stopped afterward.

Upload verification checks fresh and legacy profiles without a default server, encrypted connections, custom ShareX headers/form fields/JSON paths, multipart streaming, plain and Markdown links, duplicate queue protection, cancellation and server failures. An optional adapter for a compatible Fastify host can be run with `node scripts/Verify-UploadHost.mjs <host-source-directory> artifacts/verification` and `WINISLAND_UPLOAD_TEST_URL` set to its loopback URL. It uses disposable data and synthetic credentials. The standard suite is self-contained and does not need a host.

Source is under `src/Winisland/`. Settings, file references, protected upload credentials and error logs are stored in `%LOCALAPPDATA%\Winisland`. No media history is saved. `.tools/`, build products, and verification artifacts are ignored by Git.

## License

Winisland is open source under the [MIT license](LICENSE), copyright 2026 6fmh. Bundled dependencies retain their [third-party notices](THIRD-PARTY-NOTICES.md). Contributions belong on `main`; GitHub Actions checks the native app, self-contained payload, and real installer/helper upgrades on a disposable Windows runner. Verification artifacts and the local agent communication log are excluded from Git.

## Design and implementation references

The closed silhouette follows measured official Apple MacBook Air imagery: 190 × 32 logical DIPs, 6-DIP inward top shoulders and 12-DIP quadratic lower corners. Existing custom sizes are retained. Apple's hardware dimensions vary by display; these are image-derived proportions. The 640 × 190 expanding panel began from Boring Notch at commit `b7611bbf32c627a3427408fc142486900d71a23f`, with 19-DIP open shoulders and 24-DIP lower corners. Current Apple HIG and Control Center references guide typography, icons, colors, capsules and restrained motion. See [design evidence and limits](docs/NOTCH-DESIGN.md).

Typography uses bundled **Inter 4.1**, with real Regular, Medium, SemiBold, Light and Display Light faces. It follows the macOS hierarchy with a 13-DIP headline and tabular timestamps. Inter is an independent approximation of Apple's SF Pro, whose downloaded-font license does not permit embedding it in this Windows app. The complete Inter OFL license is included in `licenses/Inter-LICENSE.txt` in both published builds.

See [the design specification](docs/NOTCH-DESIGN.md) and the source-cited research on [geometry](docs/research/notch-geometry.md), [typography and color](docs/research/macos-visuals.md), and [layout and motion](docs/research/notch-interaction.md). Native visual verification covers opaque top-edge pixels, antialiased contours at 100–200% raster DPI, custom compact sizes and activity combinations, the centered black camera area, and the actual bundled font. Desktop composites make the silhouette visible in `fidelity-contact-sheet.png` in the verification output.

Implementation references:

- [Apple: apps and the MacBook camera housing](https://support.apple.com/en-us/102125)
- [Boring Notch](https://github.com/TheBoredTeam/boring.notch/tree/b7611bbf32c627a3427408fc142486900d71a23f), [notch geometry](https://github.com/TheBoredTeam/boring.notch/blob/b7611bbf32c627a3427408fc142486900d71a23f/boringNotch/components/Notch/NotchShape.swift)
- [NotchNook](https://lo.cafe/notchnook) and [DynamicLake](https://www.dynamiclake.com)
- [Microsoft: Windows media session manager](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Microsoft: Core Audio app session volume](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-isimpleaudiovolume)
- [Microsoft: power status](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus)

No Apple product artwork, SF Symbols assets or San Francisco font files are included in the app.
