# Document state smoke checks

Prerequisites: Windows, the repository's .NET SDK, and a compiled KillerPDF 1.9 application.
Use only a test-owned application assembly. The runner suppresses normal application startup,
redirects settings and process temporary paths into its output folder, and creates synthetic PDFs.
It sends no operating-system input and does not attach to another application window.

Build the runner with the engine assembly from the application being checked:

```powershell
dotnet build tests/KillerPDF.DocumentStateSmoke/KillerPDF.DocumentStateSmoke.csproj -p:AppDirectory=C:/absolute/app-output -p:BaseIntermediateOutputPath=C:/Users/steve/scratch/document-state/obj/ -o C:/Users/steve/scratch/document-state/runner
```

Run one case at a time, with a fresh output directory for each invocation:

```powershell
dotnet C:/Users/steve/scratch/document-state/runner/KillerPDF.DocumentStateSmoke.dll C:/absolute/app-output/KillerPDF.dll C:/Users/steve/code/KillerPDF/App.xaml C:/Users/steve/scratch/document-state/redaction redaction --startup-smoke
```

Cases:

| Case | Public operation and assertion |
| --- | --- |
| `redaction` | Open different marked PDFs in both panes, focus the right pane, run the actual Redact macro UI, and verify SECRET disappears while KEPT-RIGHT survives. |
| `macro-source` | Verify the macro window contains current-document and batch choices with current selected, and capture its rendered state. |
| `macro-batch` | Select the batch option and verify Run opens its file picker directly. Capture the selected option. |
| `macro-rotation` | Rotate through the thumbnail context menu and run the actual Save-only macro. Reopen the output and require 90 degrees. |
| `merge-rotation` | Run the actual DataMerge macro with a synthetic CSV. Require the selected record value and the pending rotation in its output. |
| `extract-form` | Edit a real form textbox, use Extract and its picker, and require the pending value in the output. |
| `extract-rotation` | Repeat extraction after rotation and require both the pending form value and exactly 90 degrees. |
| `print-rotation` | Open actual print preview after rotation and require a landscape preview raster. |
| `print-switch` | Queue a document switch during real form-print preparation, require a visible canceled status, and reject any stale preview. |
| `print-cancel-before-prepare` | Select the existing Microsoft Print to PDF queue, request close during the preparation yield, and reject progress after the close request. |
| `print-cancel-preparing` | Request close when the first of eight small pages reports preparation progress. Require preparation to stop before the next page or sending status, even while the window fades closed. |
| `print-cancel-native` | On a leased UI VM with Microsoft Print to PDF installed, let the real print job open its native output picker. The operator cancels that picker; require the app to close Print Preview without an error dialog. |
| `flatten-cancel-staged-new`, `flatten-cancel-staged-existing` | Hold the real final staging flush through the scoped filesystem adapter, click the overlay's Cancel, then release publication. Require destination preservation, staging cleanup and a canceled status without an error dialog. |
| `flatten-staged-control`, `flatten-staged-existing-control` | Release the same final staging barrier without Cancel and require a readable saved PDF and matching saved status. |
| `flatten-unexpected-cancellation`, `flatten-cancel-recovery` | Require unrelated cancellation and failed partial-replacement recovery to remain visible as errors, preserving and naming retained recovery files. |
| `merge-failure-pane` | Drop two unreadable owned PDFs through the actual routed event, choose Merge, switch panes while its worker is pending, and require the other pane's unsaved form value to survive failure. |
| `merge-close-success` | Merge valid owned inputs in pane B, close the split while the worker is pending, and require the completed document to have a visible owning pane while pane A retains its pending edit. |
| `merge-cancel` | Cancel a pending merge through the real confirmation, preserve the previous form edit, and require extraction cleanup and unchanged sources. |
| `archive-first-save`, `archive-first-close` | Require Save As and an unsaved-close prompt for a separately opened archive PDF, preserving the form and ZIP bytes on Cancel. |
| `archive-durable-save` | Save an archived form to a durable PDF and reopen it in a fresh window with the edited value intact. |
| `archive-responsive` | Process pending dispatcher input before extraction starts, then exercise Separate and Save As. |
| `archive-cap-cancel`, `archive-tabs-cancel`, `archive-unsafe` | Exercise actual limit, many-tabs and unsafe-path prompts; preserve prior edits and remove abandoned extraction storage. |
| `archive-mixed` | Keep a PDF and image after canceling a password-required entry, close the imported tabs independently and require process cleanup. |
| `archive-async-cancel`, `archive-async-cleanup` | Keep extracted input during protected opening, cancel through the real prompt, then save and freshly reopen the prior edit. The cleanup case also requires prompt extraction removal. |
| `archive-async-empty`, `archive-async-owner-closed`, `archive-async-previous-closed` | Cancel with no previous document or after closing either owning tab. Require a usable surviving or blank session and remove abandoned extraction. |
| `archive-async-working-copy-failure` | On SN01/SN02 only, deny new files in the owned session folder, observe the working-copy error, restore and prove write access, then save and freshly reopen the prior edit with abandoned extraction removed. |
| `archive-async-empty-pdf`, `archive-async-repair-failure` | Refuse zero-page encrypted inputs through protected or repair failure, restore the prior edited document, save and reopen it, and remove abandoned extraction. |
| `archive-window-closed`, `archive-next-prompt-release` | Keep a sibling window alive while closing a pending importing window, or cancel the first member before a later password prompt. Require correct input lifetimes, cleanup, and the sibling edit's Save and fresh reopen. |
| `radio-accessibility` | Open generated Alpha/Beta radio widgets, require distinct option names on their actual automation peers, select Beta, and verify mutual exclusion. |
| `radio-name-boundary` | Require /Alpha and Alpha export values to retain distinct names on actual rendered radio peers. |
| `keyboard-pane-owner` | Tab from pane A's document into pane B's document, route F6, and require only pane B to change view mode. |
| `reading-caret` | Select a collapsed range inside the actual document, require exact caret round trip, and reject leaking that position after opening another file. |
| `reading-keyboard` | Route WPF keyboard events through the actual window, require line, character and page caret movement, and preserve text-provider positions. |
| `reading-terminal-home` | Start at the final insertion point and require Home, Up and PageUp to reach the expected line or page. |
| `reading-scroll` | Navigate 35 lines in a zoomed tall page, require a visible reading range, and verify actual UIA ScrollIntoView reveals that range again. |
| `reading-scroll-spread` | Move from a settled spread to a cold spread and require the complete short target line in the public visible ranges after its right-hand tile is created. |
| `reading-scroll-wide` | Request the last glyph of a line wider than the zoomed viewport and require that caret to remain visible. |

Reflection is limited to isolated host setup, service initialization, scoped filesystem adapters and diagnostic observations, constructing WPF's internal
drag-event arguments, and calling the public OpenFromExternal entry point on the dynamically loaded application. Tests do not invoke private
workflow methods or inject private editor collections. Workflow actions use actual WPF controls,
routed events, textboxes and dialogs. Assertions inspect generated PDFs or public UI properties.
The runner exits its own process after the verdict. Its deadline is 45 seconds for automated
cases. `print-cancel-native` allows 120 seconds for the operator to identify and cancel the
native picker; its success assertions are unchanged.

These checks establish synthetic application behavior. They do not prove physical mouse/keyboard
input, full locale/theme coverage, raster-redaction fallback, or hardware printing.
