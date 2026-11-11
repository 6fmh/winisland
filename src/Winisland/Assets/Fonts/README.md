# Inter font assets

These unmodified static TrueType fonts come from the official [Inter 4.1 release](https://github.com/rsms/inter/releases/tag/v4.1), published November 16, 2024. Copyright (c) 2016 The Inter Project Authors. They are distributed under the [SIL Open Font License 1.1](LICENSE.txt); retain the complete license with published builds.

Only six faces from the release archive are included. No system installation is required. Windows WPF `GlyphTypeface` inspection confirmed the family and weight names below.

| Asset | WPF family | WPF weight | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| Inter-Regular.ttf | Inter | Normal | 411640 | `40D692FCE188E4471E2B3CBA937BE967878F631AD3EBBBDCD587687C7EBE0C82` |
| Inter-Medium.ttf | Inter | Medium | 417300 | `97AD806F526E41546D46365BB3A393145F75B7B1568913DB74549AD8B8DBA872` |
| Inter-SemiBold.ttf | Inter | SemiBold | 419744 | `78A843FADE9D4612A5567302FB595B56976EB5FCEBF4FEA5A5912D638BAFCDE3` |
| Inter-Light.ttf | Inter | Light | 412844 | `164414F0AACBE98A7E64ADDC43F7B3BFD2E32F7B90E101FEEAB227F14C371BDA` |
| InterDisplay-Light.ttf | Inter Display | Light | 413164 | `1D6072BC7807FA7BF08A6D273368F04AC68E77438258F32975057A61C8772811` |
| InterDisplay-SemiBold.ttf | Inter Display | SemiBold | 418540 | `0310D7A325896129730C6C8CF9A6E0F81EE258BEDF77B1FF059B2A7B75F74E02` |

The font files total 2,493,232 bytes. The source archive was `Inter-4.1.zip`, downloaded directly from <https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip>. Its SHA-256 is `9883FDD4A49D4FB66BD8177BA6625EF9A64AA45899767DDE3D36AA425756B11E`. The semibold display face was extracted unchanged from `extras/ttf/InterDisplay-SemiBold.ttf` on November 11, 2026 after verifying that same archive checksum.

Embed `.ttf` files as WPF resources. The application pack families are `pack://application:,,,/Assets/Fonts/#Inter` and `pack://application:,,,/Assets/Fonts/#Inter Display`. The display Light face is intended for large timer numerals; display SemiBold supplies the actual requested weight for settings headings. The text family provides the body and control hierarchy. Copy `LICENSE.txt` into published output so the license is available alongside the application.

Inter is an independently designed interface typeface, used here as a distributable Windows approximation of the proportions and hierarchy of Apple's system font. It is not SF Pro.
