# Changelog

All notable changes to KillerPDF are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.7.0] - 2026-08-01

1.7.0 is the split-pane release - two documents side by side in one window - plus the family file dialogs replacing every stock Windows one, a themed system menu, night mode that spares pictures, and two deep text fixes: non-Latin scripts now save correctly, and annotations land where you put them on rotated pages.

### Added
- Split pane: F10 shows two documents side by side in one window. Each pane is a card of its own, and the focused one carries an accent ring so it is obvious which pane the toolbar, sidebar and page list are acting on - click either pane to move focus. The boundary between them has a handle on each side: grab the left one to size the left pane, the right one to size the right, and neither pane can be squeezed below a readable width from either direction. F10 again closes the split, and the rail button's icon follows along - a pane pushed out when pressing it will open one, pulled back in when it will close. It is one window, not a second window - each pane has its own tab strip, while the toolbar, sidebar and page list are shared and follow whichever pane has focus. Drag a tab from one pane's strip to the other to move it across, and F10 on a maximized or snapped window splits it evenly instead of squeezing the second pane down to its minimum.
- Every Open and Save dialog is KillerPDF's own now, instead of the stock Windows one: the same themed window as the rest of the app, with a places rail, a folder tree, list/icon/details views, sortable columns, pinnable folders and recent locations. That covers opening and saving PDFs, merging, extracting pages, flatten, image export, image import, signature and certificate picking, OCR output and the zip export - including picking several files at once where that applies. Shared with the other Killer Tools apps, so the file dialog looks and behaves the same across the family.
- Install for everyone on this computer. The Install button on the portable badge now opens a proper confirmation with two choices - add a desktop shortcut (on by default, as before) and install for all users (off by default, since it needs an administrator). An all-users install puts KillerPDF in Program Files with a Start menu entry for every account, and removes the per-user copy so there is a single entry in Add/Remove Programs rather than two. Declining the administrator prompt leaves the app running portable exactly as it was. There is also a `/silent` switch that performs the machine-wide install with no interface at all, for winget, Chocolatey and RMM deployment. Matches Killendar and KillerShell. Your PDF file association is still registered for you alone, never for other accounts on the machine - the all-users install deliberately does not change what PDFs open with for anyone else.
- "Confirm before opening links" is back, on the About card beside the recent-files toggle. It asks before a link in a PDF opens in your browser, and it is off by default so links stay immediate unless you want the check. The prompt's own "Don't ask again" now simply switches the same option off, so the two can no longer disagree. The setting had no home after the Settings panel was dissolved; About is where the safety and data-hygiene controls live.
- Ctrl+Shift+W closes every tab except the current one, alongside Close Tab on the tab's right-click menu - both now line their keyboard shortcuts up in a real column instead of each item sizing its own.
- Right-clicking the title bar (or Alt+Space) shows a themed system menu that matches the app instead of the stock white Windows one. Same items, same behavior, in all ten languages.

