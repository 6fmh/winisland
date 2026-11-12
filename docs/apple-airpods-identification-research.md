# AirPods identification

Researched 12 November 2026 for generation-specific connection labels. Three Google searches returned a Swedish consent page; no usable Google results were available. Research requests and fetched references are saved locally in `artifacts/airpods-models/research/`.

## References

- [Apple: Identify your AirPods](https://support.apple.com/en-us/109525) supplies current product names and the official earbud/headphone A-number table, including AirPods 4, AirPods 4 (ANC), AirPods 5, AirPods Pro 3 and AirPods Max 2.
- [Apple: AirPods](https://www.apple.com/airpods/) confirms current product names. [MacBook Pro](https://www.apple.com/macbook-pro/) and [Live Activities HIG](https://developer.apple.com/design/human-interface-guidelines/live-activities) guide the existing black housing and concise, readable status presentation. The implementation preserves its native WPF layout and personalized device name; exact model information appears on the status line and in accessibility announcements.
- [AirPodsDesktop Bluetooth model lookup](https://github.com/SpriteOvO/AirPodsDesktop/blob/main/Source/Core/AppleCP.cpp) documents observed Bluetooth product IDs. Its [domain tests](https://github.com/SpriteOvO/AirPodsDesktop/blob/main/Tests/AirPodsDomainTests.cpp) cover AirPods 5 IDs `2030`/`2036`, including captured advertisement fixtures. These are community observations, not an Apple-published Bluetooth identifier specification. Only identifier facts are used; no implementation or artwork is copied.
- [AirBattery model lookup](https://github.com/lihaoyun6/AirBattery/blob/main/AirBattery/Supports/Supports.swift) independently corroborates `2019` for AirPods 4, `201B` for AirPods 4 ANC, and `2014`/`2024` for AirPods Pro 2 with Lightning/USB-C charging cases. The former Winisland lookup incorrectly classified `201B` as AirPods Pro.
- Microsoft documents [ModelNumber](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-modelnumber), [Bluetooth ProductId](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-deviceinterface-bluetooth-productid), and [VendorIdSource](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-deviceinterface-bluetooth-vendoridsource). The latter is a Byte; vendor/product IDs are UInt16. Bluetooth SIG vendor source 1 and USB vendor source 2 have separate namespaces.

## Behavior and limits

Generation identification requires Apple vendor evidence plus an observed Bluetooth product ID or an official A-number from Windows' model-number property. Bluetooth product IDs are not interpreted as USB product IDs. A renamed headset, local adapter manufacturer, or friendly name never proves a generation. Unknown and conflicting metadata retain a generic family label. Known IDs select the appropriate existing AirPods, Pro or Max illustration.

The lookup covers AirPods 1–5, the distinct AirPods 4 ANC model, AirPods Pro 1–3, and AirPods Max 1 variants. AirPods Max 2 requires an official model number because no independently established Bluetooth ID was available. Both observed AirPods 5 IDs identify the generation; the charging-case variant is left unspecified. A-number identification excludes charging-case numbers.

Discovery remains event driven and read only. It collects metadata from the association endpoint and devices/interfaces in the same physical container, briefly retries late generic AirPods metadata, preserves silent startup and profile deduplication, and never connects, pairs, scans advertisements, routes audio or invents battery/ANC state. “(ANC)” identifies the hardware variant, not the currently selected noise-control mode.

Verification uses deterministic captured-ID/model-number fixtures, malformed/conflicting/foreign-vendor cases and actual private-desktop WPF connection presentation for AirPods 4, 4 ANC and 5. A passing fixture suite is not a claim that every physical AirPods generation was available or reconnected on the development machine. Windows drivers may omit model metadata, in which case exact identification remains unavailable.
