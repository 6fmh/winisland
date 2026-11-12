# Windows notification previews in Winisland

Winisland v0.5.0 and later can show incoming visible Windows notification banners in the notch, with an app icon, title, and message preview. Windows remains responsible for delivery, sound, banner visibility, and Do Not Disturb. This feature observes banners exposed by the Windows shell through accessibility; support can vary with Windows builds and notification layouts.

## Enable notification previews

1. Check your Winisland version in **Settings → About**. Notification previews require **v0.5.0 or later**; see [published releases and downloads](https://github.com/6fmh/winisland/releases).
2. Open **Settings → Activities** and enable **Notifications on the notch**.
3. Allow the source app's notification banners in Windows Settings. Winisland can preview a banner only when Windows presents it.

Hover over a preview to read it. In the current source, pointer hover and keyboard focus pause its reading timeout. **Dismiss** closes the notch preview. Click the preview to open its source when a supported action is available; the app does not invent quick replies or notification actions. Closing the notch preview does not remove an item from Windows Notification Center.

## If a preview does not appear

- Confirm the installed version and the Winisland notification toggle.
- Check Windows notification settings and Do Not Disturb. Notifications suppressed by Windows are not replayed in the notch.
- Confirm the source app displays a normal Windows banner. Custom in-app popups and unsupported shell layouts may not be readable.
- Use a new incoming notification: opening Notification Center does not replay old notifications.
- If the notch is hidden for a fullscreen app, return to the desktop or review its fullscreen preference in Settings.

If the issue persists, [report it](https://github.com/6fmh/winisland/issues) with the Windows build, Winisland version, source app, and steps to reproduce. Omit private message content.

## Privacy and limits

Notification previews and their short pending queue stay in memory. Message content is not written to files or diagnostic logs, and Winisland does not upload notification text. This is a preview of visible shell banners, not a notification archive, account synchronization service, or general Windows notification subscription API.

Apple accessory connection moments and Winisland's focus-timer notifications are separate from incoming Windows app banners. Model names for supported Apple accessories depend on driver metadata and device identifiers exposed by Windows.

For implementation constraints and research, see [Windows notification capture research](NOTIFICATIONS-RESEARCH.md). For the other desktop activities, return to the [Winisland feature guide](../README.md#features).
