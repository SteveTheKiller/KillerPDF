# Interactive PDF smoke checks

Prerequisites: Windows with .NET SDK selected by the repository's global.json, an unlocked
interactive desktop, and no running KillerPDF user session. Build commands write only build
output and use the official NuGet registry. Run one repository at a time.

From this checkout:

```powershell
dotnet build KillerPDF.csproj --configuration Release
dotnet build tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release
dotnet run --project tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --no-build -- --verify-fixture bin/Release/net10.0-windows/KillerPDF.dll "$env:TEMP/pdf-fixture-check"
dotnet run --project tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --no-build -- --interactive bin/Release/net10.0-windows/KillerPDF.dll "$env:TEMP/pdf-ui-smoke"
```

FlaUI.UIA3 5.0.0 is confined to this test project; the app already excludes tests/**. The console
runner returns 1 on unavailable desktop, timeout, assertion failure, or host failure, and returns
0 only after all requested checks pass. Hosted PR CI runs only --verify-fixture, with its evidence
retained under ci-results/fixture; it does not run this interactive suite.

The isolated WPF host loads the actual compiled app's StampWindow, ColorPickerDialog, theme
loader, PDF renderer, and working-document services. It creates its own one-page PDF fixture and
portable settings directory. It does not launch App.OnStartup, register a protocol, open a user
document, or interact with another app. Only the host process created by a case is terminated.

Dark, Light, and SE98 cases accept two stamp colors and cancel a subsequent edit, then assert the
committed StampSpec preserves the accepted values. UI Automation invokes real buttons and sets
the hex field through its value pattern. RenderTargetBitmap captures only test-owned WPF content,
including the full stamp window. These are retained diagnostic images, not approved pixel baselines.
The save/reopen case uses the actual PdfWorkingDocument service with an unchanged synthetic PDF.
It checks page count and exact saved bytes. It does not certify the main app's Save As picker,
serialization of applied stamps, window startup, other themes, keyboard focus, or a screen reader.

UIA calls have bounded connection/transaction timeouts. A discovery timeout retains the test-owned
process tree and its AutomationIds; stamp-managed.txt records the actual stamp HWND and peer ID.
Every theme's own child host is closed on success or failure. These diagnostics do not alter the
StampDialog discovery assertion or the accepted/canceled color expectations.

The fixture-only command parses, renders, saves, and reopens without showing a window. It is useful
when an interactive desktop is unavailable; it does not count as a FlaUI pass. The existing
build/Test-ColorPickerDialog.ps1 covers all 45 theme/accent resource states and brush shapes in its
compatible PowerShell runtime. Pass `-Configuration Release` for the same assemblies used above.
Keep that check as separate evidence. Maintenance uses its actual app PDFium renderer;
development uses the KillerPDF engine renderer. This tooling preserves that architecture difference.

## Field preset selection regression

From the development checkout, after building the app and this test project:

```powershell
dotnet tests/KillerPDF.UiSmoke/bin/Debug/net10.0-windows/KillerPDF.UiSmoke.dll --verify-presets bin/Debug/net10.0-windows/KillerPDF.dll App.xaml C:/Users/steve/scratch/kp-bench-render/preset-selection-check --startup-smoke
```

Use a fresh evidence directory. This mode loads the actual window resources and
opens the populated Field Presets dialog in all 13 default themes. It changes
selection and checks both rows' name and detail colors against the selected and
unselected semantic brushes. It retains PNGs and a result with the assembly hash.
Normal app startup is suppressed, preferences remain in memory, and files stay
under the supplied evidence directory or the process's temporary directory. It
uses WPF controls directly and does not certify physical input or a screen reader.

## Help overflow regression

Use `--verify-scroll-overlays APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
to measure and render the compiled Help card in Dark, Light, and 98SE. The check
covers short and normal heights, both scroll ends, fitting content, the keyboard
view, and separation of the scrollbar from Close. App startup is suppressed and
the output folder owns all test settings. No desktop input is sent.

## Toolbar and display layout regression

Use `--verify-toolbar APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
to render the toolbar at 1000, 1320, and 1600 pixels with captions beside,
under, and hidden. The check rejects unused space that can fit an overflow
tool, verifies the footer display controls sit between view modes and zoom,
and renders Color Correction to check its right inset and preview zoom.
Exposure, saturation, warmth and tint must change the preview and reset to neutral.
An exposure adjustment is applied, saved and reopened to verify the PDF output.
It also checks document and empty-pane transitions, rail right-click routing,
and opening saved copies in an empty second pane or a new tab.
Use a fresh output directory. Startup and user settings remain isolated;
the check sends no desktop input.

## Table extraction and export

After building the app and this test project, use
`--verify-table-export APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`.
Use a fresh output directory. The isolated host renders the actual table dialog
in all 13 themes at 100% and 150% app scaling, checks a 480x360 window at 250%,
and checks all 20 locales at 640x480 and 150%. It verifies invalid selections,
editable cells, and access to the scrolling content and save controls.

The workflow cases open an owned synthetic PDF through the public window entry,
invoke the rendered Save menu, select a table area, edit a cell, and save through
the real file picker. They inspect XLSX and CSV output, cancel both review and
save, switch the source during review, and try to overwrite a PDF fixture named
with a CSV extension. Original bytes must survive each case.

The captures and results identify the compiled app hash. These WPF control
checks do not certify physical mouse dragging, native display scaling, screen
readers, or interoperability with Excel and LibreOffice. Keep those checks
separate from the public engine tests and the existing release gates.

The same isolated invocation accepts `--verify-office-export`, `--verify-sidebar-actions`,
`--verify-grain-themes` and `--verify-dialog-text` instead of `--verify-table-export`.
Office checks include editable and page-image DOCX/PPTX saves, source-change cancellation,
and command-line layout selection. Sidebar checks cover cursor menus, measurements, signing,
the F1 list and command dispatch through Search Tools. Grain checks seal a WPF style before
switching through every theme. Dialog text checks render 12 prompt variants across all 13 themes,
including multiline warnings, errors, choices, confirmations, password and text entry.

`--verify-paragraph-edit` uses the same isolated invocation. It checks paragraph
selection, resizing, font size, alignment, line spacing, overflow, preview
invalidation, cancel, Undo/Redo and save/reopen through the actual dialog and
main-window command. Rendered checks cover all themes, locales and high app scale.

`--verify-pending-text-recovery` uses the isolated startup-smoke invocation to check
unsaved annotation text and existing page-text edits through recovery, saved PDF
contents and the reopened text provider. It sends no desktop input. The separate
`--verify-render-recovery` mode also injects filesystem permission failures and
belongs in the disposable lab; the pending-text mode does not run that injection.

## Certificate catalog

Use `--verify-certificates APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
with a fresh output directory. The isolated host imports a generated public CA
certificate, verifies search, untrusted import, trust removal and cancellation,
certificate removal and read-only Windows controls,
and renders Dark, Light, 98SE and all 20 locales at the minimum dialog size.
The result identifies the compiled assembly hash. Settings and imported
certificates stay in the supplied directory; Windows trust stores are read-only.
These WPF checks do not certify physical input, a screen reader, or signature
verification against a certificate already trusted by Windows.

## Viewer and headless bitmap parity

Use `--verify-viewer-parity APP_DLL MANIFEST_JSON NEW_OUTPUT_DIRECTORY --startup-smoke`.
The manifest has a corpus `Root`, `AppXaml` and `AppXamlSha256` identifying the
unchanged App.xaml from the tested application's frozen source snapshot, and
`Documents` entries with relative `Path`,
`Sha256` and descriptive `Category` fields. Inputs must remain under that root
and retain their hashes. Use an owned, unlocked test desktop and serialize this
check after timing runs because it creates offscreen WPF windows and renders PDFs.
The isolated smoke settings lock the quit choice and disable tray hiding so
closing test windows cannot wait on an unrelated quit-preference prompt.

The isolated host loads those verified resources and the compiled application window, opens each PDF through
the public window entry, selects single-page mode through its routed shortcut,
and reads the primary page image's bitmap. A separate process runs that same
application's headless CLI at the observed pixel size. Exact BGRA pixels and
dimensions must match; every input and result is retained in `result.json`.
Supply twenty representative documents for the release A4 matrix. A smaller
manifest is a diagnostic check. This verifies primary viewer bitmap delivery;
annotation/form overlays, zoom detail composition and physical display output
need their own checks. It does not establish cross-renderer accuracy.

## Document-window lifetime regression

Use an owned UI VM and a fresh output directory. Run
`--verify-window-lifecycle APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`.
The isolated host opens two synthetic documents in actual MainWindows, waits for
the first window to finish closing, requires the application to designate the
survivor as its primary window, and uses the survivor's real Save menu and
file picker. The saved PDF must reopen as the survivor's one 300 by 200 point
page, and both input files must remain unchanged. Ordinary unsigned Save As may
add an incremental rotation update, so saved output is checked as a document
rather than requiring byte identity with its input.

The fixture locks only the optional clean-document quit confirmation in its
isolated settings. It does not bypass dirty-document prompts, launch normal app
startup, register a protocol, change installed settings or use personal files.
It verifies shared process/window lifetime, not the physical tab-drag gesture or
external-open IPC routing.

## Document-window session restoration

Use an owned UI VM and a fresh output directory. Run
`--verify-window-session APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
The host isolates portable settings and working PDFs before services initialize,
suppresses process startup and registration, and lets normal window restoration run.
It opens two synthetic PDFs in separate windows, closes them and constructs a new
window from saved settings. Both documents must return together, with the original
window's size and position and unchanged source files.

Use `--verify-window-session-secondary-first` for the reverse close order,
`--verify-window-session-manual-close` to verify a manually closed tab stays closed,
`--verify-window-session-forget` to check disabling remembered files restores an
empty window, or `--verify-window-session-close-all` to exercise the tab menu's
Close All action after a previous document window closes.
These modes require `--session-isolation`, not `--startup-smoke`,
because startup-smoke skips normal restoration. Single-document windows hide the
tab strip; the manual-close assertion checks the remaining filename and absence of
a visible strip. The fixture verifies window reconstruction in one isolated process,
not actual process restart, physical tab dragging or Windows sign-out.

## Update document-window state

The updater state regression uses
`--verify-update-window-state APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
It opens a synthetic PDF in a secondary document window, rotates it through the actual
thumbnail menu, and verifies that the primary window's existing update-host contract
detects those unsaved edits. The source bytes must remain unchanged. This is an isolated
WPF runtime check; it does not install an update or certify relaunch behavior.

## Saved-file update restart

On an owned UI VM, run `--verify-update-restart-stage APP_DLL APP_XAML OUTPUT_DIRECTORY SHARED_STATE --session-isolation`
with a fresh shared-state directory. The isolated host opens three saved synthetic PDFs across
two windows, closes a fourth document window, and disables remembered files. Preparation must
freeze editing while leaving native windows enabled. Canceling must restore editing and remove
the pending record. Committing must survive scope disposal, accept a separate process's native
close request, and close all captured windows despite tray mode without changing the preference.
The F11 check first requires normal shortcut transitions to work, then requires no transition
while the handoff is prepared. Each check waits through the fullscreen animation.

After that process exits successfully, run `--verify-update-restart-consume` with the same
arguments and shared state but a fresh output directory. The second isolated process calls the
same claim seam as eligible app startup before constructing its first window. Exactly the three
live files must return together, with the active file and original geometry retained. The closed
file stays excluded, source hashes and preferences remain unchanged, and a later window cannot
replay the handoff. Keep actual exit codes, bounded process receipts and both streams for each
phase. This does not run a real installer or normal startup registration.

Use `--verify-update-restart-early-exit` in place of the stage mode to exercise
application exit before updater activation returns. It requests native closure,
executes public application shutdown, and disposes the prepared handoff from the
Exit event before the caller can report a started updater. The same separate
consumer must still restore the saved session exactly once.

## Windows session-ending protection

Use an owned UI VM and a fresh output directory with
`--verify-session-ending APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
The isolated host exposes WPF's protected session-ending extension through a public
wrapper and supplies framework logoff arguments. It makes the secondary window
dirty through the real thumbnail Rotate menu, chooses Cancel in the existing save
prompt, and requires the cancellation flag, both windows, unsaved edits and source
bytes to survive. It does not send an actual Windows shutdown or logoff command.

`--verify-session-ending-save` chooses Save and reopens the synthetic output to
require its 90-degree rotation, a cleared dirty indicator and shutdown permission.
`--verify-session-ending-controls` edits two windows, cancels the later prompt after
the first approval, then checks both Don't Save choices permit closure despite tray
mode without another prompt or source changes. Keep each process under an external
watchdog and retain its exit code and streams. A result file alone does not replace
the process receipt. These checks exercise application lifecycle policy, not native
Windows message delivery or another application's shutdown cancellation.

## Print-only annotation visibility

On an owned UI VM, use `--verify-print-visibility APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`
with a fresh output directory. Independent positive and negative controls run before product assertions.
The isolated host opens a synthetic PDF through the real window entry, requires visible fields,
measurements and redaction marks, and rejects their print-only counterparts on screen. It clicks Print
and requires the annotation, measurement and redaction review ink at distinct preview locations.
It closes the preview without submitting a printer job and checks unchanged source bytes.
This covers native WPF integration, not physical input, printer output or normal startup/registration.

The Windows-only `--verify-print-color-probe` mode checks the same bitmap probes without opening
windows or loading the app. It rejects pale-blue edge pixels mistaken for cyan and independently
removes each required preview signal. Redaction checks use the review appearance's ink and position;
the annotation's IC entry describes its applied fill, which can have a different color.

## Reference-repair completion notice

On an owned UI VM, run `--verify-reference-repair-notice APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
with a fresh output directory. The isolated English host clicks the real Protect button for pristine
and damaged synthetic PDFs. Both outputs must reject an incorrect password, reopen with known live
text, and leave their source bytes unchanged. The success modal must include its exact output path,
omit the repair notice for pristine input, and include it for repaired input.

Independent present and removed text controls validate the notice detector first. A dispatcher timer
queues the Protect click so it can observe and dismiss the nested success modal. Retain the process
receipt, both output streams, modal text and result file. This checks the native WPF workflow;
keyboard, screen-reader and other-language acceptance remain separate.

For Reduce File Size, use `--verify-reduction-repair-notice APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
on an owned UI VM. The runner uses the actual Save menu, default downsampling options and save picker.
Pristine and damaged text-only inputs must keep their source bytes and live text. The visible success
status must name the output, report its actual size and include the notice only when repairs occurred.
Retain the process receipt, streams and per-case workflow logs. This does not assess image quality.
## Document state regressions

On an owned UI VM, use `MODE APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`
with a fresh output directory. Modes include `--verify-save-history`,
`--verify-annotation-history`, `--verify-form-history`, `--verify-failed-save-history`,
`--verify-active-editor-close`, `--verify-stamp-isolation`, `--verify-stamp-history`,
`--verify-stamp-tabs`, `--verify-stamp-panes`, `--verify-stamp-tearout`,
`--verify-page-edit-failure`, `--verify-window-collection`, `--verify-layout-pending`,
`--verify-layouts-pending`, `--verify-merge-pending`, `--verify-layer-isolation`,
`--verify-stamp-saveall` and `--verify-validate-profile`.
They open synthetic PDFs through the public window entry and use real toolbar,
editor and dialog controls. Normal startup and user settings remain isolated.

Text placement uses native pointer input and close uses native Alt+F4. Annotation
nudge uses the public WPF Right-key route and checks canvas geometry, where a
one-unit move remains observable at small zoom levels. It does not certify physical
arrow-key input. Save checks strictly reopen output. The failed-save case locks
only its synthetic destination, verifies the expected error and unchanged source,
then releases the lock and retries through Save. These modes open windows and must
not run on the main PC. Stamp checks cover distinct documents, Undo/Redo, fresh
background-pane rendering, transfer of pending stamps to a new window and saving
distinct pending stamps from multiple documents during close.

The page-edit failure case denies file creation only in its newly created isolated
session folder, retains the original access descriptor and restores it in a finally
block. It verifies the error, unchanged saved state, a successful retry and saved
rotation through Undo/Redo. Window collection checks an ordinary WPF control first,
then requires three closed PDF windows to become collectible while a sibling stays
alive and changes themes. Output checks use actual production-layout or direct
Data Merge dialogs and synthetic pickers. They require visible pending text in
strictly reopened output while preserving source bytes and live dirty state.
The layer check exercises effective View overrides, sibling preservation and
strict saved Undo/Redo. The Validate profile check imports and exports macro JSON
through real controls and compares public preflight outcomes without running macros.

Profile matrix cases run in separate processes with
`--verify-validate-profile-case-KIND-ACTION`, where KIND is `general`, `print`,
`attachments` or `custom`, and ACTION is `apply` or `cancel`. Use
`--verify-validate-profile-case-invalid-apply` for malformed JSON. These retain
unrelated step settings and compare public preflight reports. The isolated owner
may stay hidden for these public-control tests; they do not certify visible layout.
`--verify-form-format` checks blur formatting/color, raw editing and strict saved
values. `--verify-form-status` checks accessible invalid/unsupported/failed feedback,
correction, and saved raw values without executing arbitrary scripts. Screen-reader
speech and other-language runtime acceptance remain separate. `--verify-form-total`
checks an edited input, recalculated total and strict saved values for both fields.

`--verify-delete-nearest` selects the final page of a numbered five-page synthetic
PDF and confirms Delete. It checks the surviving selection and page counter, then
strictly reopens Save/Undo/Redo page sets. Failure cleanup dismisses only this
process's synthetic Don't Save/Quit prompt.
Use `--verify-delete-nearest-case-KIND` for `first`, `middle`, `last`, `tail-block`,
`disjoint` or `cancel`. The loop distinguishes independently colored page bitmaps,
waits for the expected rendered survivor and checks every saved page label.
Cancel clicks No and requires unchanged content, viewport and clean history.
Append `:ViewContinuousRadio`, `:ViewTwoPageRadio` or `:ViewGridRadio` to a deletion
kind to check that view mode with actual viewport pixels and strict saved labels.
Append `:pane-b` after the mode to run in the second pane, for example
`--verify-delete-nearest-case-last:ViewSingleRadio:pane-b`. These cases also require
the first pane's source, visible page, clean state and history to remain unchanged.

`--verify-page-transform-form-failure` combines unsaved text and an independent
form-value edit before a late Transform metadata refusal. Strict Save, Undo/Save
and Redo/Save must retain both form fields while reversing only the annotation.

Navigation probes use `MODE APP_DLL APP_XAML OUTPUT_DIRECTORY DATA_DIRECTORY
--session-isolation` on an owned UI VM. `--verify-page-navigation-document-scrollbar`
checks a long document's actual hovered thumb, off-center native drag and page77
entry. The `-variants` suffix adds Light, classic98SE and maximum-scale Dark.
Classic scrollbar geometry is measured independently of modern hover widening.
`--verify-page-navigation-tooltip-cursor` compares actual tooltip glyph pixels with
the current native cursor resource and work area. `--verify-page-navigation-tooltip-matrix`
adds the More chevron, sidebar help, classic Open and maximum-scale Open, with exact
pointer-hit and tooltip-owner controls. These probes do not change OS pointer or
display settings, capture desktop pixels, or certify custom pointer/DPI coverage.
The `-large-cursor` suffix creates a synthetic cursor owned only by the test window,
checks its actual painted size, then restores the window's cursor properties.
The `-placement-controls` suffix checks the public attached behavior with owned
tooltips: non-hover and explicit placement stay unchanged, and mouse placement
restores its original rectangle and offsets after close. This checks the non-hover
guard rather than keyboard tooltip activation.

The `--verify-page-navigation-caption-theme`, `-caption-frame-theme` and
`-caption-frame-grain` modes compare an existing public dialog frame after a live
theme change with an independently constructed classic frame, then restore Dark.
They retain owned caption and frame renders and check title, geometry and grain
separately. The `-caption-close`, `-caption-pointer-close` and
`-caption-keyboard-close` modes exercise the same public caption button through
UI Automation, a guarded native click and native Space respectively. Each checks
one close callback, the localized name and preservation of explicit caller margins
in Dark and 98SE. Native variants require observed input at the owned target and
released input afterward. Use the existing six-argument page-navigation invocation
with `--session-isolation`, a fresh output directory and an owned SN01/SN02 session.

`--verify-page-navigation-dialog-buttons-theme` uses the same invocation to compare
existing secondary and accent button renders with independent fresh classic
controls, then restore Dark. Painted-pixel and temporary label-removal controls
must pass first. It uses the public button factory and never invokes an action.
The `--verify-page-navigation-dialog-button-actions` companion additionally checks
the original test-owned callbacks once after each transition and verifies that
disabled buttons reject UI Automation Invoke. It sends no native input or product
command.

`--verify-picker-navigation-recovery APP_DLL APP_XAML OUTPUT_DIRECTORY
--session-isolation` opens the actual file picker through its public owner overload.
After establishing a synthetic good-directory listing, native Enter submits a
missing child folder. The picker must restore usable rows and Accept in the last
good directory. The test closes without accepting, opening or saving a PDF.

`--verify-export-loss-review` checks actual page/content loss details and Cancel.
`--verify-export-loss-mixed` adds independent vector/image/link controls;
`--verify-export-loss-continue` checks accepted plain-text output, and
`--verify-export-lossless` checks valid JSON output without an unnecessary review.
Each mode needs its own process and fresh output directory. The synthetic link is
parsed only and must never be opened. These are public-control workflow checks,
with separate visible-layout and screen-reader acceptance.

`--verify-watch-readiness` imports and saves a synthetic OCR macro requiring German,
then checks missing-model feedback before Start in Watched folders. Its input must
contain no matching PDFs. It never runs a macro or OCR job, loads native OCR,
downloads a model or writes output. Bundled English extraction is confined to the
isolated data root.
`--verify-paragraph-refusal` checks rotated-page guidance through actual tool search.
It requires ordinary fixture candidates, refusal for the unsupported fixture and
unchanged source bytes, and uses a fresh process and evidence directory.

## Recovery, signature and text-selection regressions

Build the app and this tool first. Use an owned, unlocked lab VM for every case
below, a fresh output directory, and no other running test window. The signature
keyboard case requires SN01 or SN02; the current Japanese clipboard case requires
SN01. Pass arguments in this order:

```text
KillerPDF.UiSmoke.exe MODE APP_DLL APP_XAML FRESH_OUTPUT --startup-smoke
```

- `--verify-multiwindow-recovery` opens main panes A/B and a torn-out window,
  checks live editors or their committed unsaved annotations, then requires all
  three exact markers in recovery PDFs. It also checks unchanged originals and
  clean-tab restore settings. It does not induce a hardware crash or restart.
- `--verify-undo-storage-failure` denies snapshot creation only in its isolated
  session folder, restores the access rule, and checks real Undo/Redo retries and
  saved source preservation through the public buttons.
- `--verify-dirty-open-viewport` waits for the actual initial raster and layout,
  chooses page four and the real 200% preset, then closes and reopens the fixture.
  Page, zoom and original bytes must survive.
- `--verify-cross-mode-zoom` saves Continuous at 200%, changes the global mode
  through another document, and reopens in Single. The `-reverse` and `-twopage`
  suffixes check Single to Continuous and a mixed-size selected right page.
  The modes `--verify-cross-mode-fit-page`, `--verify-cross-mode-fit-width` and
  `--verify-cross-mode-grid` check fit and three-column grid precedence.
- `--verify-zoom-close-writer` closes the owning window before reopening through
  a new window. `--verify-cross-mode-zoom-clamp` uses the actual 50% preset and
  two Zoom Out clicks to request 20%; Single's existing internal minimum yields
  38% on this fixture. That case records the clamp rather than claiming exact
  percentage preservation. All zoom cases require unchanged source bytes.
- `--verify-cross-mode-zoom-reassert` reasserts the public Single view command
  while reopen restoration is pending. `--verify-cross-mode-zoom-navigate`
  selects page three at that same point. Each requires its timing trigger to
  fire, the requested page, restored 200% and unchanged original bytes.
- `--verify-cross-mode-zoom-wheel` raises the toolbar's real preview wheel event
  while restoration is pending. It requires page four at 220%, matching the
  independent rendered-page width, and unchanged original bytes.
- `--verify-cross-mode-zoom-button` measures one Zoom In click after Single mode
  settles at 200%, then requires the same public zoom and rendered width when
  that click occurs during pending restoration before the first raster completes.
- `--verify-cross-mode-fit-page-pending` and `--verify-cross-mode-fit-width-pending`
  compare each pending fit command with its settled public fit tag and rendered
  page width. `--verify-cross-mode-zoom-sequence` similarly measures real Zoom Out,
  Out, In clicks after settling, then compares the same pending sequence. These
  cases require a positive pending restore and incomplete initial primary render.
- `--verify-cross-mode-fit-zoom-pending` compares Fit Width followed by Zoom In
  with the same settled commands. `--verify-cross-mode-zoom-fit-pending` reverses
  those commands and requires Fit Width to win. Both compare public page geometry
  and zoom controls, preserving the source bytes.
- `--verify-cached-fit-zoom-pending` warms page three, returns to page four, then
  watches the actual primary image clear during navigation back to page three.
  Its synchronous Fit Width and Zoom In commands must match the settled control.
  It requires the null image, warmed page, positive fit request and new generation.
- `--verify-cross-mode-zoom-compare-fit` uses a real separate same-size owning
  window at Fit Page as the source for the viewer's comparison module API while
  the target is pending. It compares actual page geometry and the fit marker;
  this covers module integration, not a physical Compare UI gesture.
- `--verify-cross-mode-fit-page-twopage-pending` measures settled Fit Page on a
  mixed-size selected right page, then compares pending Fit Page in Two-Page mode.
- `--verify-two-page-fit-after-mode-switch` opens the mixed right page in
  Continuous, switches to Two-Page, then requires Fit Page to contain the actual
  spread within its public viewport. `--verify-two-page-zoom-after-mode-switch`
  instead requires the 200% preset to render the 300pt right page 800 DIP wide.
  Both wait for actual page images and stable public layout and preserve source bytes.
- `--verify-signature-keyboard` uses native G, Tab, Enter, Space and Escape, plus
  guided selection, deletion and automation semantics. It also checks ordinary
  external-open page navigation and another owned window's keyboard focus.
- `--verify-japanese-selection` needs the exact reporter attachment named
  `issue465-japanese.pdf` beside the output directory. Its SHA256 must be
  `F8A583B46B47612417BC72C677EB6BC01961E76C69BB810D89EC598D439E7C18`.
  It selects the reported phrase through the public text automation provider,
  checks screen bounds and native Ctrl+C, and restores the prior clipboard.

Native keys are sent only when the expected owned window has foreground focus.
Keep the exit result, assembly hashes and output files together. These cases do
not certify printer hardware, installers, native updater execution or a complete
screen-reader acceptance matrix.
