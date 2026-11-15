# Player volume

The notch controls the selected player's own volume. Its controls do not write to Windows app-session volume or master volume. Unsupported, unconnected, or ambiguous players show connection guidance.

## Connect a browser

1. Open **Winisland Settings → Player controls → Open companion folder**.
2. Open Comet's extensions page, enable **Developer mode**, choose **Load unpacked**, and select that folder. It is published at `%LOCALAPPDATA%\Winisland\BrowserExtension`.
3. **Reload already-open media tabs** after installing the companion. After a Winisland update, click **Reload** on the companion in the extensions page, then reload those tabs. Open its browser popup to check the local connection.

The companion supports Comet and compatible Chromium browsers, including Chrome, Edge, Brave, Vivaldi, and Opera. Firefox and restricted browser pages are unsupported. Private windows need the browser's separate extension permission.

HTML5 video/audio uses the actual element's `volume` and `muted` properties with fractional percentage values. YouTube-style players use their own `setVolume`, `getVolume`, `mute`, and `unMute` API, whose volume values are integer percentages. Embedded cross-origin players are targeted independently. Untitled embedded players use their trusted parent tab title when no media-session title is available.

Player selection uses the Windows media session's title and artist, then playback state when duplicate titles remain. It refuses unresolved ambiguity and known title mismatches. Proprietary players that expose neither controllable HTML5 media nor the YouTube API need a dedicated adapter.

## Connect Spotify desktop

Open Spotify and choose **Settings → Player controls → Reconnect local controls** if the volume control is unavailable. This prepares Spotify's accessible player controls without restarting it or changing its files. The adapter supports localized accessible sliders, including Swedish **Ändra volym**.

Spotify's Chromium slider declares 10% steps on the tested desktop version. The local adapter respects those steps; pointer feedback remains continuous and actual readback shows the player's accepted value. **Connect your Spotify account** in the same settings panel to use Spotify's official API with 1% volume steps. Spotify builds that do not expose a usable accessibility slider can use account volume instead.

### Account connection for 1% volume

1. With Spotify Premium, choose **Settings → Player controls → Connect Spotify** in the account section.
2. Sign in on Spotify's authorization page and allow Winisland to control playback. Return to Winisland after Spotify redirects back. You do not need a developer account, Client ID, or client secret.
3. Select this PC in Spotify's device menu. The adapter accepts an unrestricted computer device whose name matches this PC, and sends every volume command to its explicit device ID. Other computers, phones, speakers, and devices without API volume support remain unavailable. Disconnect the account to return to local accessibility controls.

Winisland uses authorization code with PKCE and requests only `user-read-playback-state` and `user-modify-playback-state`. Sign-in expires after three minutes or when Settings closes. Its callback listener binds only to IPv4 loopback and needs no administrator rights. Previously saved account connections continue to use their original app registration until you reconnect.

**Signed in** confirms authorization is saved; **Account connected · 1% volume steps** appears after Spotify provides a usable local playback device. **Switch Spotify account** lets you choose a different login. If Spotify reports that the account is not registered, the notch says **Spotify account needs approval**, and Settings explains that the app owner must add the signed-in account's Spotify email to User Management. Premium alone does not satisfy that app approval. Winisland retries automatically after approval; another sign-in is normally unnecessary.

Working local Spotify controls remain available when account API access is refused or unavailable, using the desktop slider's actual step size. Account API reads run independently of accessibility, so a blocked or unavailable native provider cannot delay working 1% account volume. Local slider notifications still update volume while the account uses local fallback.

Builds include Winisland's Spotify registration so users can sign in directly. Builds with the registration explicitly removed show an inline availability message and keep local controls available. Spotify development-mode apps require the owner to approve each account; signing in alone does not grant API access to an unapproved account. Contact Winisland support if Spotify rejects account access.

API commands use integer percentages, so scrolling changes Spotify volume by 1%. Pointer feedback remains continuous; readback uses the accepted integer. API writes do not present or activate Spotify's window and work while it is minimized. Successful command feedback is held briefly while Spotify updates its playback state. The API has no playback event stream; cached playback reads run at most once per second while active, with slower closed-notch polling. Spotify's local accessibility volume notifications update the cache immediately when available. `429` responses respect `Retry-After`; expired access tokens refresh automatically, and revoked authorization asks for reconnection.

The account's refresh token and Client ID are saved together with Windows DPAPI encryption in `spotify-account.bin`, separate from ordinary settings. Access tokens and playback state stay in memory. Disconnecting removes the saved credential. Account requests go only to Spotify's account and API endpoints. Local and browser integrations keep working without account sign-in.

