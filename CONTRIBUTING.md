# Contributing to Winisland

Winisland is a native C# / WPF notch utility for Windows 10 and Windows 11. Contributions to its desktop interface, integrations, documentation, and verification are welcome. Submit pull requests against `main`.

## Report a bug or suggest a feature

[Open an issue](https://github.com/6fmh/winisland/issues) with your Windows version, Winisland version, chosen notch appearance, steps to reproduce, expected result, and actual result. For media or device issues, include the player, browser, or device model. Remove private notification text, tokens, and personal file paths from anything you share.

Check the [release notes](https://github.com/6fmh/winisland/releases) first: the current source can contain features that the latest installer does not include.

## Build a personal development candidate

Use Windows and the .NET 8 SDK. The build script can download the SDK into the ignored `.tools/` directory when needed.

```powershell
git clone https://github.com/6fmh/winisland.git
Set-Location winisland
.\scripts\Build.ps1 -PrivateBuild -Verify
```

The verified x64 candidate is `artifacts/verification/candidate/private/win-x64/Winisland.exe`. `-PrivateBuild` embeds metadata that disables public update checks and the Update indicator in that executable. It does not change the project version or shared settings, and building does not replace or restart a running instance. Add `-SelfContained` to include the runtime, or `-Runtime win-arm64` when building and verifying on an ARM64 Windows machine.

Public builds and release installers omit `-PrivateBuild` and retain updates. Public portable output without `-Verify` is `dist/win-x64/Winisland.exe`. See [update and build behavior](docs/UPDATES.md).

## Find the relevant source

| Area | Location |
| --- | --- |
| Startup, tray, and application lifetime | `src/Winisland/Program.cs`, `App.xaml.cs` |
| Notch layout and interactions | `src/Winisland/NotchWindow*.cs`, `NotchWindow.xaml` |
| Native controls and styles | `src/Winisland/Controls/` |
| Media, audio, devices, storage, uploads, and updates | `src/Winisland/Services/` |
| Chromium player companion | [Extension source and setup](src/Winisland/Assets/BrowserExtension/README.md) |
| Native verification fixtures | `src/Winisland/*Verification.cs`, `SmokeTests.cs` |
| Build, browser checks, and installer | `scripts/`, `installer/`, `.github/workflows/build.yml` |

Keep the existing native C# / WPF stack. For visual changes, consult the [notch design specification](docs/NOTCH-DESIGN.md) and official Apple references linked there. Distinguish the MacBook's physical camera housing from Winisland's software interactions, and preserve keyboard access and reduced-motion behavior.

## Verify your change

For application changes, run `scripts/Build.ps1 -PrivateBuild -Verify` for a personal candidate. Native verification uses a private Windows desktop and writes JSON reports and PNG evidence under `artifacts/verification/`. Read the actual `results.json`, check for failures, and confirm the production-updater check reports private-build metadata `true` for a personal build. For an existing candidate, use `scripts/Invoke-Verification.ps1 -ExecutablePath <path> -OutputDirectory <ignored-artifact-directory>`.

Keep automated work off the interactive desktop. Do not launch the normal app, open Settings, show notification balloons, manipulate global input, or stop a running Winisland instance as a routine check. Do not invoke `--verification-worker` directly. If isolation fails, inspect the report and fix the cause; verification must not fall back to the interactive desktop.

For companion changes, run `node scripts/Verify-BrowserSafety.mjs` and the relevant browser verification scripts. Browser automation must use a headless browser with a dedicated fixture profile; do not attach to a personal browser. See [player volume verification](docs/player-volume.md#verification).

Installer payload checks use `scripts/Build-Installer.ps1 -Verify`. Full install, upgrade, rollback, and uninstall checks change registry and shell settings and belong in a disposable Windows VM using `scripts/Verify-Installer.ps1 -AllowDesktopChanges` with the installer path. The [Verify and release workflow](.github/workflows/build.yml) runs these gates for public inputs.

For documentation-only changes, check Markdown structure, relative links, feature claims, and `git diff --check`; an app build is unnecessary unless build instructions or application inputs changed. A relative link must resolve to a file included in the repository, not merely an ignored local file.

## Prepare a pull request

Describe the problem, resulting behavior, and relevant verification evidence. Keep changes focused and preserve other contributors' work. New verification entry points must be registered in `BackgroundVerification.IsInvocation` so they receive desktop isolation.

Keep `artifacts/`, `dist/`, `.tools/`, `bin/`, `obj/`, local communication logs, configuration, and credentials out of commits. Installer executables belong in GitHub Releases. Documentation can identify local evidence using code paths; public links should point to tracked files, published releases, or actual workflow runs.

Contributed code and assets must be compatible with the [MIT license](LICENSE) and their own third-party terms. Include dependency or asset provenance in [third-party notices](THIRD-PARTY-NOTICES.md) where appropriate. Apple product artwork, SF Symbols, and San Francisco font files are not bundled with Winisland.
