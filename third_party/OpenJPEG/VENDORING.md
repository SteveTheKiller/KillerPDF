# OpenJPEG

`win-x64/openjp2.dll` comes from `openjpeg-v2.5.4-windows-x64.zip`
in the [OpenJPEG 2.5.4 release](https://github.com/uclouvain/openjpeg/releases/tag/v2.5.4).
The DLL requires the Microsoft Visual C++ runtime.

- Archive SHA-256: `655F6111449DA83F5424F76D74873116BC01CE50CC10361D2B0B4667C3E5E8C3`
- DLL SHA-256: `4E5261DF72CA0702B2DE0BAC29D772E86A316A24373E16AAF6AEC84C7081AEB9`
- License: BSD-2-Clause. The text is from the matching upstream tag.

The other platforms come from the same release. Each library is the versioned file from the
archive's `lib` folder, renamed to the name .NET probes for `openjp2`. None of them ship in the
Windows app; they go into the engine NuGet under `runtimes/<rid>/native/`.

| File | Archive | Archive SHA-256 | Library SHA-256 |
|---|---|---|---|
| `linux-x64/libopenjp2.so` (from `libopenjp2.so.2.5.4`) | `openjpeg-v2.5.4-linux-x86_64.tar.gz` | `77915284C4823BBB5F75053D2B6EC8AF11378C9FC5F3D1742A17E1BED984277D` | `8AEFF1337A466B45F042D4561AECA3E0451272CDA5E71B8BC9B1F6BB3008278A` |
| `osx-arm64/libopenjp2.dylib` (from `libopenjp2.2.5.4.dylib`) | `openjpeg-v2.5.4-osx-arm64.zip` | `214B8B32CF42D32D79698AD766EEA75366D1FB214A7FFE5AE3C563A7E7418981` | `0886F8911034F34C00EBE3B00AD514C9CFD2F000DA9BBCDCB0D37FE2CF40D1C5` |
| `osx-x64/libopenjp2.dylib` (from `libopenjp2.2.5.4.dylib`) | `openjpeg-v2.5.4-osx-x86_64.zip` | `4BF5E64EFB682293898EF5E3ADCAF9DC0E6769212B9A65FC8191E98926EDE595` | `BF6F86E519E786FC48DE1ECAD846887F4349DF07C91ADFA0CD7CF3F9F82C6E55` |

The Linux library needs only glibc's `libc` and `libm`. Windows ARM64 and Linux ARM64 have no
upstream build and stay without JPEG 2000 until one exists.