References: [PKCE authorization](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow), [set playback volume](https://developer.spotify.com/documentation/web-api/reference/set-volume-for-users-playback), [loopback redirect requirements](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri), [quota modes](https://developer.spotify.com/documentation/web-api/concepts/quota-modes), and [rate limits](https://developer.spotify.com/documentation/web-api/concepts/rate-limits).

### One-time setup for the app owner

The project includes the owner-supplied public Client ID `990d5c6cc850485588e6518a5a7e3a24`. In its [Spotify Developer Dashboard](https://developer.spotify.com/dashboard), enable Web API and register `http://127.0.0.1:43827/spotify/callback/` exactly, including the trailing slash. To use a different Winisland-owned registration, override the public Client ID with `scripts/Build.ps1 -SpotifyClientId <client-id>` or the `WINISLAND_SPOTIFY_CLIENT_ID` build environment variable. Direct MSBuild publishes can use `-p:SpotifyClientId=<client-id>`. Personal candidates must also use `-PrivateBuild -Verify`. Never package a client secret or use another application's Client ID or callback.

CI and installer builds use the project default. The optional repository variable `SPOTIFY_CLIENT_ID` overrides it for every portable and self-contained build. Tag releases fail before building if the effective registration is missing or malformed. The public identifier is embedded in assembly metadata, separate from account credentials.

Spotify currently limits development-mode apps to five approved users and requires the app owner to have Premium. A wider rollout requires Spotify's extended quota approval; a shared authorization link cannot bypass those restrictions. Registering the app and obtaining the necessary access are prerequisites for live account sign-in.

Minimized Spotify can freeze accessibility readback. For an explicit write or connection request, the adapter briefly presents its window behind the current foreground window, then restores the saved placement. Spotify can also post activation after an accessibility volume command returns. The adapter protects the initiating window during the usual 400 ms readback delay, for both visible and minimized Spotify, while honoring new keyboard or pointer focus choices. Its DWM animation setting is restored when readable and left untouched when unreadable. A failed placement capture leaves the window alone. Cancellation and incomplete setup also restore saved state. If the user activates Spotify during the operation, its window stays open. Ordinary polling never presents another application's window.

## Responsiveness and privacy

The notch publishes pointer changes immediately and coalesces queued writes to the newest value. Spotify range/value accessibility events trigger reads without waiting for polling. Fallback local reads run every 150 ms while expanded and about once per second while closed; unavailable controls retry more slowly. Track, artist, and pause changes preserve Spotify's volume target and pending adjustments. Browser track changes still invalidate their precise player targets. Volume presentation animation does not issue player commands. Visible HTML5 media ramps over 80 ms; background tabs reach the target promptly when animation frames are throttled. Native accessibility runs on a separate STA dispatcher, with confirmed player readback after writes.

Playback progress follows WPF's display cadence while the music panel is open. A monotonic clock anchors it to the last Windows timeline update; repeated playback/control events do not reset progress to an old position. Small timestamp rounding errors retain continuity, while seeks and track changes apply larger corrections. Scrubbing still previews locally and commits once on release. Closed panels and reduced motion stop continuous timeline rendering. Repeated artwork notifications reuse the same decoded image rather than restarting its presentation.

The fixed companion ID is `agpnncgfekilbepnojjaolgpbhefolne`; its local native host is `com.winisland.player`. Both pipe endpoints restrict access to the current Windows user. The pipe also hashes the app's data directory, isolating verification from the running app. Native messages use strict UTF-8 and a 64 KiB frame limit; disconnections and malformed input exit quietly.

Only player metadata, volume/playback state, and temporary player/frame identifiers pass through the local browser bridge. The bridge and companion make no network requests and save no browsing or playback history. URLs, page content, cookies, passwords, and account tokens are not passed to the companion. Optional Spotify account sign-in uses the separate connection described above.

## Verification

`PlayerVolumeVerification` covers native framing, real host-process disconnects with stdin still open, malformed JSON/UTF-8, fractional write coalescing, source/artist changes, exact browser targeting, duplicate paused titles, and a real WPF accessibility slider. Run the application's isolated native self-test to include these checks.

`SpotifyVerification` uses an injected HTTP transport to check token refresh/revocation, protected credential rotation, 1% device-targeted commands, immediate local notification feedback, delayed readback, remote/restricted device refusal, and API rate-limit backoff. It also exercises the complete packaged-client sign-in through an injected browser opener, real loopback callback, PKCE token exchange, encrypted save/reload, unavailable registration, and the rendered settings and notch playback/volume controls. These checks do not open a browser or make account requests to Spotify; live Spotify access still requires the owner's valid registration and an approved account. Run the focused suite with `scripts/Invoke-Verification.ps1 -ExecutablePath <candidate>/Winisland.exe -Suite Spotify -OutputDirectory artifacts/spotify-verification`; the launcher isolates its windows on a private Windows desktop.

`scripts/Verify-BrowserPlayer.mjs` exercises real Comet with the current unpacked companion and a local native host. Its fixtures verify actual media-element volume, other-tab preservation, cross-origin frames, untitled movie frames, YouTube API writes, API containment, and tab removal. Each run needs a fresh browser profile to avoid a cached older unpacked service worker.

The live Spotify harness verifies 70% → 60% → 70% through Spotify's real slider, preserves master/app-session snapshots, and checks foreground and minimized-window restoration after successful and canceled writes. It also checks readable DWM animation state; the tested Windows build exposes that attribute only for setting, so the adapter leaves it untouched. Compatibility depends on the desktop build exposing that control.
