# Winisland GitHub discoverability audit

Reviewed **12 November 2026** against the current worktree, public GitHub API and repository HTML. The audit covers the application and integration feature surface, documentation, images, contributor paths, release discovery, and repository metadata. No search-ranking or keyword-volume measurements were available.

**Applied settings update, 12 November 2026:** GitHub now selects **v0.5.0** as latest through both its API and public redirect, and the latest-download installer URL succeeds. The repository has an updated About description and 12 relevant topics. Merged-branch cleanup, the pull-request Update branch option, Dependabot alerts, and Dependabot security-update PRs are enabled. Existing read-only Actions defaults, secret scanning and push protection are retained. Published release assets and tags were preserved.

## Highest-priority findings

| Priority | Surface and evidence | Improvement | State |
| --- | --- | --- | --- |
| High | Repository About: generic description, no topics | Use descriptive copy and topics so the Windows notch purpose is clear in GitHub search and shared links. | Applied: updated description and 12 topics |
| High | README: Dynamic Island source availability and notification support were unclear | Name both appearances, distinguish source from releases, describe supported notifications and Apple accessory moments, and use descriptive setup headings. | Implemented locally |
| High | README linked twice to ignored `AGENTS.md` | Provide a public contributor guide with source layout, build instructions, and actual background verification rules. | Implemented in [CONTRIBUTING.md](../CONTRIBUTING.md) |
| High | Public latest-release API and web redirect selected v0.4.0 although stable v0.5.0 exists | Check release provenance, correct latest selection, and verify both routes plus the installer URL. | Corrected and verified: v0.5.0 |
| Medium | Notification feature had research documentation but no setup guide | Provide a focused Windows notification setup, privacy, and troubleshooting page linked from the README. | Implemented in [notification guide](NOTIFICATIONS.md) |
| Medium | Nine links in engineering docs pointed to ignored logs, reports, screenshots, or `dist/` | Present local evidence as explicitly local code paths, preserving historical findings without broken public links. | Repaired in BUG-REVIEW, NOTCH-DESIGN, and DESIGN-POLISH |
| Medium | Shared repository links use GitHub's generated Open Graph image | Upload the prepared social preview through repository Settings. This improves shared-link recognition; it is not a claimed ranking factor. | [PNG prepared](images/winisland-social-preview.png); web UI upload pending (no supported API) |
| Later | No website homepage or GitHub Pages deployment | Consider a dedicated product site only if ongoing documentation and search needs justify it. | Optional; no deployment proposed as a prerequisite |

## Ready-to-use repository metadata

The public repository is `https://github.com/6fmh/winisland`, uses `main`, and is public under MIT. At review time its description was:

> A native Windows notch with media controls, a file shelf, and automatic updates.

Its topics were empty, homepage was unset, and `has_pages` was `false`. Repository HTML used this description in its page title/description and social text. Changing About therefore improves the actual public summary; changing the README heading alone does not control those tags.

Applied **About description**, describing the published feature set:

> Free, open-source macOS-style notch for Windows 11 and 10, with music controls, a file shelf, focus timer, calendar and notifications. Native C# / WPF.

Applied **topics**, all tied to the public project:

```text
windows windows-11 windows-10 notch desktop-widget
media-controls file-shelf focus-timer calendar csharp wpf open-source
```

GitHub permits up to 20 topics; these 12 fit its lowercase/hyphen format. Add `dynamic-island` and update the description when the new appearance is included in public source or a verified release, with accurate availability wording. It is currently in the local worktree. Leave the website field empty until there is a maintained public product or documentation site; do not add a duplicate repository URL merely to fill it.

