# Winisland Player Companion

This companion lets the notch change the website player's own volume. It supports YouTube's player API and HTML5 video/audio, including cross-origin embedded players. It leaves the browser's Windows mixer volume and other tabs unchanged.

1. Keep Winisland running. Open **Settings → Player controls → Open companion folder** to find the published companion and local native messaging bridge.
2. Open `chrome://extensions` in Comet or Chrome (`edge://extensions` in Edge, or `brave://extensions` in Brave).
3. Enable **Developer mode**, choose **Load unpacked**, and select this folder.
4. Reload already-open media tabs once. Open the companion from the browser's extension menu to check its connection.

After a Winisland update, click **Reload** on this companion in Comet's extensions page, then reload the media tabs. Unpacked extensions can otherwise keep an older cached background worker until the extension reloads.

The fixed extension ID is `agpnncgfekilbepnojjaolgpbhefolne`. The public manifest key preserves that ID when this folder moves or Winisland updates. Native messaging accepts only this extension origin and connects over a pipe restricted to your Windows account.

The companion runs in frames containing HTML5 media and needs access to the sites where you want player controls. Private/incognito windows require separate permission in the browser. Restricted browser pages and players that expose neither HTML5 volume nor the YouTube API are unsupported. It never substitutes the browser's app-mixer volume. Firefox is not supported by this Chromium companion.

Only the current players' title, artist, playback/volume state, and transient frame identifiers travel locally to the notch. URLs, cookies, passwords, account tokens, browsing history, and page content are not sent or saved. Neither the companion nor the native bridge makes network requests.
