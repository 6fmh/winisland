# Notch Control Center

Open the expanded notch and click the two-switch Control Center icon to the right of the camera space. **Ctrl+6** selects it while the notch is pinned. **Escape** closes the notch. Tab, Space and arrow-key navigation use native WPF controls.

The 640 × 356 DIP surface keeps the quadratic shoulders, black camera space, bundled Inter typography and restrained spring motion. Apple's [current Control Center reference](https://support.apple.com/guide/mac-help/use-control-center-mchl50f94f8f/mac) guides individual network capsules, white active wells with blue symbols and neutral rounded control wells. Subtle borders and gradients suggest depth over the opaque black housing; these are WPF adaptations of Apple's dynamic materials. Slider hit areas remain32DIPs. Header targets adapt to custom camera widths. Battery fill and sound-wave layers reflect current values. Reduced motion finishes transitions immediately.

| Control | Behavior |
| --- | --- |
| Wi-Fi | Toggles Windows Wi-Fi radios; reports the connected SSID when Windows exposes it. Arrow opens network selection in Windows settings. |
| Bluetooth | Toggles Windows Bluetooth radios. Arrow opens device management and pairing in Windows settings. |
| Brightness | Reads and writes active built-in displays through `WmiMonitorBrightness` and `WmiMonitorBrightnessMethods`. External or unsupported displays show guidance instead of a working slider. |
| Sound | Controls the default Windows multimedia output through Core Audio. Shows its device name, volume and mute state. Arrow opens output selection. |
| Airplane mode / Night light / Display / Power | Opens the corresponding Windows settings page. These are labeled shortcuts, with no simulated toggle state. |
| Capture | Closes the notch and opens Windows screen capture. |
| Focus timer | Selects the existing notch timer. |

The music page retains its separate player-volume behavior. Control Center does not change a player's own volume or app mixer levels.

Radio access is requested only after a button press. Missing adapters, hardware switches and policy denial stay unavailable or show an inline error. The UI retains confirmed radio state while a command is pending, then reads back Windows state. A failed change does not invent a successful state.

Read operations and commands are serialized. Slider writes coalesce over 60 ms and keep the most recent adjustment while a write is in flight. Readback does not interrupt pointer capture or issue new volume commands. Device polling runs every three seconds while the menu is active and stops on tab changes or collapse. No control state is persisted.

## Verification

The full `--self-test` suite includes Control Center checks. To run just these checks:

```powershell
.\scripts\Build.ps1 -OutputDirectory artifacts/control-center/candidate
.\scripts\Invoke-Verification.ps1 -ExecutablePath artifacts/control-center/candidate/Winisland.exe -Suite ControlCenter -OutputDirectory artifacts/control-center
```

Verification runs on a private background desktop, using isolated app data and a synthetic device backend for writes. It checks opening without writes, denied and successful radio commands, external changes, delayed/coalesced writes, mute, control-local dragging, unavailable hardware, header bounds, hidden polling and interrupted transitions. Native device discovery is read-only. Results, discovery state and rendered screenshots are saved in the output directory without opening windows on your screen.
