# Changelog

All notable changes to TDPdf are documented here. TDPdf is a fork of [SteveTheKiller/KillerPDF](https://github.com/SteveTheKiller/KillerPDF); entries from before the rename describe upstream history continued under the previous name.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0.1] - 2026-05-18

### Changed

- New TDPdf crest logo across the app icon, favicon, and landing-page header.
- Reworked release tooling for internal Intune distribution: `release.ps1` now supports PFX signing with a secure password prompt and packages the signed EXE into a `.intunewin` via `IntuneWinAppUtil.exe`. The app gained `/install [/silent]` and `/uninstall [/silent]` CLI flags so Intune can drive install and uninstall headlessly; `QuietUninstallString` is registered alongside the interactive `UninstallString`.

### Documentation

- Replaced `docs/code-signing.md` with `docs/intune-distribution.md` covering the end-to-end Intune Win32 packaging, signing, deployment, and update workflow.

## [1.0.0-tdpdf] - 2026-05-18

First release under the **TDPdf** identity, maintained by **The Doodle Project, LLC**. Combines the rename of the upstream `KillerPDF` codebase with the body of fork work done since (upstream sync through KillerPDF v1.4.0, viewer + annotation polish, snapshot-based undo/redo, native Windows shell, and more).

### Added

- **`NOTICE` file** at the repo root with the GPLv3 §5(a) modification notice and full attribution to the upstream project. README prominently links to upstream and `NOTICE`.
- **Real menu bar** (File / Edit / View / Tools / Help) with full Alt-mnemonic support and `InputGestureText` shortcut hints next to each item.
- **About TDPdf** dialog (Help → About, or F1) showing version, license, and upstream fork attribution.
- **Standard keyboard shortcuts** wired through `Window.InputBindings`: Ctrl+N (new), Ctrl+W (close file), Ctrl+Z (undo), Ctrl+Y / Ctrl+Shift+Z (redo), Ctrl+Shift+S (save as), F1 (about). Existing Ctrl+O / Ctrl+S / Ctrl+P / Ctrl+F continue to work.
- **TdpDialog** keyboard polish: Enter activates the default button, Esc cancels (or dismisses OK-only dialogs). The default button is auto-focused on open. `MessageBoxImage` is now rendered as a Segoe MDL2 Assets glyph (Information, Warning, Error, Question) in the left column of the message body.
- **Edit → Redo** (`Ctrl+Y` / `Ctrl+Shift+Z`) — paired with the snapshot-based undo overhaul below.
- **Viewer + annotation polish, addressing [Reddit/KillerPDF community feedback](https://www.reddit.com/r/KillerPDF/):**
  - **Pan / hand tool** — drag the page in any direction. Middle-mouse drag also pans regardless of the active tool.
  - **Shift + scroll wheel** scrolls horizontally; **horizontal tilt-wheel** (left/right) on multi-axis mice now scrolls horizontally via a `WM_MOUSEHWHEEL` hook.
  - **Eraser tool** — click any annotation (highlight, ink, shape, text, signature, image) to delete it.
  - **Shape tool** — drag-to-create rectangles, ellipses, and lines with a settings bar for stroke color, fill color (toggleable), and stroke width. Shapes are persisted into the PDF on save.
  - **Edit existing annotations with the Select tool** — drag highlights, shapes, ink, and text to move them. Drag the new bottom-right green resize handle to scale highlights, shapes, and ink (ink stroke width scales proportionally).
  - **Zoom to Width / Zoom to Page now re-fit on window resize** — if either mode is active, resizing the window re-applies the fit. Picking an explicit zoom level or using Zoom In/Out/Reset disables the auto-fit.
  - **Signature auto-selects after placement** — the Select tool activates automatically with the new signature highlighted, so you can immediately drag the corner handle to resize or press Delete.
  - **Insert Blank Page now opens a page-size dialog** — choose "Same as current page", Letter, Legal, Tabloid, A3, A4, or A5, plus Portrait or Landscape, instead of always inserting A4.
- **Upstream sync (KillerPDF v1.4.0):**
  - Rotate page (upstream Issue #52). Right-click any page in the sidebar to rotate it 90° clockwise or counter-clockwise. Works on multi-page selections.
  - Insert Image tool (upstream Issue #50). Click the toolbar button, then click anywhere on the page to place a PNG, JPG, BMP, GIF, or TIFF as a resizable annotation. Drag the green corner handle to resize; burned into the PDF on save.
  - PDF link annotation support (upstream Issue #47). Clicking hyperlinks and internal cross-references in a PDF now navigates to the target page or opens the URL in the default browser. Works on both the primary page and all secondary pages in multi-page grid view.
  - New Blank Document (Ctrl+N, toolbar button). Creates a single blank A4 page as a new working document. Prompts to discard unsaved changes if a dirty file is open.
  - Typewriter tool font size picker. When the Text tool is active, a settings bar appears showing size presets (8–72pt) and a color palette. Size and color are stored per-annotation and applied when flattening to PDF.
  - Insert Blank Page. Right-clicking any page in the sidebar now shows a context menu with page-level operations: insert a blank A4 page, move up/down, extract, or delete.
  - Signature resize. Placed signatures now show a green drag handle in the bottom-right corner. Dragging it scales the signature proportionally; releasing commits the new size.
  - Multi-page grid view. When viewing a page, subsequent pages render as a tiled grid to the right and below, allowing context across multiple pages at once.
  - Fit to Width on open. Files now auto-zoom to fill the viewer width on open instead of opening at 100% and clipping wide pages.

### Changed

- **Renamed product, package, and namespace from `KillerPDF` to `TDPdf`.** Versioning resets to 1.0.0 under the new identity.
- Assembly, EXE name, install directory, ProgID, registry keys, Add/Remove Programs entry, and uninstall batch all migrate to `TDPdf` / `TDPdf.pdf`. Existing KillerPDF installations are unaffected by the new build; they continue to work in place.
- Publisher metadata in the Add/Remove Programs entry now reads `The Doodle Project`.
- All in-app dialog titles, automation strings, and wordmark updated to `TDPdf`. The header wordmark renders as `TD` + accent-green `Pdf`.
- Application icon replaced with a placeholder `Resources/tdpdf-icon.ico` (multi-resolution dark/green TD glyph).
- Footer hyperlink and crash-report header updated to The Doodle Project.
- **Native Windows frame is now the default** for new installs (`Settings.UseNativeWindowFrame` default flipped from `False` to `True`). Existing users keep whatever they had configured. The native frame matches Windows 11 chrome, DWM-managed window snapping, and Aero Snap. Custom dark chrome remains available in Settings.
- `OnPreviewKeyDown` no longer swallows global Alt+letter or duplicated Ctrl shortcuts; menu accelerators and `InputBindings` own those paths. Context-sensitive keys (Ctrl+C/Ctrl+A inside text selection, Esc to dismiss search, Delete for annotation removal) still flow through the preview hook.
- `ResizeMode` is now applied after `InitializeComponent` so the custom-chrome resize grip and the native-frame standard resize border each look correct.
- Theme refresh: accent green updated to `#1ea54c`, backgrounds shifted to `#333333`/`#3a3a3a`, sidebar darkened to `#222222`, toolbar and title bar at `#222222`. Film grain overlay added to the main content area. Footer text lightened for readability.
- Sidebar scroll is now handled by an outer ScrollViewer wrapping the page list, allowing the list to size to its content rather than stretching to fill the panel height.

### Fixed

- **Snapshot-based undo** ([#50](https://github.com/doodlemania2/TDPdf/issues/50)) — `Ctrl+Z` now reverses move, resize, erase, image-edit replace/delete/reset, and inline text edits on annotations, not just additions. Each mutating gesture pushes a deep-cloned page snapshot before mutation; no-op clicks are dropped from the stack. A symmetric redo stack (capped at 100 entries each) is populated on undo. Document-level operations (crop / insert page / delete page / reorder / rotate) remain a hard history barrier — page snapshots are cleared when a `Document` entry is undone so they can't be restored onto an out-of-date page layout.
- Scroll wheel in the main viewer no longer triggers page navigation. Previously, at low zoom levels where the page fit entirely in the viewport, every scroll tick caused a full page re-render.
- Page selection no longer flashes centered before jerking left. The layout width is now managed exclusively in the Dispatcher callback, eliminating the double layout pass that caused the visual artifact.
- "Back to TOC" and other internal links on secondary pages now navigate to the correct target instead of advancing to the next sequential page.
- Clicking an internal link now scrolls the viewer back to the top of the target page so links pointing to page tops (e.g. TOC back-links) land correctly.
- Internal PDF links now survive a merge. When merging PDFs, named destinations from the source document's catalog are resolved and rewritten as explicit page-object references in the merged document, so TOC and cross-reference links continue to work after merging.
- Multi-page grid content is now centered in the viewport instead of left-aligned. Panel width is snapped to a whole number of page-width slots so HorizontalAlignment=Center has room to work.
- Sidebar page list no longer shows empty space after the last page. The list now ends at the final page entry with no trailing dead zone.

### Removed

- Upstream author personal branding ("Steve the Killer", `thekiller.net` and `killertools.net` links) stripped from the UI, landing page, footer, and installer dialogs as required by GPLv3 §7(e) for modified versions.
- Upstream-specific landing-page assets (badges, og-image, screenshots) and Umami analytics script removed.

## [1.3.2] - 2026-05-11

### Fixed
- Windows Program Compatibility Assistant popup on first launch. Added an app manifest declaring Windows 10/11 compatibility, which suppresses PCA when the app writes to uninstall registry keys.
- "Set as default PDF viewer" prompt now only appears if the app is not already the default handler. Previously showed on every install/update regardless.
- "Set as default PDF viewer" prompt now uses the dark KillerDialog instead of a native Windows message box.

## [1.3.1] - 2026-05-11

### Fixed
- Print no longer fails with "No application is associated with the specified file for this action" on systems where Edge is the default PDF handler. Printing now uses WPF-native rendering and PrintDialog instead of the shell print verb.
- Zoom dropdown selected value no longer shows in blue - selection highlight now uses the accent green.

## [1.3.0] - 2026-05-08

### Added
- Image signatures. Import a PNG, JPG, or BMP as a reusable signature instead of drawing one. Stored alongside drawn signatures and flattens into the PDF on save.
- Close File (Ctrl+W). Close the current document without quitting the app. Prompts if there are unsaved changes.
- Unsaved-changes protection. The title bar marks dirty files with `*` and prompts before closing or opening a new file with unsaved edits.
- Full-document Find. Ctrl+F search now scans the entire PDF and cycles through all matches, not just the current page.
- Zoom preset dropdown with quick presets (50%, 75%, 100%, 125%, 150%, 200%). Scroll-wheel zoom syncs the box, including non-preset levels.

### Fixed
- Scrolling past the bottom of a page now advances to the next page; scrolling past the top goes back.
- Re-dropping a PDF onto the window after a file is already open now works correctly.
- Owner-password-protected PDFs now open correctly (previously only user-password was handled).
- Dragging the title bar while maximized now correctly restores and moves the window.
- Delete confirmation now reads "Delete 1 page?" or "Delete 2 pages?" instead of "Delete N page(s)?".
- Signature delete button showed a rectangle glyph instead of an X.

### Changed
- All dialog boxes are now fully dark-themed via a custom dialog window. No more native Windows popups.
- Create Signature dialog now uses a dark custom chrome title bar with a red X close button.
- Button hover states and page thumbnail hover in the sidebar are now green instead of the default Windows blue.
- Toolbar icons overhauled: Open Folder, Close File, Move Up, Move Down, Extract Pages, and Merge PDFs all use cleaner glyphs.

## [1.2.1] - 2026-05-04

### Changed
- Code signed with Certum certificate. Windows now shows a verified publisher instead of unknown.
- Cleaned up footer.

## [1.2.0] - 2026-04-24

### Added
- Self-installing EXE. Running the downloaded binary now shows an Install / Run dialog. Install copies the EXE into `%LOCALAPPDATA%\Programs\<AppName>\` (no UAC required), creates Start Menu and optional Desktop shortcuts, registers as a PDF file handler, and adds an uninstall entry to Add/Remove Programs. Uninstall self-deletes via a deferred batch file. Running a newer version from outside the install path shows an Update prompt instead.
- Command-line file argument support so file associations work: passing a path opens the file directly.
- Password-protected PDF support. Opening an encrypted PDF now prompts for the password instead of showing a generic error. The decrypted copy is held in a temp file for the session so all rendering and editing works normally.
- Save Flattened PDF (photo icon in toolbar). Rasterizes every page at 150 DPI via PDFium and writes them as embedded images into a new PDF, producing a fully uneditable document. Pending annotations are burned in before rasterization.

## [1.1.1 (KillerPDF)] - 2026-04-18

### Fixed
- Maximize no longer covers the Windows taskbar. Added a `WM_GETMINMAXINFO` hook so the frameless window clamps to the monitor's work area (multi-monitor aware).
- Two `CS8602` nullability warnings in the font-name cleanup path.

## [1.1.0 (KillerPDF)] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Replaced `Math.Clamp` calls with `Math.Min`/`Math.Max` equivalents.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

## [1.0.1]

_Historical entries to be backfilled._

[Unreleased]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0.1...HEAD
[1.0.0.1]: https://github.com/doodlemania2/TDPdf/compare/v1.0.0-tdpdf...v1.0.0.1
[1.0.0-tdpdf]: https://github.com/doodlemania2/TDPdf/releases/tag/v1.0.0-tdpdf
[1.3.2]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/SteveTheKiller/KillerPDF/compare/v1.1.1...v1.2.0
[1.1.1 (KillerPDF)]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.1.1
[1.1.0 (KillerPDF)]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.1.0
[1.0.1]: https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.0.1
