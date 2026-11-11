# README research and image sources

Reviewed on 10 November 2026. This note records the references behind the README rewrite and the provenance of its screenshots.

## README and search guidance

| Reference | Applied to Winisland |
| --- | --- |
| [GitHub: About READMEs](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes) | Explain purpose, usefulness, installation, help, and maintenance. Use descriptive headings and relative paths for repository links and pictures. |
| [Make a README](https://www.makeareadme.com/) | Show the product, keep badges useful, state requirements, give concrete usage examples, and explain support, contributing, and licensing. |
| [Google: SEO Starter Guide](https://developers.google.com/search/docs/fundamentals/seo-starter-guide) | Write useful original content with descriptive headings, link text, and image alt text. Use topic terms naturally; avoid keyword stuffing. |
| [GitHub: Classifying repositories with topics](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics) | Repository topics help people discover related projects. The suggested metadata below matches the implemented features. |

Google Custom Search was attempted for both README guidance and Apple design research. Both calls returned HTTP 403, reporting that the configured project lacked Custom Search JSON API access. A direct Google Search request reached a regional cookie-consent page. These attempts did not provide search results; the references above were read directly through background HTTP requests.

The README targets people looking for a Windows notch or Dynamic Island-style desktop utility. Its title and introduction describe that purpose, while feature descriptions mention supported Windows versions, native WPF, media controls, file sharing, and focus tools. GitHub controls the page's HTML title, metadata, and indexing; README content alone cannot guarantee search rankings. No ranking or keyword-volume measurements are claimed.

## Suggested GitHub repository metadata

The public repository was read during this review. Its existing description was “A native Windows notch with media controls, a file shelf, and automatic updates.” It had no topics. These suggested changes are ready to apply in the repository's About settings; this documentation change does not modify remote settings.

**Description:**

> Free, open-source macOS-style notch for Windows 11 and 10. Music controls, file sharing, focus timer, calendar, and Control Center. Native C# / WPF.

**Topics:**

```text
windows windows-11 windows-10 notch dynamic-island desktop-widget
media-controls file-sharing focus-timer calendar csharp wpf
```

## Apple design references

- [Apple MacBook Air product page](https://www.apple.com/macbook-air/) — official front-facing display imagery and the physical camera-housing silhouette.
- [Apple: Adjust apps to appear below the camera area](https://support.apple.com/en-us/102125) — explains the camera housing and macOS's “Scale to fit below built-in camera” behavior.
- [Winisland design specification](NOTCH-DESIGN.md) — existing measured geometry, typography, materials, motion, and fidelity limits.

The README uses actual Winisland renders to show the inward top shoulders, lower curves, black center, compact activity wings, and expanding panels. Apple's physical notch is distinguished from Winisland's software interactions. No Apple imagery or proprietary fonts were copied into the screenshot assets.

## Screenshot provenance

All seven PNGs come from the successful [v0.4.0 Windows verification run](https://github.com/6fmh/winisland/actions/runs/42910036606), at source revision [`da69fb4`](https://github.com/6fmh/winisland/tree/da69fb48ffe82c50fc0f13d04bc9c90d28e01c5e). Its main native report contains 646 recorded results with no failures. The PNGs were copied without editing, cropping, or resampling.

| Public asset | Original verification filename |
| --- | --- |
| [Now playing](images/now-playing.png) | `productivity-player-volume.png` |
| [Compact notch](images/compact-notch.png) | `fidelity-compact-1-desktop.png` |
| [Focus timer](images/focus-timer.png) | `fidelity-expanded-focus-desktop.png` |
| [File shelf](images/file-shelf.png) | `fidelity-expanded-shelf-desktop.png` |
| [Calendar](images/calendar.png) | `productivity-calendar.png` |
| [Notes](images/notes.png) | `productivity-notes.png` |
| [Control Center](images/control-center.png) | `control-center.png` |

These are renders of the actual WPF interface with deterministic fixture data, rather than captures of a personal desktop. The background is a synthetic composite, and `NotchVisualVerification.CreateArtwork` draws the sample cover illustration. “Midnight City,” “M83,” “City lights.png,” agenda entries, notes, “Studio Wi-Fi,” and device percentages are sample data. The music-volume and Control Center pictures demonstrate supported states supplied by test fixtures; device and player availability on a user's PC can differ.

The seven assets total 198,117 bytes (about 193 KiB), keeping the gallery lightweight. Original reports and intermediate research downloads remain in ignored local verification artifacts.