The prepared [social preview](images/winisland-social-preview.png) is a **1280 × 640 PNG** for **Settings → General → Social preview**. It uses the existing full [now-playing screenshot](images/now-playing.png), scaled proportionally, plus project text rendered with bundled Inter. The screenshot is an actual WPF fixture render from the [documented v0.4.0 verification run](README-RESEARCH.md#screenshot-provenance), with sample media and synthetic background. No Apple imagery or new product UI was drawn. It is a sharing asset rather than another README badge or gallery image. Merely committing the PNG does not configure GitHub's social preview.

## Search intent and useful landing content

These are relevant topic phrases, not measured search volumes. Keep related Windows notch terms on the main project page and send detailed setup questions to their guide.

| Intent | Main destination | Content to maintain |
| --- | --- | --- |
| Windows notch / Dynamic Island for Windows | [README](../README.md) | Free/open-source status, Windows requirements, actual appearances, screenshot, installation, and source/release availability |
| Spotify or browser music controls on Windows | [Player volume guide](player-volume.md) | Browser setup, supported players, Spotify local versus Premium account integration, limitations |
| Windows file shelf / ShareX uploads | [README file shelf setup](../README.md#file-shelf-and-sharex-uploads) | References versus original files, user-configured HTTPS host, ShareX scope, folders and size limit |
| Focus timer / local calendar and notes | [Organizer guide](ORGANIZER.md) and [README timer setup](../README.md#timer-calendar-and-tools) | Custom timer durations, local storage, `.ics` interoperability, supported tools and sync limits |
| Windows notification previews | [Notification guide](NOTIFICATIONS.md) | Version, Activities toggle, shell-banner compatibility, Do Not Disturb, privacy, no history replay |
| Native C# / WPF desktop project | [Contributing guide](../CONTRIBUTING.md) | Source map, private development candidates, verification, contribution requirements |

Do not label the focus timer as a complete Pomodoro work/break scheduler, promise universal AirPods model/battery support, or imply Apple services run on Windows. Current limitations are part of useful search content. Keep screenshots captioned and their alt text descriptive; the original seven README PNGs already total only **198,117 bytes**, so an additional gallery or badge wall is unnecessary.

## Release and freshness checks

Authoritative observations during this audit:

- Authenticated `/repos/6fmh/winisland/releases/latest`, including a timestamp query, returned **v0.4.0**. A public HTTP request to `/releases/latest` also resolved to `/releases/tag/v0.4.0` with HTTP 200.
- The releases collection and `/releases/tags/v0.5.0` returned **public, non-draft, non-prerelease v0.5.0**, published 11 November 2026, with the x64 installer, checksum, and verification ZIP.
- The current project version is `0.5.0`, but Dynamic Island is absent from the tagged v0.5.0 source and present in the worktree. A local version number or release-notes file does not prove feature availability in a download.

The authorized settings follow-up corrected only v0.5.0's latest designation. Both the ordinary and timestamped API/public redirect now select it, and `/releases/latest/download/Winisland-Setup-0.5.0-win-x64.exe` returns HTTP 200. Downloaded installer SHA-256 matches the sidecar and GitHub digest: `bc96a842bc98ab1b47f461007b4364da48b0539e76ee577aee2b1fd98124b292`. Release assets, tags and published notes were preserved. [All releases](https://github.com/6fmh/winisland/releases) and [v0.5.0](https://github.com/6fmh/winisland/releases/tag/v0.5.0) remain direct discovery paths.

The existing verification ZIP records the manual publication route and its limitation: exact locally built installer bytes were not installed in a fresh VM; identical application/installer inputs passed the disposable-VM scenarios, and the published payload passed 831 local private-desktop checks. Its source commit `8fdec68` and the current tag commit `d8356bc` differ only in local-agent instruction exclusion; application, script, installer, license and build-policy inputs are identical. The later [successful tag workflow](https://github.com/6fmh/winisland/actions/runs/43128678179) preserved the already published assets. Local `AGENTS.md` now requires greatest-numeric-stable-version checks before commits and after source pushes/release publication, with API and public redirect verification.

For each future release, keep the README availability note, screenshots and setup steps consistent with the tagged payload. Add release notes describing user-visible changes and requirements; use the existing verified publication workflow. Refresh the source-only Dynamic Island note only when its tagged installer exists and has passed the release gates. No version bump or installer publication is needed for this documentation audit itself.

## What GitHub controls

The repository is a GitHub page, not an existing GitHub Pages website. GitHub controls its HTML template, title generation, robots policy, canonical URL, sitemap, and rendered Markdown sanitization. Adding `robots.txt`, `sitemap.xml`, meta tags, or JSON-LD to this source repository would not configure the GitHub repository page. Do not add unsupported FAQ schema to the README or treat image text as a substitute for readable content.

For a future standalone site, revisit page titles, canonical URLs, crawlability, structured data, and performance in that deployment. Respect the existing native C# / WPF application stack; no web framework migration is needed to improve repository discoverability.

## Verification and follow-up

This work used three collaborating reviewers for product claims, documentation discovery, and live GitHub metadata. Feature/setup copy was checked against current source and the v0.5.0 tag. Markdown was rendered with GitHub's GFM API; relative targets and heading anchors were checked against files intended for Git history, not ignored local evidence. Scoped whitespace checks cover the changed documentation. Reports and the social-card generator stay under ignored `artifacts/github-seo/`; no application build or user desktop interaction was needed.

Documentation improvements are local until committed and pushed. Include the new contributor guide, notification guide, social PNG and all referenced current-source research documents in that review; preserve concurrent agents' changes. About/topics, PR housekeeping, Dependabot alerts/security-update PRs and latest-release selection have been applied and read back. Social preview still needs the GitHub web UI: neither the official REST update-repository schema nor GraphQL UpdateRepositoryInput supports uploading it. No ranking gain is claimed without observation.

After publication, record the About description/topics and social preview, confirm public README links and `/releases/latest`, and observe GitHub traffic/referrers, views, visitors and clones over successive periods. These measure discovery and engagement, not search position by themselves. Review titles and content when actual user queries or support issues reveal missing information; avoid repeated speculative keyword additions.

## Sources

- [GitHub: About READMEs](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes)
- [GitHub: Repository topics](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics)
- [GitHub: Social preview](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/customizing-your-repositorys-social-media-preview)
- [Google: SEO Starter Guide](https://developers.google.com/search/docs/fundamentals/seo-starter-guide)
- [Apple: MacBook Pro](https://www.apple.com/macbook-pro/) and [camera-housing guidance](https://support.apple.com/en-us/102125), checked before edits to keep the physical-housing and software-overlay descriptions accurate

Official references were fetched through background HTTP. Google Search returned a regional consent page and no general search MCP was available, so this audit does not claim live Google results, keyword demand, or competitive rankings.
