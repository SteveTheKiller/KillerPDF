# TDPdf — Copilot instructions

TDPdf is a Windows-only WPF PDF editor distributed as a single, portable, self-contained `TDPdf.exe`. It is a fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF) (GPLv3); see `NOTICE` for the §5(a) attribution. The repo targets `net9.0-windows`; building and publishing require Windows plus the .NET 9 SDK.

## Build / publish / release

- Dev build: `dotnet build -c Release` (from repo root on Windows). Project is `TDPdf.csproj`.
- Publish (single-EXE bundle): `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true` → output in `bin/Release/net9.0-windows/win-x64/publish/TDPdf.exe`.
  - Publish triggers the `BundleSource` MSBuild target which runs `build/bundle-source.ps1` to produce `TDPdf-<Version>-src.zip` (GPLv3 corresponding source) next to the EXE. It uses `git ls-files`, so an unstaged file will not be included.
  - Publish uses SDK single-file properties in `TDPdf.csproj` (`PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`, and compression enabled); do not reintroduce Costura/Fody bundling.
- Full release (Windows only, requires Certum cert + Windows SDK signtool): `./release.ps1` (or `./release.ps1 -SkipSign` for a test run). Publishes with `dotnet publish`, signs and verifies `bin/Release/net9.0-windows/win-x64/publish/TDPdf.exe`, prints SHA256, and reminds you to paste it into `pdf-landing/index.html`.
- No test suite or linter is configured. `dotnet build` warnings are the only lint signal — do not introduce new warnings (nullable reference types are enabled).

## Cross-platform caveat

The repo often lives on macOS/Linux in automation, but the project is a Windows-targeted WPF app (`UseWPF=true`, `net9.0-windows`, `win-x64`, native `pdfium.dll` P/Invoke). Cross-targeting from Linux requires `-p:EnableWindowsTargeting=true`; otherwise build on Windows with the .NET 9 SDK.

## Architecture

- **Single-window WPF app with MVVM foundations**, no DI yet. Most UI behavior still lives in `MainWindow.xaml.cs`, while small focused view models such as `ViewModels/ZoomViewModel.cs` are used where introduced.
  - `MainWindow.xaml.cs` owns UI state, tools, rendering, search, signatures, save/flatten, install/uninstall, dialogs, print, crop, zoom integration, theme handling, etc. New UI features usually go here unless there is a strong reason to split.
  - `Models/Annotations.cs` holds the annotation/signature/crop/edit data model (`PageAnnotation` and subclasses: `TextAnnotation`, `InkAnnotation`, `HighlightAnnotation`, `TextEditAnnotation`, `ImageEditAnnotation`, `CropAnnotation`, `SignatureAnnotation`; plus `SavedSignature`/`SerializablePoint` for JSON persistence).
- **Rendering vs. editing split** — three PDF libraries, each used for what it is best at. Don't swap them:
  - **Docnet.Core (PDFium)** — high-quality page raster for on-screen rendering and the "Save Flattened" 150 DPI export. Native `pdfium.dll` is shipped by SDK single-file publish with `IncludeNativeLibrariesForSelfExtract=true`; validate Docnet/PDFium loading on a Windows publish because native libraries extract under the .NET bundle extraction directory at runtime.
  - **PdfSharpCore** — page geometry, merging/splitting, and writing annotations into the saved PDF.
  - **PdfPig** (`UglyToad.PdfPig`, aliased as `PdfPigDoc`) — text extraction for search, drag-select copy, and font-name matching during inline text edits.
- **Annotation model** — `_annotations` is `Dictionary<int pageIndex, List<PageAnnotation>>`. The on-screen `AnnotationCanvas` is a transient WPF overlay; on save, annotations are baked into the PDF via PdfSharpCore. "Save Flattened" instead rasterizes pages through PDFium so nothing remains editable.
- **Custom chrome** — `WindowStyle=None` + `AllowsTransparency=True` unless native frame settings are enabled. There is a `WM_GETMINMAXINFO` hook in `MainWindow_SourceInitialized` so maximize doesn't cover the taskbar; preserve it if you touch window sizing. All dialogs are custom dark-themed windows — do not use `MessageBox.Show` or `OpenFileDialog`'s default chrome for new UI; match the existing pattern.
- **Self-installer** — first-launch Install/Run dialog installs per-user to `%LOCALAPPDATA%\Programs\TDPdf\` (no UAC), registers PDF file association (ProgID `TDPdf.pdf`), and writes an Add/Remove Programs entry; uninstall self-deletes via a deferred batch file. CLI: `TDPdf.exe "file.pdf"` opens directly (used by the file association).
- **Password-protected PDFs** — prompt for password, write a decrypted copy to a temp file, edit/render against that for the session.
- **Signatures** persist to `signatures.json` next to the EXE (`AppDomain.CurrentDomain.BaseDirectory`). Both drawn (`Strokes`) and imported-image (`ImageData` = base64 PNG) variants share the `SavedSignature` type — `ImageData != null` is the discriminator.

## Conventions

- `net9.0-windows` target with `LangVersion=latest`. Modern C# and current BCL APIs such as `Math.Clamp` are available; no PolySharp workarounds are needed.
- Nullable reference types are enabled project-wide. New code must be null-annotated; do not silence `CS8602` etc. with `!` unless the invariant is genuinely guaranteed.
- XAML-defined controls that the codegen can't resolve are re-fetched in the constructor via `FindName(...)!` and stored in `_camelCase` fields (see the block under `// Manual element refs`). Follow that pattern for new named XAML elements rather than fighting the generator.
- UI palette is centralized in `MainWindow.xaml` resources (`BgDark`, `BgPanel`, `AccentGreen`, `DangerRed`, etc.) and theme dictionaries under `Themes/`. Use those brushes; don't hardcode new hex colors. Toolbar glyphs are `Segoe MDL2 Assets`.
- Track unsaved edits by setting `_isDirty = true` on any change that mutates the document, and route close/open paths through the existing dirty-check prompts.
- Versioning: bump `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in `TDPdf.csproj` together, add a `## [x.y.z] - YYYY-MM-DD` section to `CHANGELOG.md` (Keep a Changelog format, SemVer), and update the compare links at the bottom. Also bump `$MinVersion` in `build/intune/Detect-TDPdf.ps1` to match — the Intune Win32 detection rule keys off `HKLM\Software\TDPdf` value `Version` >= `$MinVersion`, so leaving it stale makes Intune detect the old release as current.
- GPLv3 compliance: keep `LICENSE` and `NOTICE` intact, preserve upstream copyright headers, and never reintroduce upstream personal branding stripped under §7(e). Any new dialog title or product string must use "TDPdf".

## Modernization notes

- Trimming (`PublishTrimmed`) is intentionally disabled: WPF, PDF libraries, and native PDFium interop are not trim-safe.
- Native AOT is out of scope/not viable for WPF on .NET 9.
- `IHost`/`Microsoft.Extensions.DependencyInjection` composition remains a #18 follow-up.
- `Microsoft.Extensions.Logging` remains a #26 follow-up; keep existing crash logging unless that follow-up lands.