### Changed
- Internal: the document view is now a self-contained control rather than part of the main window - the page rendering, zoom, annotations, text editing, crop, forms, links and text selection all moved into it, about 8,700 lines. Every function moved verbatim, verified line by line against the previous version, and no behavior change is intended. This is groundwork for showing two documents side by side in one window.
- The document area is a rounded, lifted card like the rest of the Killer Tools apps, instead of a squared pane running flush into the window edges: rounded corners, an 8px inset on its outer side, and a real drop shadow that falls across the status bar. The sidebar's five-dot gripper is gone, replaced by the family divider - a thin line that lights up in the accent color when you hover or drag it. Full screen still fills the display edge to edge.
- Night mode no longer inverts pictures (#135, thanks dmantisk). Photos and figures keep their real colors while the page around them goes dark - the behavior the request pointed at in Okular. Right-click the moon (or press Shift+N) for "Invert images too" if you want the old full inversion back - useful on scanned documents, where the whole page is one image. Night mode only changes what you see on screen: saving, printing and exporting still produce the document's original colors.
- The Settings panel and its gear are gone: every section moved to where the thing it configures lives, matching the rest of the Killer Tools apps. Theme and language are flyouts on new rail buttons (below the night-mode moon, with a ? button for the shortcuts overlay); the theme flyout stays open across a pick so themes can be compared.
- Toolbar appearance is a right-click menu on the toolbar itself, and it is two independent choices now instead of one list of five: icon size (small/large) and text placement (none/beside/under/text only) - so large icons with captions is finally possible. Fresh installs default to large icons with text underneath; existing installs keep their setting. Ctrl+Shift+1-6 pick the options directly.
- Internal: the codebase has been reorganized into the Killer Tools family layout - document logic in service classes, the About/CLI/OCR/search features behind controllers, the window partials under Shell/. Every moved function is verbatim and no behavior change is intended; the repo root now holds only the entry files.
- New app icon. The old one - the document with the red bar across the bottom - now marks PDF *files* instead, so a KillerPDF window and a PDF sitting in a folder are no longer the same picture. Explorer caches icons aggressively, so a PDF may keep showing the old art until the cache refreshes.
- View mode is a rail button wearing four view tiles, one per layout: click for a flyout (each mode with its F-key beside it), roll the wheel over it to step through the views, or press F9 to jog from the keyboard - F5-F8 still jump straight to one, and Ctrl+, is retired. All the new shortcuts are on the F1 overlay, in all ten languages.
- Internal: the tab strip and split-pane drag/focus model were rewritten to match KillerShell's implementation, the family's reference for both, replacing the original hand-built version.
- The sidebar's left/right choice sits at the bottom of the sidebar's right-click menu, which now opens from any part of the sidebar; Ctrl+Shift+B flips the side, pairing with Ctrl+B's collapse.
- The content pane's border is a shade lighter in every theme, so the edge between the pane and the chrome reads more clearly - the same value the other Killer Tools apps use.
- The prompt offering to make KillerPDF your default PDF viewer is translated now; it was English-only in every interface language.
- The sidebar's page list fades into the background at its top and bottom edges while there are pages scrolled past them - the same treatment KillerShell's folder tree and the killerpdf.net sidebar use. Each fade ramps in over its own height as a row slides under it, so nothing pops, and neither shows when the list is flush at that end.

### Fixed
- Japanese and other non-Latin text no longer saves as empty boxes (#168, thanks terada-d). The editor is a Windows text box, which quietly borrows glyphs from any installed font, so what you type always looks right; the save path resolved a single font and wrote a box for every character that font lacked. Two things were wrong. Nearly every CJK font on Windows ships as a collection file (Yu Gothic, MS Gothic, Meiryo, YaHei, JhengHei), and the save path could not read collections at all - so even picking a Japanese font by hand did not help. And nothing checked whether the chosen font could carry the text. KillerPDF now reads collection fonts, and when your font cannot render something it picks one that can, preferring the same faces Windows itself falls back to. Your own font is always used when it covers the text. This affects Bengali, Korean, Chinese, Thai, Arabic and Indic scripts too, and applies to page numbers and watermarks as well as placed text. Embedded fonts are subset, so a line of Japanese adds tens of KB to the file rather than the whole typeface. In the rare case that nothing installed can draw a character - two different non-Latin scripts in one box, or a script with no font on the PC at all - KillerPDF now says so when the text is placed, listing the characters, instead of leaving it to be discovered after saving and reopening.
- Punctuation shortcuts work on keyboard layouts that need Shift for those characters (#153, thanks Mr-Update). Shortcuts were matched by key position, which is a US-layout assumption: on a German keyboard "?" lives on Shift+ss and "=" on Shift+0, so Ctrl+? and Ctrl+= pressed keys the app was not listening for, and the extra Shift broke the match a second time. Zoom and the shortcuts overlay now respond to the key that TYPES the character, whatever position it occupies, so they work on German, French, Nordic and other layouts rather than being fixed one at a time. The shortcuts overlay also prints the spelling that is right for the keyboard in use instead of always showing the US one.
- Annotations and stamps are no longer burned into the wrong frame on a rotated page (#169, thanks terada-d for a report that diagnosed it down to the line). A page's rotation is deliberately kept outside the working document, so the canvas you draw on is in the rotated frame while the save path writes into the page's own unrotated one - and the save path was never told the angle. Anything placed on a quarter-turned page came out rotated 90 degrees from where you put it, offset, and scaled on swapped axes, which also squeezed text boxes narrow enough to wrap after almost every character. Stamps had the same fault, so page numbers and watermarks landed in the wrong corner and disagreed with their own preview. The rotation now travels with the burn, in the editor and in the background flatten that print uses.
- The app-size readout parked itself on the status bar. Rolling the wheel over the logo wrote "App size N%" into the footer behind a short hold, which existed so the chrome resize could not stomp the message with its own page and zoom status the same frame. When that hold expired nothing repainted the line, so the readout stayed put until the next page change, tool switch or open happened to write over it. It is transient now. Each notch rewrites the readout and restarts a five second timer, and the status line goes back to what it was showing before the first notch when the timer expires. The hold is unchanged and still only covers the same-frame stomp. If something else wrote a status after the hold lapsed the restore is skipped, so a real message is never replaced by a stale one.
- Editing a line of text could collapse it to 3pt (#163, fixed in #165, thanks Ryokoxx). The font size was read from the size written in the content stream, which is only the visual size when the text matrix does not scale - a generator that emits `/F1 1 Tf` and applies the scale through the matrix reported 1, and the replacement text hit its lower clamp. The point size is used now, falling back to the old value and then to the line-height estimate, since the point size can be zero on fonts with no usable metrics. Covered by tests that pin both spellings.
- Rotating a quarter-turned page by a few degrees no longer squashes it back to portrait (#167, thanks japsmits). The transform renders the page as you see it - rotation included - but sized the result from the page box, which is always stored unrotated, so the two disagreed on a landscape page and the result was stretched vertically to fit the old portrait shape. The page dimensions now follow the rendered orientation. Affected both page-size modes.
- Ctrl+0 and Ctrl+1 did not reset the zoom to a true 100% (#154, thanks Ryokoxx). The internal zoom level scales each page's layout box, and outside Continuous that box is the render-dimension bitmap rather than the page's natural width - so asking for 1.0 landed near 200% in Single, Two-Page and Grid. Absolute zoom requests now convert through the same display factor the zoom dropdown presets already used, so 100% means 100% in every view mode.
- A signature dropped onto a fill-in form field is no longer hidden behind it (#156, thanks Peter5164). Redrawing a page paints the annotations first and then restores the interactive field overlays on top of them, so anything placed over a field vanished underneath it. The field overlays now sit below the annotation layer; they stay clickable, since annotation visuals never intercept the mouse.
- The page-number tooltip now shows on every page in every view mode (#151, thanks Mr-Update). It was only ever set on the secondary page tiles, so Single and Continuous had none, Two-Page only showed it on the right-hand page, and Grid started at page 2.
- Text edit could not pick up a line's font when the letter data left the name blank (#166, thanks Ryokoxx). The fallback read the font name off the word, which joins its letters' names into one string ("Helvetica Helvetica Helvetica ..."), so nothing could resolve it and the edit box landed on the default font - the same result as having no fallback. It reads the letter's name only now.
- The odd/even page filter never reached the print job (#159, thanks Ryokoxx). The preview and the sheet count both read the filtered page list, but the print path re-parsed the typed range on its own and so printed every page in it. All three now walk the same list, and "print odds, flip the stack, print evens" works as intended.
- A link annotation with no /Subtype entry no longer aborts the pre-save link-border strip with a NullReferenceException mid-save - it is skipped like any other unreadable annotation. Surfaced while the scrubs moved to their service class; the old code dereferenced the missing entry before checking it.
- Picking a color with the screen eyedropper and pressing OK could silently throw the pick away - the tool then kept drawing whatever color last got through, which read as "shapes ignore the color I chose". The eyedropper opens a second modal window inside the color dialog, and closing that inner window could corrupt the outer dialog's OK/Cancel result, so a real OK came back as a cancel. The dialog now reports its committed color through its own flag instead of trusting that result. The eyedropper button also gained a proper hover and a lit armed state while the crosshair is active, and no longer shows the crosshair cursor just for hovering it.

## [1.6.6] - 2026-07-23

1.6.6 is primarily a bug fix release - the big one being form fields sitting in the wrong place on non-A4 documents - plus a tool-hotkey remap, a new Remove Password command, more menu polish, and other small keyboard and UI improvements.

### Added
- Remove Password in the Save dropdown (#149, thanks dmantisk): saves the open document back over the original with its password protection dropped - available whenever the file needed a password (or carried owner restrictions) to open. KillerPDF already strips encryption at open time because the editing pipeline cannot modify encrypted files in place, so every save has always written an unprotected PDF; this makes that behavior a visible, deliberate action, and regular saves of a previously protected file now say so in the status bar instead of dropping the password silently. In all ten languages.

### Changed
- Tool hotkey remap - the digits again mirror the toolbar left to right, with Shapes slotted in (breaks some muscle memory; the letter keys are unchanged): V = Select (the Photoshop / Illustrator / Figma convention; its old digit went to Text), 1 = Text, 2 = Highlight, 3 = Line, 4 = Shapes, 5 = Draw, 6 = Image, 7 = Signature, 8 = Crop, 9 = Transform, 0 = Stamp. The toolbar buttons reorder to match (Highlight now before Line, Shapes between Line and Draw), and the Shapes tool has a keyboard shortcut for the first time. The shortcuts overlay (both views), tooltips, and the help page follow.
- Invert document colors moved from Ctrl+I to the bare N key (night mode), freeing the conventional italic chord: Ctrl+B / Ctrl+I / Ctrl+U now toggle Bold / Italic / Underline while typing in a text box, matching the text bar's B/I/U buttons. Listed in the shortcuts overlay in all ten languages.
- Esc now steps down instead of straight out: with nothing left to cancel it first returns to the Select tool, Acrobat-style, and only a second Esc exits the app as before. And the Highlight tools' hint on a page with no text layer now points at the deliberate rectangle path - "No text here - Shapes is on 4" - in all ten languages.
- The right-click menus caught up with 1.6.5's menu polish: every item in the page, annotation, sidebar-thumbnail, and background context menus now carries its icon in the left gutter, matching the toolbar's glyph for the same action - and page rotation gets a proper mirrored CW / CCW pair.
- The page sidebar now starts collapsed when no PDF is open and opens itself when one is - an empty workspace has no thumbnails to show - and re-collapses when the last document closes. The empty page-number box and "/ –" that used to sit in the sidebar header on an empty workspace are hidden until a document is open.

### Fixed
- Interactive form-field overlays sat in the wrong place on any document that is not A4-sized - shifted down and slightly wide, worst near the top of the page, while the page itself (and every other viewer) drew the fields correctly. PdfSharpCore's page.Width getter, which the link layer touches on every render, silently converts the parsed /MediaBox array into its internal rectangle type; the field parser's array read then came up empty and fell back to a hardcoded A4 page size, so only A4 documents lined up. The field parser now reads both representations and walks the page-tree inheritance chain for /MediaBox and /CropBox. Found through the brochure: the shipped copy is A4, so the bug was invisible until a US Letter rebuild put every field about 40 points adrift.
- The Document Info shortcut label showed mojibake in Spanish, Bengali, and both Chinese interfaces - the same double-encoding repaired for Japanese in 1.6.5 (#136). All four now render their real text.
- Exported JPEGs no longer come out as black pages, and exported PNGs no longer carry a transparent background (#148, thanks Ryokoxx). PDFium leaves unpainted background pixels fully transparent; the JPEG encoder dropped that alpha channel and kept the zeroed color underneath, so any page without an explicit background - most PDFs - rendered solid black through `--to-image --format jpg` and the new Export pages as images dialog. Exports now composite over white by default, which also keeps the needless full-page alpha channel out of flattened PDFs (`--flatten` and Save Flattened). A new `--transparent` flag on `--to-image` keeps the raw alpha for PNG output when transparency is actually wanted.
- The Password Required prompt for opening a protected PDF is now a themed dialog like the rest of the app - wordmark title bar, dark card with film grain, a themed password field, and Open / Cancel buttons - instead of the stock white Windows dialog with native chrome it used to be.

## [1.6.5] - 2026-07-22

### Added
- Shapes tool: rectangle, ellipse, and free-form polygon markers, each with an optional fill. Box keeps the classic drag-a-filled-rectangle gesture the highlighter used to have; ellipse and polygon are closed outlines that move, resize, flatten, and print like any other drawing. Freeform places points click by click - click the first point (its target lights up when you are close) or double-click to close, Esc cancels, Backspace removes the last point. The tool shares the draw bar's color, size, and opacity, with a mini-shape sub-mode picker and a Fill toggle.
- Export pages as images (#132, thanks KaneLeung): a new entry in the Save dropdown renders pages to PNG or JPEG files at a chosen DPI (24-1200, default 150) with an optional page range, through the same pipeline as the CLI's `--to-image`. Pending annotations and stamps are burned in, in-app rotations are honored, and files land as `<name>-page-001.png` next to the base name you pick.
- Odd/even page printing (#134, thanks superaustingao): a new selector in the print dialog under Pages - All pages, Odd pages only, Even pages only - applied on top of the page range, with the preview following along. Print the odds, flip the stack, print the evens: manual duplex for printers without a duplexer, the Word way.
- Invert document colors (#135, thanks dmantisk): a moon toggle at the bottom of the sidebar rail (or Ctrl+I) renders the document with inverted colors for dark-mode reading - the icon lights in the accent while active, and the choice is remembered across launches. Display only: saving, printing, exporting, OCR, and the sidebar thumbnails all keep the document's true colors.
- App-wide size control for accessibility, the KillerNotes way: the title bar now shows the app icon next to the wordmark, and scrolling the mouse wheel over that logo scales the app chrome - toolbar, sidebar, and tab strip - in fine steps from 70% to 250% (or Ctrl+Shift and the +/- keys, Ctrl+Shift+0 to reset), remembered across launches. The document pane is deliberately untouched: app size and page zoom stay separate controls, so scaling the chrome never changes what the page looks like. It uses a layout scale so UI text stays sharp, and the title bar and footer stay fixed so the logo never moves out from under the cursor.
- Recent-files privacy controls (#146, thanks Bolle1987): a Clear list link on the start screen's Recent panel (matching the one already in the Open dropdown), and a "Don't remember recently opened files" toggle in the About window next to Clear all Data, where the data-hygiene controls live - turning it on also empties the existing list, so nothing about your documents persists on a shared machine. Translated into all ten languages.
- Czech (cs-CZ) localization (#138, thanks jiri-ops): the tenth interface language, a full translation following Czech Windows/Adobe conventions, in the language picker as "Čeština" - with Czech ("ces") joining the OCR language catalog, downloadable on demand like the rest.

### Fixed
- Page numbers and watermarks are now actually written into the saved PDF when they are the document's only markup (#147, thanks Mr-Update). Every save path only burned the stamp layer when the document also carried at least one annotation, so stamping a clean document - the whole point of the tool on a fresh file - produced a PDF with nothing on it.
- Stamps can be removed again (#145, thanks Mr-Update). Unchecking both Page Numbers and Watermark disabled the Apply button, so once a document had stamps there was no way to turn them off - applying with both sections off is exactly how they are cleared, and is now allowed whenever the document already has stamps.
- Crash opening the Stamp or Transform tool (and on save) on a page with a multi-line text annotation (#142, thanks TrNguyen20; root cause and fix from Ryokoxx in #144). The burn silently used justified alignment, whose draw path dereferenced the empty line-break blocks a newline produces - any font, any machine. Burned text is now explicitly left-aligned, finally matching the editor once a line wraps, and the vendored formatter skips line-break blocks (and no longer flings blocks off the page on single-word justified lines). As a separate net for genuine font failures - the typeface behind a text box resolves lazily, so a font that constructed fine can still fail at first draw on machines missing that face - the draw falls back to the stock font, then skips just that annotation, and a failed preview burn renders the page without its annotation layer instead of taking the app down.
- The pre-save signature scrub tripped a NullReferenceException on every save of a document with no form fields (a fresh blank document, most PDFs without forms) - swallowed silently in release builds, but it aborted the scrub early and broke into any attached debugger. Absent dictionary entries like a missing /AcroForm are now treated as "not there" instead of dereferenced.
- Bookmarks that point at named destinations now resolve (#143, thanks Ryokoxx). PDFs from HTML-to-PDF generators (wkhtmltopdf underneath most invoice and statement tools) write outline destinations as names looked up through the catalog, which the outline loader did not handle - Debug builds popped an assertion dialog and Release builds left the bookmark silently dead. Resolution now falls back to the same name-tree walker the link layer already uses.
- The sidebar page thumbnails, outline tooltips, and grid-view tooltips always said English "Page N" regardless of the interface language (#137, thanks jiri-ops) - the labels are now real localized strings in every language, and they update immediately on a language switch.
- Japanese: repaired a garbled Document Info shortcut label (mojibake) and tightened the About wording (#136, thanks coolvitto).
- Fresh clones build again without manual repair: an explicit .gitattributes rule keeps EOL normalization away from the vendored third_party sources (#140, thanks Ryokoxx), belt and braces on top of the earlier re-encode.
- The Shapes tool strings and the outline's "(untitled)" placeholder existed only in English and Czech - the other eight languages showed blank tooltips and labels there. All ten languages now carry the full string set, verified key-for-key against English.

### Changed
- Text selection now flows with the text (#127, thanks Ryokoxx): dragging with the Select tool tracks the actual run of characters in reading order, browser-style - across lines, paragraphs, and (in continuous view) across pages. A plain click still selects annotations, and drags that start on empty page keep the classic box select, so scans and annotation multi-select behave as before. Ctrl+A now shows real per-line selection on the page.
- Highlight, Strikethrough, and Underline follow the text the same way: drag over words and the markup hugs each line instead of laying down one rectangle. One gesture produces one grouped annotation per page - it selects, moves, deletes, and undoes as a single unit. On pages with no text layer the tools show a status hint instead of silently drawing a box; the highlight eraser keeps its rectangle.
- Black theme: the on-page selection color was a stray royal blue; it is now a readable dark green matching the theme.
- The form-field font-size stepper is now an "inline flyout" - a new style for controls that float on the document itself: a translucent rounded pill that drips down from the field being typed in, follows it through scrolling and zoom, flips above it at the bottom of the pane, and solidifies on hover. Subtle enough to sit on a legal document without being in the way, and it can no longer collide with the draw/text bars or the toolbar.
- Menu polish: dropdown items can carry icons in the gutter the check column always reserved (Save, Open, and OCR menus got them), and the OCR "Use High Quality Models" toggle now keeps the menu open, refreshing its checkmark and the per-language "(download)" labels in place.
- Tooltips now show their keyboard shortcut everywhere one exists, in all ten languages: the whole tool palette carries its single-key hint (V select, T text, H highlight, D draw, L line, I image, G signature, C crop, R transform, S stamp), and the invert and app-size controls show Ctrl+I and Ctrl+Shift+=/-/0. The shortcuts overlay's list view also caught up with the keyboard view: Ctrl+Shift+Z (redo), Ctrl+Shift+Tab (previous tab), and F2 (rename bookmark) are listed now.
- Collapsing and expanding the sidebar is now a smooth slide instead of a snap: the panel glides shut over a quarter second with the thumbnails holding their size (clipped, not squished), and the document settles in a single crisp pass afterwards - the same pipeline a splitter drag uses.

## [1.6.4] - 2026-07-17

### Added
- Full command-line interface: `--merge`, `--extract-pages`, `--split`, `--decrypt`, `--to-image`, `--flatten`, `--print`, `--ocr`, `--version`, and `--help` run headlessly with meaningful exit codes, work while the app window is open, and reuse the exact pipelines the GUI runs (merge link rewriting, pre-save scrubs, lossless PDFium decrypt, rotation-safe 150/300 dpi rasterizing, searchable-PDF OCR with on-demand language download). See the Command Line section on the help page.
- Bookmark editing in the sidebar Outline panel (#133, thanks alivio-israu): add via the row at the top of the tree (named in place), inline rename, child bookmarks, reorder, retarget, and delete - with Ctrl/Shift multi-select, Delete and F2 keys, delete all, and full Ctrl+Z undo. Hidden on read-only files.
- Redo: Ctrl+Y (or Ctrl+Shift+Z) re-applies undone actions - annotations, text edits, stamps, clears, and document-level operations alike. Any new edit clears the redo chain, and redo history is kept per tab.
- Jump history: Alt+Left / Alt+Right and the mouse back / forward buttons retrace bookmark, link, jump-box, and Home/End jumps, browser-style.
- Keyboard view in the shortcuts overlay (F1): a visual keyboard with every bound key lit and color-coded by category. Toggle LIST / KEYBOARD in the header (the choice sticks), click a layer or hold Ctrl / Shift / Alt to preview it, and hover a lit key for its action. Follows the active theme and language.
- More conventions from the big viewers: Home / End jump to the first / last page, Ctrl+1 / Ctrl+2 / Ctrl+3 set actual size / fit width / fit page, and the Menu key or Shift+F10 opens the right-click menu at the current selection (keyboard accessibility).
- Japanese OCR language (`jpn`), downloadable on demand like the rest - the OCR language list now covers the same nine languages as the interface.
- Command-line batch mode: `KillerPDF.exe --batch-resave <input> <output> [--log report.csv] [--quiet]` resaves a single PDF or a whole folder tree headlessly through the standard open/save pipeline, with per-file OK/SKIP/FAIL reporting. Built for the validation harness.
- Standards-conformance validation harness (`validation/`): `Compare-VeraPDF.ps1` diffs two veraPDF batch reports (corpus baseline vs a `--batch-resave` output tree) and flags any file whose validation outcome a KillerPDF save changed. Verifies that saving through KillerPDF does not degrade PDF/A conformance.

### Changed
- Shortcut remap: About moved from F2 to F12, and Document Info moved from F12 to F4 (Ctrl+D also works, matching Acrobat/Foxit/Sumatra's Document Properties). F2 now renames the selected bookmark in the Outline panel, the Windows rename convention. Settings gained F9 (Ctrl+, also works, the VS Code / Windows Terminal convention), and F3 / Shift+F3 step to the next / previous search match from anywhere (F3 opens search when it isn't). Pressing a dialog shortcut while the shortcuts overlay is open dismisses the overlay first. The shortcuts overlay and the help page keyboard map follow.
- Keyboard shortcut hints audited app-wide: menus now show their shortcut dimmed at the right edge wherever one exists (OCR, close tab, bookmark rename/delete, and more), the help tooltip advertises F1, and missing tooltip hints were added in all nine languages (OCR Ctrl+Shift+O, sidebar collapse Ctrl+B, grid view F8).
- Continuous view: clicking a page no longer snap-scrolls its top to the viewport. Clicks in the document are for tools and selection only, and the current page follows the viewport as you scroll - the convention the big viewers use. The sidebar, jump box, links, bookmarks, and page keys still jump as before (#128, thanks Ryokoxx).
- German translation refinements: Dokumentinfo, Zuschneidebereich for CropBox, Entf for the Delete key (thanks Mr-Update, #126).
- The sidebar tab is labeled OUTLINE (singular) in English, matching the other languages.

### Fixed
- Resaving a PDF no longer reduces its PDF/A conformance. The PDF library (PdfSharpCore, MIT) is now vendored under third_party/ with six patches: no Producer/Creator stamping into an imported document's Info dictionary, no /ModDate rewrite at open, no transparency /Group injected into every page, stream /Length now always matches the spec's byte count (empty streams included), boolean values written as the spec's lowercase true/false keywords, and the debug-only verbose file layout removed. Found by the new veraPDF validation harness across a 2,900-file corpus.
- Intermittent hard crash (native heap corruption) while scrolling or clicking through a document, most visible on annotation-heavy pages: KillerPDF's direct PDFium calls (link extraction, encryption stripping) could land at the same moment as a background page render inside PDFium, which is single-threaded. Every direct call now holds the same lock the render path uses. Diagnosed from a 1.6.3 crash dump showing two threads inside PDFium at once.
- Saving a PDF that carries a digital signature kept the old signature value even though any edit breaks its digest (which must cover the entire file), so strict validators rejected the result. Saves now strip dead signature values and the matching /Perms entry; the signature fields themselves are kept.
- Saving over the open file failed with "being used by another process" on PDFs whose pages carry annotations but no links readable by the primary parser (typically fillable forms): the cached PDFium link handle was holding the file open. It is now released before every save (#129, thanks Peter5164).
- Opening a PDF whose page tree parses to zero pages crashed Continuous view with an out-of-range page index; it is now guarded (#130, thanks demo1866).
- Bookmark titles in password-protected PDFs showed as mojibake (a stray BOM prefix followed by garbled characters) instead of their Unicode text - most visible on Chinese outlines. Titles the parser hands over raw are now re-decoded for display (#133, thanks alivio-israu).
- Grid view never tracked the current page while scrolling, so the statusbar counter, the page jump box, and the page a new bookmark targets could all point at a page long since scrolled away. Grid now follows the tile nearest the viewport center, like Continuous.

### Security
- Image codec library SixLabors.ImageSharp updated from 1.0.4 to 2.1.13, clearing all seven published advisories against the old version (denial-of-service and out-of-bounds issues in image parsing). Image import, clipboard paste, and signature images all pass untrusted files through this library.

## [1.6.3] - 2026-07-12

### Changed
- Links open directly again: the confirm-before-opening prompt and its Settings row are off for now.
- When both document scrollbars are visible, the vertical bar now runs the full pane height and owns the corner.

### Fixed
- Switching from Grid to Continuous view kept the grid's scrollbar overrides, clipping zoomed pages with no horizontal scrollbar. Continuous now restores its own scrollbar setup.
- Closing with unsaved changes stacked two prompts. Confirming "close without saving" now counts as the quit confirmation, and the prompt defaults to No so a stray Enter can't discard new work.
- Saving any PDF whose pages had no crop box silently planted a zero-size /CropBox on every page, which Adobe rejects with a "page dimensions out-of-range" error - the real reason merged Google Docs exports failed in Acrobat but opened in Chrome. Page boxes are now read without touching the document, and every save strips degenerate crop boxes, so re-saving a file damaged by 1.6.x heals it (thanks Richard Lam).
- The quit prompt no longer appears when no documents are open - an empty window just closes.
- Saving any PDF that has no bookmarks silently corrupted the file's structure (a dangling /Outlines reference). Strict viewers refused the file with a repair prompt, and the repair stripped fillable forms. Saves are now clean, and the repair path first tries a lossless PDFium re-save that preserves forms and bookmarks, so files damaged by older builds recover intact (#103, thanks Peter5164).
- Two-Page mode: arrow keys, PgUp/PgDn, and the wheel's edge page-flip now move one spread at a time instead of one page (#120, thanks eddardburger).
- Selection boxes drawn with the Select tool could get stranded on screen until the app was restarted. They are now removed from the layer they actually live on, and closing a file sweeps any stragglers (#121, thanks TaBnLd).
- High memory use on large documents (#122, thanks RoyYang567): the per-tab page-bitmap cache is now capped to a window of pages around the viewport, closing a tab compacts the heap so RAM visibly drops, and Continuous view only holds bitmaps for pages near the viewport - a 243-page image-heavy PDF now costs a few hundred MB instead of climbing past 7 GB.

## [1.6.2] - 2026-07-11

### Added
- Page Up / Page Down navigate to the previous / next page regardless of what has focus. Page reordering stays on the toolbar Move Up / Move Down buttons (#117).
- Japanese (ja-JP) interface translation, selectable from the language picker (#118, thanks coolvitto).

### Changed
- Footer/status bar tightened to match the killerpdf.net statusbar: 4px shorter with larger text, and the corner grip dots now stay visible when the window is maximized or snapped.
- Ctrl+scroll zooming is smooth: each wheel notch zooms by a constant 10% ratio, the view scales instantly while the wheel is moving, and the crisp high-resolution re-render happens once when the wheel rests. Precision touchpads glide proportionally.
- Up / Down arrows now scroll the view like the mouse wheel, flipping pages at the top or bottom edge. Left / Right and PgUp / PgDn remain hard page jumps.
- Status-bar and dialog messages that were still shown in English now follow the selected language across all nine locales.

### Fixed
- Switching view modes now cross-fades instead of cutting instantly, with no intermediate-frame flashes.
- The in-app self-updater now reads `SHA256SUMS.txt` from the release assets instead of the repo at the release tag, so the hash can no longer drift from the binary and fail the update's checksum.
- Importing images with broken DPI metadata (common in WhatsApp photos and some scans) produced pages Adobe Reader refuses to display; imported image pages are now kept within Adobe's supported 3-14,400 point range (thanks Richard Lam).
- Saving a document that already contains out-of-range pages now offers to scale them to a supported size; the pages keep their look and proportions.

## [1.6.1] - 2026-07-01

### Added
- On quit with documents open, KillerPDF asks whether to reopen them next launch, with a "remember my choice" option (#105).
- Enter and Esc now confirm and cancel dialogs (#111).
- Right-clicking the Open, Save, and OCR toolbar buttons opens their dropdown menu (#109, thanks Ryokoxx).
- Copies and custom Scale in the print dialog are numeric fields with an up/down spinner, arrow-key and wheel stepping (#109, thanks Ryokoxx).
- The print dialog remembers the last printer, orientation, color, and two-sided choice (#109, thanks Ryokoxx).
- Improved German translation (#114, thanks Mr-Update).

### Changed
- Mouse wheel scrolling is faster in all view modes and the page sidebar.

### Fixed
- Continuous view stays sharp when zooming in and on high-DPI displays; visible pages re-render at a higher resolution (#85).
- Open menu: the remove (X) button on each recent-files entry was clipped off the right edge of the dropdown; it now stays inside the frame.
- Crash when saving a freshly merged or imported PDF (#112).
- Save failing with "Cannot retrieve stream length"; the file is now recovered automatically (#106).
- Startup crash on older Windows 10 / .NET Framework builds (#101).
- Toolbar dropdown carets (Recent files, Save, OCR) missing on Windows 10 (#104, #108, thanks again Ryokoxx).
- Extra copy when printing multiple copies on some printers (#83, #107).

## [1.6.0] - 2026-06-27

### Added
- Tabbed documents: open several PDFs at once, each restoring its page, zoom, and view mode. Drag tabs to re-order.
- OCR built into the single exe (Tesseract): OCR a whole page or a dragged region to the clipboard, Make Searchable PDF (an invisible text layer over the scan), and Extract All Text to a .txt or .md file. A language picker downloads extra languages on demand, with an optional high-quality model toggle.
- Digital signatures with a cloud certificate (Certum SimplySign): reusable signatures and initials, click-to-sign form fields, and a movable Signatures popup that remembers its position.
- Transform tool: rotate in 90-degree steps or by a fine angle, scale, flip, and straighten a crooked scan by drawing a line along anything that should be level, all with a live preview. Annotations on the page follow the transform.
- Annotation tools: Line tool plus refreshed draw and highlighter bars, each with its own color, opacity, and width; resizable, word-wrapping text boxes (double-click to re-edit) with an optional whiteout background fill.
- Select tool moves and resizes any annotation, Shift+click to multi-select, marquee-selects across page boundaries, and reopens an annotation's bar to restyle it in place.
- Full RGB color picker on every swatch row: saturation/value square, hue strip, RGB/hex inputs, a screen eyedropper, and an editable palette.
- Print options: scale, position, margins, pages per sheet, color / black-and-white, and two-sided.
- Page-number stamping from the right-click menu (start value, format, position, size) as one undo.
- Drop a folder or .zip archive onto the window to open the PDFs and images inside, choosing to merge them into one PDF or open each in its own tab.
- Document Info dialog (F12): view and edit a PDF's title, author, subject, keywords, and creator metadata.
- Recent files: a dropdown by Open (last 10) and on the start screen, plus a Save / Save As dropdown; each entry carries its real Windows file-type icon.
- Keyboard shortcuts for tools, views, and panels (F1 shortcuts list, F2 About, Ctrl+V paste, Esc to close, F5-F8 view modes, F11 fullscreen...); the overlay lists them all and links to the full online guide.
- Full-screen mode (F11): hides all chrome so only the document fills the monitor, with a black fade in and out.
- Per-field font size while filling text fields, baked into the saved PDF.
- One-click update from the About dialog when a newer release exists.
- Toolbar style picker: small or large icons, text beside, under, or only.
- Sidebar is resizable and can be placed either left or right, with the collapse toggle, splitter, and Settings flyout mirroring to match.
- Accent colors (red, orange, green, teal, blue, purple) for the Dark, Light, and Black themes, each remembered independently.
- "Clear all Data" link in the About window to wipe settings, downloaded OCR language models, and temp files.
- Bengali, Turkish, Simplified Chinese, German, and French translations (contributors akib-h #79, mrantikadev #76, KaneLeung #82, Dtrieb & Gevlug #93, Thalis-fr #95).

### Changed
- Visual refresh: new logo, wordmark, app and PDF-file icons, fonts, and colors throughout.
- Blood, Greed, and Cyanotic use darker chrome with a lighter document pane; the signature windows are fully themed and reload on theme change.
- Settings is now a slide-out accordion (Language, Theme, Toolbar, View Mode, Sidebar) that stays open after a pick.
- Crop tool rebuilt as a single docked, slidable bar matching the annotation bars.
- Text-over-text editing drops an opaque cover (fill sampled from the page) with an editable box on top; the pair can be unpaired, and image-only pages get a manual cover and box.
- Unified the page-rendering pipeline so annotations, search highlights, and tools behave identically across Single, Continuous, Two-Page, and Grid views.
- Grid and Two-Page pages render sharper on high-DPI displays.
- Restored sessions load tabs lazily, and placed images no longer re-decode while being dragged.
- Save Flattened opens the source PDF once instead of per page (Issue #68).
- Internal refactor: the ~15,000-line MainWindow code-behind split into ~40 focused partial-class files, no behavior change.

### Fixed
- Prints now rasterize at a true 300 DPI instead of the preview's ~140, so output is sharp; the preview itself renders lighter and only the pages being printed are re-rendered at full resolution, keeping memory in check on large files (Issue #83).
- Printing and Save Flattened no longer crash on documents PdfSharpCore can't reopen; they use the same repair fallback as Save.
- Opening an encrypted PDF or repairing a damaged one runs on a background thread instead of freezing the window.
- A manually-closed PDF no longer reopens on next launch (Issue #75).
- Form fields appear and fill in every view mode, align on pages with an inset CropBox or offset origin, and size their text from the field's own /DA.
- Grid view: the wheel keeps scrolling after a zoom or column change, page jumps fit correctly (Issue #78), and annotations commit to the page they were drawn on.
- Undo removes one item per press; a held Ctrl+Z no longer fires several at once.
- Clear All Annotations clears every view mode as one undo; right-click Clear Page Annotations targets the correct page.
- Search waits for a pause in typing before running; the Outlines panel scrolls and no longer auto-expands every branch.
- Pressing Esc during a long OCR, repair, or flatten operation asks whether to cancel instead of closing the window.

## [1.5.1] - 2026-06-14

### Fixed
- PDFs that opened fine in browsers and Acrobat/Foxit but failed in KillerPDF with "Unexpected EOF" now open. PdfSharpCore rejected them during parsing; KillerPDF now falls back to re-saving the file losslessly through PDFium (which reads them) and opening that copy (Issue #72).
- Files opened from UNC / network shares (including the WSL `\\wsl$` filesystem) are now copied to a local temp before opening, avoiding partial-read failures on network filesystems.
- Grid view now renders every page, and tiles stream in progressively as they render instead of blocking until the whole document is done. Grid was previously capped at the first 26 pages, so longer documents stopped loading partway through.
- Ctrl+Scroll in grid view no longer re-renders every page when the zoom is already at its limit (the column count cannot change), which made large documents reload pointlessly.
- Lowered the minimum zoom from 10% to 5% so grid view can pack more columns (useful for wide/landscape pages) and single-page view can zoom out further.
- Removed a stray horizontal scrollbar (a thin green line) that appeared across the bottom of grid view; grid fits its columns to the window and no longer scrolls sideways.

### Changed
- Save Flattened PDF now rasterizes across multiple CPU cores. PNG encoding runs in parallel; the PDFium render step is serialized because the library is not thread-safe. Large documents flatten faster and the UI stays responsive (Issue #68).

## [1.5.0] - 2026-06-14

### Added
- Localization support (Issue #53 / contributor leox243). Language selector in Settings panel. Ships with English (en-US), Spanish (es), and Traditional Chinese (zh-TW). Theme names, zoom dropdown, fit-mode status, and keyboard shortcut overlay all update with the selected language. Contributor guide at `Strings/TRANSLATING.md`.
- Continuous scroll view mode. Opens all pages in a single vertical strip with progressive async rendering. Page number and sidebar thumbnail track automatically as you scroll.
- Two-page view mode. Displays two pages side-by-side (primary + one secondary). Editing tools are available in this mode.
- Re-edit placed text by double-clicking it with the Select tool. The text re-opens with its current content, size, and color; the size dropdown and color swatches restyle it live while editing.
- Per-monitor DPI v2 support. Window and page re-render correctly when dragging between monitors with different scale factors.
- Zoom +/− toolbar buttons and keyboard shortcuts (Ctrl+=, Ctrl+−, Ctrl+0, Ctrl+Scroll).
- Crop tool improvements (Issue #15): editable CropBox coordinates, page range apply, TrimBox sync, rotation-aware coordinate conversion, draggable confirm bar.
- Settings persistence - window size, zoom, and fit mode saved/restored on launch (Issue #69).
- Global crash handler with structured log files and recovery dialog.
- About dialog (click the version label in the status bar).
- Authenticode install gate, downgrade protection, and pdfium.dll integrity check.
- Theme system: Dark, Light, High Contrast, Blood, Greed, and Cyanotic themes with live switching and settings panel (gear icon)
- Grid view zoom fits a whole number of pages across the window. Ctrl+Scroll steps through column counts (3, 4, 5 and up) and the grid opens at three pages across.
- Built-in print dialog with working print preview. Replaces the Windows print dialog (which showed "This app doesn't support print preview") with a themed dialog that previews each page and exposes printer, orientation, copies, and page-range (for example 1-3,5) settings.

### Changed
- Continuous scroll is now the default view mode for new installs.
- View mode order in Settings: Continuous, Single Page, Two-Page, Grid.
- Settings and keyboard shortcut overlay borders widened to 2px for better visibility.
- Text tool size value is now interpreted as points. A size of 14 renders and exports as roughly 14pt instead of about 5pt of internal render units.
- Placing an image now switches to the Select tool with the image selected, so you can immediately drag to reposition or use the corner handle to resize instead of the next click reopening the image picker (matching signature placement).
- Extracted SignatureStore and SearchService into Services/ with unit tests (KillerPDF.Tests).
- Encrypted PDF temp files written to `%LOCALAPPDATA%\KillerPDF\Temp\` instead of `%TEMP%`.
- Reopens last file on startup; ESC closes the app when no overlay is active (Issue #69).
- Grid view mode moved from a toolbar toggle to the Settings panel alongside Theme and Language. Four modes: Single Page, Continuous, Two-Page, Grid. Selection persists across sessions.
- Switching to Single or Two-Page view fits the page to the window, Continuous opens fit-to-width, and Grid opens at its column-fit default, rather than carrying the previous mode's zoom level.
- Annotation toolbars (text and draw size/color) now appear at the top-right under the toolbar buttons instead of the top-left.
- Four corner resize handles on placed images and signatures. Drag any corner to resize with the opposite corner held fixed. Handles are larger and render at the same on-screen size in every view mode.

### Fixed
- Stale debug string appearing in status bar after Fit Width in single-page mode.
- Text edit box closed when changing the font size, because the size dropdown took keyboard focus and triggered a commit. Focus moving into the size or color bar no longer commits the edit.
- Crop confirm bar was scaled down with page zoom, making it unreadable at low zoom levels. Selection rectangle improvements.
- Save Flattened PDF now runs on a background thread (Issue #68).
- Cropped pages rasterize at CropBox size instead of document-wide maximum (Issue #68).
- Temp files cleaned up on close, crash, and startup.
- Undo of a document change (crop, rotate, page operations) now re-renders the active view, so a page no longer keeps showing its pre-undo state while the sidebar shows the correct version.

---

## [1.4.3] - 2026-06-08

### Fixed
- Encrypted PDFs (owner-restricted RC4) no longer fail with "Unexpected token 'xref'" when rotating pages. PdfSharpCore can silently produce a broken cross-reference entry after saving encrypted files; KillerPDF now pipes the file through PDFium to repair the XRef and retries the open automatically.
- Page view now fits to page after a rotation so the full rotated page is visible without manual rezoom.
- Mailto and other link annotations with visible borders (e.g. colored rectangles that looked like strikethroughs) no longer render those borders in saved PDFs. KillerPDF strips `/AP`, `/C`, and `/BS` from link annotations and sets an invisible border on save.
- Right-click a link annotation to remove it from the PDF entirely ("Remove Link from PDF"). Previously, clearing annotations only removed the KillerPDF overlay; the native PDF link remained active.
- Right-click a mailto link to copy just the email address; right-click an http/https link to copy the URL.

---

## [1.4.2] - 2026-06-06

### Added
- PDF form filling. Interactive PDF forms now render their fields (text inputs, checkboxes, radio buttons) as live controls. Fill them in directly and save - field values are written back into the PDF.
- PDF outline (bookmark) support (Issue #63). A new OUTLINES tab in the sidebar displays the document's bookmark tree. Click any entry to jump to that page. The sidebar auto-fits its width to the longest entry on open and can be dragged wider; switching back to PAGES snaps to the pages-mode width.

### Fixed
- Page rotation no longer reverts after saving. Rotations applied via the sidebar context menu now persist correctly through the save pipeline.
- Copied text words were out of order on PDFs where glyphs are stored in non-reading order (Issue #66). Text extraction now sorts words by position and uses a dynamic line-grouping threshold so both drag-select and Select All produce correctly ordered output.
- PDFs with malformed or non-standard XRef tables now open in read-only mode instead of showing "Invalid entry in XRef table" and failing entirely.

---

## [1.4.1] - 2026-05-21

### Added
- Page number jump box in toolbar. Type a page number and press Enter to navigate directly to that page.
- Signature auto-selects after placing so you can immediately reposition or resize without switching tools.
- Zoom to Width / Fit Page now re-applies when the window is resized.
- Middle mouse button panning. Hold middle mouse and drag to pan the view in any direction.
- Multi-page grid view toggle (toolbar button left of the zoom dropdown). Switch between seeing all pages in a scrollable grid and a focused single-page view. Defaults to grid view on open.
- Ctrl+S saves directly to the current file without a dialog. Ctrl+Shift+S opens Save As.
- Arrow key navigation: Left/Up goes to the previous page, Right/Down goes to the next page.
- Keyboard shortcut overlay. Press Ctrl+? to show a full shortcut reference. Dismiss with Escape or by clicking outside the panel.
- Crop tool improvements: corner drag handles to resize the selection after drawing without having to redraw; Enter applies the crop to the current page; Escape cancels; Remove Crop / Remove All buttons in the confirm bar clear an existing CropBox from one page or all pages.

### Fixed
- Fit to Width and Fit Page zoomed incorrectly on HiDPI (4K) displays.
- Pages appeared blurry at higher zoom levels on HiDPI displays.
- Signature position drifted after saving.
- Memory spike (6+ GB) when opening large PDFs on HiDPI displays.
- Navigating pages caused multi-second UI lag on documents with many pages.
- Scroll wheel now navigates to the previous page when scrolled to the top of a page, and to the next page when scrolled to the bottom.

---

## [1.4.0] - 2026-05-16

### Added
- Rotate page (Issue #52). Right-click any page in the sidebar to rotate it 90° clockwise or counter-clockwise. Works on multi-page selections.
- Insert Image tool (Issue #50). Click the toolbar button, then click anywhere on the page to place a PNG, JPG, BMP, GIF, or TIFF as a resizable annotation. Drag the green corner handle to resize; burned into the PDF on save.
- PDF link annotation support (Issue #47). Clicking hyperlinks and internal cross-references in a PDF now navigates to the target page or opens the URL in the default browser. Works on both the primary page and all secondary pages in multi-page grid view.
- New Blank Document (Ctrl+N, toolbar button). Creates a single blank A4 page as a new working document. Prompts to discard unsaved changes if a dirty file is open.
- Typewriter tool font size picker. When the Text tool is active, a settings bar appears showing size presets (8-72pt) and a color palette. Size and color are stored per-annotation and applied when flattening to PDF.
- Insert Blank Page. Right-clicking any page in the sidebar now shows a context menu with page-level operations: insert a blank A4 page, move up/down, extract, or delete.
- Signature resize. Placed signatures now show a green drag handle in the bottom-right corner. Dragging it scales the signature proportionally; releasing commits the new size.
- Multi-page grid view. When viewing a page, subsequent pages render as a tiled grid to the right and below, allowing context across multiple pages at once.
- Fit to Width on open. Files now auto-zoom to fill the viewer width on open instead of opening at 100% and clipping wide pages.

### Fixed
- Scroll wheel in the main viewer no longer triggers page navigation. Previously, at low zoom levels where the page fit entirely in the viewport, every scroll tick caused a full page re-render.
- Page selection no longer flashes centered before jerking left. The layout width is now managed exclusively in the Dispatcher callback, eliminating the double layout pass that caused the visual artifact.
- "Back to TOC" and other internal links on secondary pages now navigate to the correct target instead of advancing to the next sequential page.
- Clicking an internal link now scrolls the viewer back to the top of the target page so links pointing to page tops (e.g. TOC back-links) land correctly.
- Internal PDF links now survive a merge. When merging PDFs, named destinations from the source document's catalog are resolved and rewritten as explicit page-object references in the merged document, so TOC and cross-reference links continue to work after merging.
- Multi-page grid content is now centered in the viewport instead of left-aligned. Panel width is snapped to a whole number of page-width slots so HorizontalAlignment=Center has room to work.
- Sidebar page list no longer shows empty space after the last page. The list now ends at the final page entry with no trailing dead zone.

### Changed
- Theme updated to match killertools.net: accent green changed from `#4ade80` to `#1ea54c`, backgrounds shifted to `#333333`/`#3a3a3a`, sidebar darkened to `#222222`, toolbar and title bar at `#222222`. Film grain overlay added to the main content area. Footer text lightened for readability.
- Sidebar scroll is now handled by an outer ScrollViewer wrapping the page list, allowing the list to size to its content rather than stretching to fill the panel height.

## [1.3.2] - 2026-05-11

### Fixed
- Windows Program Compatibility Assistant popup on first launch. Added an app manifest declaring Windows 10/11 compatibility, which suppresses PCA when the app writes to uninstall registry keys.
- "Set as default PDF viewer" prompt now only appears if KillerPDF is not already the default handler. Previously showed on every install/update regardless.
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
- Self-installing EXE. Running the downloaded binary now shows an Install / Run dialog. Install copies the EXE to `%LOCALAPPDATA%\Programs\KillerPDF\` (no UAC required), creates Start Menu and optional Desktop shortcuts, registers as a PDF file handler, and adds an uninstall entry to Add/Remove Programs. Uninstall self-deletes via a deferred batch file. Running a newer version from outside the install path shows an Update prompt instead.
- Command-line file argument support so file associations work: `KillerPDF.exe "file.pdf"` opens the file directly.
- Password-protected PDF support. Opening an encrypted PDF now prompts for the password instead of showing a generic error. The decrypted copy is held in a temp file for the session so all rendering and editing works normally.
- Save Flattened PDF (photo icon in toolbar). Rasterizes every page at 150 DPI via PDFium and writes them as embedded images into a new PDF, producing a fully uneditable document. Pending annotations are burned in before rasterization.

## [1.1.1] - 2026-04-18

### Fixed
- Maximize no longer covers the Windows taskbar. Added a `WM_GETMINMAXINFO` hook so the frameless window clamps to the monitor's work area (multi-monitor aware).
- Two `CS8602` nullability warnings in the font-name cleanup path.

## [1.1.0] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Replaced `Math.Clamp` calls with `Math.Min`/`Math.Max` equivalents.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

