# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TDPdf is a Windows-only WPF PDF editor shipped as a single self-contained `TDPdf.exe`. It is a GPLv3 fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); see `NOTICE`. Target is `net9.0-windows` / `win-x64`.

## Build / publish / release

- Dev build (Windows): `dotnet build -c Release`
- Cross-compiling from macOS/Linux (where this repo often lives): add `-p:EnableWindowsTargeting=true`. The app itself only runs on Windows (WPF + native `pdfium.dll` P/Invoke).
- Publish single-EXE: `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true` → `bin/Release/net9.0-windows/win-x64/publish/TDPdf.exe`. Publish also runs the `BundleSource` MSBuild target (`build/bundle-source.ps1`) producing the GPLv3 corresponding-source zip; it uses `git ls-files`, so untracked files are silently excluded.
- Full signed release: `./release.ps1` (Windows; needs Certum cert + signtool), or `./release.ps1 -SkipSign` for a dry run.
- **There is no test suite or linter.** `dotnet build` warnings are the only lint signal — do not introduce new warnings. Nullable reference types are enabled project-wide; don't silence `CS8602` etc. with `!` unless the invariant is genuinely guaranteed.
- Single-file publish uses SDK properties in `TDPdf.csproj` (`IncludeNativeLibrariesForSelfExtract`, compression); do not reintroduce Costura/Fody bundling. `PublishTrimmed` and Native AOT are intentionally off — WPF and the PDF libraries are not trim-safe.

## Architecture

Single-window WPF app with MVVM foundations but no DI. Almost all UI behavior lives in `MainWindow.xaml.cs` (~8,000 lines): tools, rendering, search, signatures, save/flatten, install/uninstall, print, crop, zoom, dialogs, themes. New UI features usually go there unless there's a strong reason to split.

- `ViewModels/MainWindowViewModel.cs` is a foundation-only stub — per its header comment, do NOT wire it into MainWindow yet; migration happens in separate PRs (issue #18). `Services/` is likewise a placeholder for future extractions (only `ZoomViewModel` and the few existing services are live).
- `Models/Annotations.cs` holds the entire annotation data model: `PageAnnotation` subclasses (`TextAnnotation`, `InkAnnotation`, `HighlightAnnotation`, `TextEditAnnotation`, `ImageEditAnnotation`, `CropAnnotation`, `SignatureAnnotation`) plus `SavedSignature` for JSON persistence.

**Three PDF libraries, each used for what it's best at — don't swap them:**

| Library | Role |
|---|---|
| Docnet.Core (PDFium) | Page rasterization for on-screen rendering and the 150 DPI "Save Flattened" export. Supplies native `pdfium.dll`. |
| PdfSharpCore | Page geometry, merge/split, writing annotations into the saved PDF. |
| PdfPig (aliased `PdfPigDoc`) | Text extraction: search, drag-select copy, font matching for inline text edits. |

**Annotation flow:** `_annotations` is a `Dictionary<int pageIndex, List<PageAnnotation>>`. On-screen annotations live on a transient WPF `AnnotationCanvas` overlay; on save they're baked into the PDF via PdfSharpCore. "Save Flattened" instead rasterizes every page through PDFium so nothing remains editable.

**Other load-bearing behaviors:**
- Custom chrome: `WindowStyle=None` + `AllowsTransparency=True`. A `WM_GETMINMAXINFO` hook in `MainWindow_SourceInitialized` keeps maximize from covering the taskbar — preserve it if touching window sizing. All dialogs are custom dark-themed windows; never use `MessageBox.Show` or default file-dialog chrome for new UI.
- Self-installer: first launch from outside the install path offers Install / Run Portable; installs per-user to `%LOCALAPPDATA%\Programs\TDPdf\` (no UAC), registers ProgID `TDPdf.pdf`, uninstalls via deferred batch file. `TDPdf.exe "file.pdf"` opens directly.
- Password-protected PDFs: prompt for password, write a decrypted copy to a temp file, work against that for the session.
- Signatures persist to `signatures.json` next to the EXE. Drawn (`Strokes`) and imported-image (`ImageData` base64 PNG) variants share `SavedSignature`; `ImageData != null` is the discriminator.
- Telemetry (`Diagnostics/`) is opt-in and a no-op by default — gated on a provisioning file at `%ProgramData%\TDPdf\telemetry.dat`. Never expose a raw `TrackException`; crash reporting goes through `Sanitizer` to scrub paths. `Diagnostics/EmbeddedTelemetry.Generated.cs` is produced by `build/embed-telemetry-key.ps1` at release time and is gitignored.

## Conventions

- XAML-named controls the codegen can't resolve are re-fetched in the constructor via `FindName(...)!` into `_camelCase` fields (see the `// Manual element refs` block). Follow that pattern for new named XAML elements.
- UI palette is centralized in `MainWindow.xaml` resources (`BgDark`, `BgPanel`, `AccentGreen`, `DangerRed`, …) and theme dictionaries under `Themes/` (Dark, Light, HighContrast). Use those brushes; don't hardcode hex colors. Toolbar glyphs are `Segoe MDL2 Assets`.
- Set `_isDirty = true` on any change that mutates the document, and route open/close paths through the existing dirty-check prompts.
- Versioning: bump `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` together in `TDPdf.csproj`, add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` (Keep a Changelog / SemVer), and update the compare links at the bottom.
- GPLv3 compliance: keep `LICENSE` and `NOTICE` intact, preserve upstream copyright headers, never reintroduce upstream personal branding. New dialog titles / product strings must say "TDPdf".
