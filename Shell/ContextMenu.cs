using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Context menu
        // ============================================================

        private void TrackGrainBrushes()
        {
            // The family grain tile (Controls/GrainTexture.cs). Publishing it also replaces the
            // app-level GrainTileBrush that the flyouts and standalone windows resolve, and every
            // tracked brush is repainted when the theme or accent changes.
            // BOTH panes by name, not through GrainBrush - that member resolves to ActiveViewer, so
            // only the focused pane ever got the texture and pane B rendered flat.
            Controls.GrainTexture.Track(Viewer.Grain);
            Controls.GrainTexture.Track(ViewerB.Grain);
            // TryFindResource, NOT the Resources indexer: the indexer looks ONLY in this window's
            // own dictionary and returns null without complaint if the key lives higher up.
            // A key defined above this window (GrainBrushShared can be) is invisible to the
            // indexer, and the failure is silent - the grain simply never gets its texture.
            // TryFindResource walks window -> app, so it works wherever the key is defined.
            if (TryFindResource("GrainBrushShared") is ImageBrush sharedGrain) Controls.GrainTexture.Track(sharedGrain);
            Controls.GrainTexture.Track(StatusGrainBrush);
        }

        /// <summary>Generated film-grain tile, exposed so secondary windows (e.g. the
        /// print preview) can paint the same texture over their document area.</summary>
        public ImageSource? GrainTextureSource => GrainBrush?.ImageSource;

        // One shared ContextMenu instance; its items are rebuilt on every open to match whatever is
        // under the cursor (a specific annotation -> per-type actions; empty page -> page-level actions).
        private ContextMenu _ctxMenu = null!;

        // A closed restriction notice still leaves the owner password one right-click away.
        private void AddOwnerPasswordItem(ContextMenu menu)
        {
            if (!IsOwnerRestricted || _restrictedByCertificate
                || menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, nameof(UnlockOwnerRestrictions)))) return;
            var unlock = MakeMenuItem(Loc("Str_Restricted_EnterPassword"), (_, _) => UnlockOwnerRestrictions(), glyph: "");
            unlock.Tag = nameof(UnlockOwnerRestrictions);
            menu.Items.Add(unlock);
            menu.Items.Add(new Separator());
        }

        private void BuildContextMenu()
        {
            _ctxMenu = MakeThemedMenu();
            // BOTH panes' primary canvases (2026-08-15): `_annotationCanvas` here bridged to the
            // ACTIVE viewer, which is pane A at startup - pane B's canvas never got the menu and
            // right-click did nothing there. One shared menu instance is fine: WPF opens it
            // against whichever element raised it.
            Viewer.AttachContextMenuExt(_ctxMenu);
            ViewerB.AttachContextMenuExt(_ctxMenu);
        }

        // Rebuild the shared context menu for a right-click at canvas point pt on the given page. If an
        // annotation sits under the cursor it is selected and gets a menu tailored to its type; otherwise
        // the page-level menu (tools, rotate, stamp, undo, clear) is shown.
        internal void PopulateContextMenu(Point pt, int pageIdx, PageAnnotation? target = null)
        {
            _ctxMenu.Items.Clear();
            // One menu serves every right-click on the page: markup gets the card, everything else the plain menu.
            if (TryFindResource(typeof(ContextMenu)) is Style plain) _ctxMenu.Style = plain;
            AddOwnerPasswordItem(_ctxMenu);
            // The keyboard path names the selected mark: a stroke need not pass through its own center (r2 BF-009).
            var hit = target ?? AnnotationAt(pt, pageIdx);
            if (hit is not null)
            {
                // Target this annotation: select it unless it's already part of the current selection
                // (so right-clicking one of several selected items doesn't collapse the multi-selection).
                // A grouped annotation selects its whole group so the menu acts on the group.
                if (!ReferenceEquals(hit, _selectedAnnotation) && !_selectedSet.Contains(hit))
                {
                    if (hit.GroupId.Length > 0)
                        SelectGroup(hit);
                    else
                    {
                        ClearSelection();
                        RenderAllAnnotations(pageIdx);
                        SelectAnnotation(hit, AnnotBounds(hit));
                    }
                }
                if (hit is ImageAnnotation picture && hit.GroupId.Length == 0 && SelectionCount() <= 1) AddPictureCard(picture, placed: true);
                else AddAnnotationCard(hit);
            }
            // Tiled views (continuous/grid/two-page) have no clickable link overlay, so resolve a link
            // under the cursor by bounds-check and offer the same menu the single-page overlay carries.
            else if (LinkAt(pt, pageIdx) is { } lk)
                AddLinkMenuItems(_ctxMenu, lk.target, lk.annotIndex, pageIdx);
            else if (ActiveViewer.TrySelectSourceImageExt(pageIdx, pt) && ActiveViewer.SelectedSourceImageExt is { } sourcePicture)
                AddPictureCard(sourcePicture, placed: false);
            else AddPageMenuItems(pt, pageIdx);
        }

        // Topmost draggable annotation under pt on the given page, or null. Mirrors the click-select loop
        // (reverse order = topmost first) so the menu targets exactly what a left-click would select.
        private PageAnnotation? AnnotationAt(Point pt, int pageIdx)
        {
            if (pageIdx < 0 || !_annotations.TryGetValue(pageIdx, out var list)) return null;
            for (int i = list.Count - 1; i >= 0; i--)
                if (IsDraggable(list[i]) && HitTestAnnotation(list[i], pt, out _)) return list[i];
            return null;
        }

        // Link under pt on the given page, resolved by the same padded bounds-check the click/hover use.
        // Tiled views (continuous/grid/two-page) have no clickable link overlay, so _continuousLinks is the
        // source of truth there; single-page links carry their own overlay ContextMenu and never reach here.
        private (object target, int annotIndex)? LinkAt(Point pt, int pageIdx)
        {
            if (!_continuousLinks.TryGetValue(pageIdx, out var links)) return null;
            const double pad = LinkHitPad;   // matches the click/hover pad so the menu targets the same links
            foreach (var l in links)
                if (pt.X >= l.Cx - pad && pt.X <= l.Cx + l.Cw + pad &&
                    pt.Y >= l.Cy - pad && pt.Y <= l.Cy + l.Ch + pad)
                    return (l.Tag, l.AnnotIndex);
            return null;
        }

        // A short human label for the kind of annotation right-clicked, shown as a disabled menu header.
        private static string AnnotationKindLabel(PageAnnotation a) => a switch
        {
            CoverAnnotation => Loc("Str_Annot_Cover"),
            TextAnnotation => Loc("Str_Annot_TextBox"),
            InkAnnotation => Loc("Str_Annot_Drawing"),
            SignatureAnnotation => Loc("Str_Annot_Signature"),
            ImageAnnotation => Loc("Str_Annot_Image"),
            HighlightAnnotation { IsShape: true } => Loc("Str_Annot_Drawing"),
            HighlightAnnotation h => h.Style switch
            {
                HighlightStyle.Strikethrough => Loc("Str_Annot_Strikethrough"),
                HighlightStyle.Underline => Loc("Str_Annot_Underline"),
                _ => Loc("Str_Annot_Highlight")
            },
            _ => Loc("Str_Annot_Item")
        };

        // Page-level menu shown when the right-click lands on empty page space (no annotation under it). Its top row
        // carries Back, Forward, Undo, Redo, Print and the page; a text selection and the place-here tools are icon
        // strips; the page tools sit under one Page submenu, the same tools the thumbnail card offers. Every item and
        // shortcut is the one the old flat list had.
        private void AddPageMenuItems(Point pt, int pageIdx)
        {
            if (ActiveViewer.NativeDocumentRef is not null) { AddNativePageMenuItems(pageIdx); return; }
            bool hasSelection = SelectionCount() > 0;
            bool hasTextSel   = !string.IsNullOrEmpty(_selectedText);

            AddNavRow(_ctxMenu, pageIdx);

            // A flowing text selection can be copied, marked up or redacted right here, the way the Highlight
            // tools would mark it, without switching tools first.
            if (ActiveViewer.HasTextSelectionRangeExt || hasTextSel)
            {
                var text = new List<(string, string, string?, Action, bool, bool)>();
                if (hasTextSel) text.Add(("\uE8C8", Loc("Str_Ctx_Copy"), "Ctrl:C", CopySelectedText, false, true));
                if (ActiveViewer.HasTextSelectionRangeExt)
                {
                    text.Add(("\uE7E6", Loc("Str_Lbl_Highlight"), null, () => ActiveViewer.AnnotateTextSelectionExt(EditTool.Highlight), false, true));
                    text.Add(("\uE8DC", Loc("Str_Lbl_Underline"), null, () => ActiveViewer.AnnotateTextSelectionExt(EditTool.Underline), false, true));
                    text.Add(("\uEDE0", Loc("Str_Lbl_Strike"), null, () => ActiveViewer.AnnotateTextSelectionExt(EditTool.Strikethrough), false, true));
                    text.Add(("\uED1A", Loc("Str_Lbl_Redact"), null, ActiveViewer.MarkTextSelectionForRedaction, false, true));
                }
                // Two rows of three keep the page menu narrow (Steve: five across was too wide).
                var textStrip = QuickStrip(_ctxMenu, 3, [.. text]);
                _ctxMenu.Items.Add(CardSlot(LabelledStrip(Loc("Str_Card_SelectedText"), textStrip), new Thickness(0)));
                FocusFirstActionFromKeyboard(_ctxMenu, textStrip);
            }

            // Placement drops the item exactly where the user right-clicked.
            var placeStrip = QuickStrip(_ctxMenu,
                ("\uE8D2", Loc("Str_Lbl_Text"), "T", () =>
                {
                    SetTool(EditTool.Text);
                    _activeCanvas = CanvasForPage(pageIdx);
                    PlaceTextBox(pt, pageIdx);
                }, false, true),
                ("\uE8E4", Loc("Str_Paragraph_Title"), "AltShift:P", () => EditParagraph(pageIdx, pt), false, true),
                ("\uEB9F", Loc("Str_Lbl_Image"), "I", () =>
                {
                    _activeCanvas = CanvasForPage(pageIdx);
                    PlaceImageFromDialog(pt, pageIdx);
                }, false, true),
                ("\uEE56", Loc("Str_Lbl_Signature"), "G", () =>
                {
                    _activeCanvas = CanvasForPage(pageIdx);
                    if (_pendingSignature is not null) PlaceSignature(pt, pageIdx);
                    else { SetTool(EditTool.Signature); ShowSignaturePopup(); }
                }, false, true));
            // No label over the strip (Steve): the buttons say what they place.
            _ctxMenu.Items.Add(CardSlot(placeStrip, new Thickness(0)));
            FocusFirstActionFromKeyboard(_ctxMenu, placeStrip);   // only when no selected-text strip came first

            _ctxMenu.Items.Add(new Separator());
            _ctxMenu.Items.Add(BuildFieldPresetMenu(pt, pageIdx));
            _ctxMenu.Items.Add(BuildComparisonContextItem());
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OcrPage"), (s, e) => OcrPageToClipboard(pageIdx), GestureFor("CtrlShift:O"), "\uEE6F"));

            var tools = MakeMenuItem(Loc("Str_Card_PageTools"), (_, _) => { }, gesture: "", glyph: "\uE7C3");
            tools.Items.Add(MakeRotateMenuItem(Loc("Str_Ctx_RotateCW"), (s, e) => RotatePages_Click(90, pageIdx), clockwise: true));
            tools.Items.Add(MakeRotateMenuItem(Loc("Str_Ctx_RotateCCW"), (s, e) => RotatePages_Click(-90, pageIdx), clockwise: false));
            tools.Items.Add(MakeTransformMenuItem(Loc("Str_Lbl_Rotate"), (s, e) => OpenTransformWindow(page: pageIdx), GestureFor("R")));
            tools.Items.Add(MakeMenuItem(Loc("Str_ColorCorrection"), (s, e) =>
                OpenTransformWindow(TransformWindowMode.ColorCorrection, pageIdx), gesture: GestureFor("E"), glyph: "\uE790"));
            tools.Items.Add(MakeMenuItem(Loc("Str_Pages_FixOrientation"), (s, e) => FixPageOrientation(),
                GestureFor("AltShift:O"), "\uE7AD"));
            tools.Items.Add(new Separator());
            tools.Items.Add(MakeMenuItem(Loc("Str_Pages_RasterizeRegion"), (s, e) => RasterizeRegion(pageIdx), glyph: "\uE91B"));
            tools.Items.Add(MakeMenuItem(Loc("Str_ExtractImage_Menu"), (s, e) => ExtractImage(pageIdx), glyph: "\uEB9F"));
            tools.Items.Add(MakeMenuItem(Loc("Str_TableExport_Title"), (_, _) => ExportTable(pageIdx), glyph: "\uE80A"));
            tools.Items.Add(new Separator());
            tools.Items.Add(MakeMenuItem(Loc("Str_Ctx_DuplicatePage"), (s, e) => DuplicatePage(pageIdx), glyph: "\uE8C8"));
            tools.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeletePage"), (_, _) => DeletePages(pageIdx), glyph: "\uE74D"));
            _ctxMenu.Items.Add(tools);
            _ctxMenu.Items.Add(new Separator());

            // Markup selected elsewhere on the page can still go from here.
            if (hasSelection)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeleteSel"), (s, e) => DeleteSelected(), GestureFor("Del"), "\uE74D"));
            if (CanPasteInView())
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Paste"), (s, e) => PasteInView(pageIdx), GestureFor("Ctrl:V"), "\uE77F"));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_SelectAll"), (s, e) => SelectAllInView(), GestureFor("Ctrl:A"), "\uE8B3"));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampPages"), (s, e) => OpenStampTool(pageIdx), GestureFor("S"), "\uE932"));
            var clearPage = MakeMenuItem(Loc("Str_Ctx_ClearPage"), (s, e) => ClearAnnotations_Click(s!, e), glyph: "\uE75C");
            clearPage.IsEnabled = (_annotations.TryGetValue(pageIdx, out var pageMarks) && pageMarks.Count > 0) || ActiveViewer.HasPendingAnnotationExt;
            _ctxMenu.Items.Add(clearPage);
            AppendViewItems(_ctxMenu);
        }

        // A small caption over a card strip ("Selected text", "Place here").
        private FrameworkElement LabelledStrip(string label, FrameworkElement strip)
        {
            var caption = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(8, 6, 0, 2) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var panel = new StackPanel();
            panel.Children.Add(caption);
            panel.Children.Add(strip);
            return panel;
        }

        // A native EPUB or DjVu book has no PDF page behind it, so its page menu holds only what works on a book:
        // the jump history, copying the selected text, Select All, converting to a PDF and the view items.
        private void AddNativePageMenuItems(int pageIdx)
        {
            AddNavRow(_ctxMenu, pageIdx);
            if (!string.IsNullOrEmpty(_selectedText))
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Copy"), (_, _) => CopySelectedText(), GestureFor("Ctrl:C"), "\uE8C8"));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_SelectAll"), (_, _) => SelectAllInView(), GestureFor("Ctrl:A"), "\uE8B3"));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_NativePdf_Convert") + "...", (_, _) => ConvertNativeDocumentToPdf(), "", "\uE8AB"));
            AppendViewItems(_ctxMenu);
        }

        // The top row of the page menus, laid out the way a browser's is: Back and Forward retrace the jump history
        // (Alt+Left, Alt+Right, the mouse's side buttons), then Undo, Redo and Print, and the page on the right.
        private void AddNavRow(ContextMenu menu, int pageIdx = -1)
        {
            var row = new DockPanel { Margin = new Thickness(4, 0, 4, 2), LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(NavRowButton(menu, "\uE72B", "Str_Kb_Back", "Alt:Left", () => NavHistoryGo(true), _navBack.Count > 0));
            buttons.Children.Add(NavRowButton(menu, "\uE72A", "Str_Kb_Forward", "Alt:Right", () => NavHistoryGo(false), _navForward.Count > 0));
            var rule = new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            rule.SetResourceReference(Border.BackgroundProperty, "MenuBorderBrush");
            buttons.Children.Add(rule);
            var edit = EditAvailability();
            buttons.Children.Add(NavRowButton(menu, "\uE7A7", "Str_Ctx_UndoLast", "Ctrl:Z", () => Undo_Click(this, new RoutedEventArgs()), edit.Undo));
            buttons.Children.Add(NavRowButton(menu, "\uE7A6", "Str_Ctx_Redo", "Ctrl:Y", () => Redo_Click(this, new RoutedEventArgs()), edit.Redo));
            buttons.Children.Add(NavRowButton(menu, "\uE749", "Str_Ctx_Print", "Ctrl:P", () => Print_Click(this, new RoutedEventArgs()), _doc is not null && _currentFile is not null));
            DockPanel.SetDock(buttons, Dock.Left);
            row.Children.Add(buttons);
            int pageCount = _doc?.PageCount ?? ActiveViewer.ReadPageCountExt;
            if (pageIdx >= 0 && pageCount > 0)
            {
                var where = new TextBlock
                {
                    Text = string.Format(Loc("Str_PageOf"), pageIdx + 1, pageCount), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0)
                };
                where.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                row.Children.Add(where);
            }
            menu.Items.Add(new MenuItem { Header = row, Focusable = false, Template = NavRowTemplate });
            menu.Items.Add(new Separator());
        }

        private Button NavRowButton(ContextMenu menu, string glyph, string labelKey, string cap, Action act, bool enabled)
        {
            var button = new Button
            {
                Content = glyph,
                IsEnabled = enabled,
                ToolTip = WithGesture(Loc(labelKey), cap),
            };
            if (TryFindResource("ToolbarButton") is Style style) button.Style = style;
            System.Windows.Automation.AutomationProperties.SetName(button, Loc(labelKey));
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += (_, _) =>
            {
                menu.IsOpen = false;
                act();
            };
            return button;
        }

        // The row's host item draws only the buttons: no highlight, no gutter, no click of its own.
        private static readonly ControlTemplate NavRowTemplate = BuildNavRowTemplate();

        private static ControlTemplate BuildNavRowTemplate()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter };
            template.Seal();
            return template;
        }

        // Full Screen and Book Layout end both page menus. Full Screen is checked while it is on,
        // so the same item is the way back out once the rail and toolbar are hidden.
        private void AppendViewItems(ContextMenu menu)
        {
            menu.Items.Add(new Separator());
            var fullScreen = MakeMenuItem(Loc("Str_Ctx_FullScreen"), (s, e) => ToggleFullScreen(), GestureFor("F11"), "\uE740");
            fullScreen.IsCheckable = true;
            fullScreen.IsChecked = _fullScreen;
            menu.Items.Add(fullScreen);
            AppendBookLayoutItem(menu);
        }

        // Book layout changes the whole two-page viewport, so it belongs on both the page menu and
        // the surrounding-canvas menu. Keeping the item construction here prevents the two menus
        // from drifting apart (including the live check state and keyboard hint).
        private void AppendBookLayoutItem(ContextMenu menu)
        {
            if (_viewMode is not (ViewMode.TwoPage or ViewMode.TwoPageContinuous)) return;

            var book = MakeMenuItem(Loc("Str_View_BookMode"), (s, e) => ToggleBookMode(), GestureFor("B"), "\uE736");
            book.IsCheckable = true;
            book.IsChecked = Controls.PdfViewer.BookMode;
            menu.Items.Add(book);
        }

        // Deep-copies page pageIdx and inserts the copy right after it. The engine imports the complete
        // reachable page graph into a new page identity instead of sharing the original page reference.
        private void DuplicatePage(int pageIdx)
        {
            if (_doc is null || pageIdx < 0 || pageIdx >= _doc.PageCount) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            try
            {
                ReloadWithRemappedAnnotations(
                    annotations =>
                    {
                        PageAnnotationInsertion.Shift(annotations, pageIdx + 1, 1);
                        // The copy carries the page's unsaved annotations, as pages dragged
                        // between panes do, so it matches the page it was made from.
                        if (!annotations.TryGetValue(pageIdx, out var source)) return;
                        var copies = source.Select(CloneAnnotation)
                            .Where(annotation => annotation is not null)
                            .Cast<PageAnnotation>()
                            .ToList();
                        foreach (PageAnnotation annotation in copies)
                            annotation.PageIndex = pageIdx + 1;
                        if (copies.Count > 0) annotations[pageIdx + 1] = copies;
                    },
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path =>
                            PdfEngineIntegration.DuplicatePage(path, pageIdx),
                        remapRedactionMarks: true,
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageDuplication(
                                rotations, pageIdx),
                        selectedPageAfterReload: pageIdx + 1,
                        documentUndo: documentUndo));
                SetStatus(string.Format(Loc("Str_St_DuplicatedPage"), pageIdx + 1));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_DuplicateFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Nearest annotation that visually overlaps `a`, searching up (dir>0) or down (dir<0) the page's
        // z-order list from a's index. Returns its list index, or -1 if nothing overlaps in that direction.
        // "Layer" order is judged by what actually sits on top of / under a at its location, so Raise/Lower
        // step past only the things stacked with it - not unrelated annotations elsewhere on the page.
        private static int OverlapNeighbor(List<PageAnnotation> list, int i, PageAnnotation a, int dir)
        {
            var ab = AnnotBounds(a);
            if (dir > 0)
            {
                for (int k = i + 1; k < list.Count; k++)
                    if (AnnotBounds(list[k]).IntersectsWith(ab)) return k;
            }
            else
            {
                for (int k = i - 1; k >= 0; k--)
                    if (AnnotBounds(list[k]).IntersectsWith(ab)) return k;
            }
            return -1;
        }

        // Raise (+1) or Lower (-1) an annotation one visual layer: move it just past the nearest annotation
        // that overlaps it in that direction. No-op when nothing overlaps above / below. Later in the list
        // = drawn on top.
        private void MoveAnnotationLayer(PageAnnotation a, int dir)
        {
            if (!_annotations.TryGetValue(a.PageIndex, out var list)) return;
            int i = list.IndexOf(a);
            if (i < 0) return;
            int target = OverlapNeighbor(list, i, a, dir);
            if (target < 0) return;
            PushPageSnapshotUndo(a.PageIndex);
            list.RemoveAt(i);
            list.Insert(target, a);   // after removal, inserting at the neighbor's old index lands a past it
            RenderAllAnnotations(a.PageIndex);
            ReattachSelectionVisuals();
            MarkDirty();
        }

        // Annotations copied via the context menu, held as page-independent deep clones until pasted.
        private readonly List<PageAnnotation> _annotationClipboard = [];

        // Deep-copy an annotation so the clipboard (and paste) is fully independent of the live object.
        // CoverAnnotation must be matched before HighlightAnnotation since it derives from it.
        private static PageAnnotation? CloneAnnotation(PageAnnotation a)
        {
            PageAnnotation? clone = CloneAnnotationCore(a);
            if (clone is not null)
                clone.CopyLayerStateFrom(a);
            return clone;
        }

        private static PageAnnotation? CloneAnnotationCore(PageAnnotation a) => a switch
        {
            CoverAnnotation c => new CoverAnnotation
            {
                PageIndex = c.PageIndex,
                PairId = c.PairId,
                GroupId = c.GroupId,
                Bounds = c.Bounds,
                Style = c.Style,
                ColorR = c.ColorR,
                ColorG = c.ColorG,
                ColorB = c.ColorB,
                ColorA = c.ColorA,
                Erases = c.Erases?.Select(e => new HighlightErase { Points = [.. e.Points], Radius = e.Radius }).ToList()
            },
            HighlightAnnotation h => new HighlightAnnotation
            {
                PageIndex = h.PageIndex,
                PairId = h.PairId,
                GroupId = h.GroupId,
                Bounds = h.Bounds,
                Style = h.Style,
                IsShape = h.IsShape,
                ColorR = h.ColorR,
                ColorG = h.ColorG,
                ColorB = h.ColorB,
                ColorA = h.ColorA,
                Erases = h.Erases?.Select(e => new HighlightErase { Points = [.. e.Points], Radius = e.Radius }).ToList()
            },
            TextAnnotation t => new TextAnnotation
            {
                PageIndex = t.PageIndex,
                PairId = t.PairId,
                GroupId = t.GroupId,
                Position = t.Position,
                Content = t.Content,
                FontSize = t.FontSize,
                LetterSpacing = t.LetterSpacing,
                BlurRadius = t.BlurRadius,
                FontName = t.FontName,
                Bold = t.Bold,
                Italic = t.Italic,
                Strike = t.Strike,
                Underline = t.Underline,
                Width = t.Width,
                Height = t.Height,
                Rotation = t.Rotation,
                ColorR = t.ColorR,
                ColorG = t.ColorG,
                ColorB = t.ColorB,
                ColorA = t.ColorA,
                BgR = t.BgR,
                BgG = t.BgG,
                BgB = t.BgB,
                BgA = t.BgA
            },
            MeasurementAnnotation m => new MeasurementAnnotation
            {
                PageIndex = m.PageIndex,
                AnnotationFlags = m.AnnotationFlags,
                PairId = m.PairId,
                GroupId = m.GroupId,
                Kind = m.Kind,
                Points = [.. m.Points],
                Closed = m.Closed,
                LabelCenter = m.LabelCenter,
                LabelSize = m.LabelSize,
                StrokeWidth = m.StrokeWidth,
                ColorR = m.ColorR,
                ColorG = m.ColorG,
                ColorB = m.ColorB,
                ColorA = m.ColorA,
                ScaleName = m.ScaleName,
                UnitsPerPoint = m.UnitsPerPoint,
                Unit = m.Unit,
                Precision = m.Precision
            },
            InkAnnotation ink => new InkAnnotation
            {
                PageIndex = ink.PageIndex,
                PairId = ink.PairId,
                GroupId = ink.GroupId,
                Points = [.. ink.Points],
                StrokeWidth = ink.StrokeWidth,
                ColorR = ink.ColorR,
                ColorG = ink.ColorG,
                ColorB = ink.ColorB,
                ColorA = ink.ColorA,
                FillR = ink.FillR,
                FillG = ink.FillG,
                FillB = ink.FillB,
                FillA = ink.FillA
            },
            SignatureAnnotation s => new SignatureAnnotation
            {
                PageIndex = s.PageIndex,
                PairId = s.PairId,
                GroupId = s.GroupId,
                Position = s.Position,
                Scale = s.Scale,
                SourceWidth = s.SourceWidth,
                SourceHeight = s.SourceHeight,
                Strokes = [.. s.Strokes.Select(st => new List<Point>(st))],
                StrokeWidth = s.StrokeWidth,
                ImageData = s.ImageData,
                Vectors = s.Vectors is null ? null : [.. s.Vectors]
            },
            ImageAnnotation img => new ImageAnnotation
            {
                PageIndex = img.PageIndex,
                PairId = img.PairId,
                GroupId = img.GroupId,
                Position = img.Position,
                Scale = img.Scale,
                SourceWidth = img.SourceWidth,
                SourceHeight = img.SourceHeight,
                ImageData = img.ImageData,
                Rotation = img.Rotation
            },
            _ => null
        };

        private void CutSelectedSourceImage()
        {
            if (!ActiveViewer.CanDeleteSelectedSourceImageExt
                || ActiveViewer.SelectedSourceImageExt is not { } source) return;
            if (CloneAnnotation(source) is not ImageAnnotation copy) return;
            _annotationClipboard.Clear();
            _annotationClipboard.Add(copy);
            DeleteSelected();
        }

        // Copy the current selection (primary or multi-select) into the annotation clipboard.
        private void CopySelectedAnnotations()
        {
            var sel = new List<PageAnnotation>();
            if (ActiveViewer.SelectedSourceImageExt is { } sourceImage) sel.Add(sourceImage);
            if (_selectedAnnotation is not null) sel.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!sel.Contains(a)) sel.Add(a);
            if (sel.Count == 0) return;
            // A replacement copies as the whole object, cover and text, whichever layer was selected. (#467)
            var expanded = new List<PageAnnotation>();
            foreach (var a in sel)
                foreach (var member in _annotations.TryGetValue(a.PageIndex, out var page)
                             ? Services.ReplacementLayers.CopyMembers([a], page) : [a])
                    if (!expanded.Contains(member)) expanded.Add(member);
            sel = expanded;

            _annotationClipboard.Clear();
            foreach (var a in sel)
                if (CloneAnnotation(a) is { } c) _annotationClipboard.Add(c);

            SetStatus(string.Format(Loc(_annotationClipboard.Count == 1
                ? "Str_St_CopiedAnnotationOne" : "Str_St_CopiedAnnotationMany"), _annotationClipboard.Count));
        }

        // Paste the clipboard onto pageIdx: fresh clones nudged down-right so they don't sit exactly on
        // the originals, clamped on-page, added (each its own undo step) and left selected. A pasted
        // text/cover pair keeps its pairing internally but is regenerated so it's independent of the source.
        private void PasteAnnotations(int pageIdx)
        {
            // EPUB and DjVu books are read-only; annotations cannot be added to them.
            if (_annotationClipboard.Count == 0 || pageIdx < 0 || ActiveViewer.NativeDocumentRef is not null) return;
            const double off = 14;

            var pasted = new List<PageAnnotation>();
            var pairMap = new Dictionary<string, string>();
            var groupMap = new Dictionary<string, string>();
            foreach (var src in _annotationClipboard)
            {
                if (CloneAnnotation(src) is not { } c) continue;
                c.PageIndex = pageIdx;
                // Remap pairing and grouping so the pasted set stays internally linked but independent
                // of the originals (a copied group/pair pastes as its own new group/pair).
                if (c.PairId.Length > 0)
                {
                    if (!pairMap.TryGetValue(c.PairId, out var np))
                    {
                        np = Guid.NewGuid().ToString("N");
                        pairMap[c.PairId] = np;
                    }
                    c.PairId = np;
                }
                if (c.GroupId.Length > 0)
                {
                    if (!groupMap.TryGetValue(c.GroupId, out var ng))
                    {
                        ng = Guid.NewGuid().ToString("N");
                        groupMap[c.GroupId] = ng;
                    }
                    c.GroupId = ng;
                }
                AnnotSetPos(c, new Point(AnnotGetPos(c).X + off, AnnotGetPos(c).Y + off));
                AnnotSetPos(c, ClampAnnotPos(c));
                pasted.Add(c);
            }
            if (pasted.Count == 0) return;

            ClearSelection();
            foreach (var c in pasted) AddAnnotation(c);
            RenderAllAnnotations(pageIdx);

            var canvas = CanvasForPage(pageIdx);
            _activeCanvas = canvas;
            if (pasted.Count == 1)
                SelectAnnotation(pasted[0], AnnotBounds(pasted[0]));
            else
                foreach (var c in pasted) ToggleMultiSelect(c, AnnotBounds(c), canvas);

            SetStatus(string.Format(Loc(pasted.Count == 1
                ? "Str_St_PastedAnnotationOne" : "Str_St_PastedAnnotationMany"), pasted.Count));
        }

        // --- Edit / pairing / grouping menu actions ------------------------------------------------

        // Other members of a group are moved alongside the primary during a drag; this holds each one
        // with the position it had when the drag began so the whole group translates rigidly.
        private List<(PageAnnotation a, Point orig)> _dragGroupOrig => ActiveViewer.DragGroupOrigRef;

        // "Edit" menu action: inline-edit a text box. For any other annotation, selecting it (which the
        // right-click already did) opened its color/size bar, so there's nothing more to do here.
        private void EditAnnotation(PageAnnotation hit)
        {
            if (hit is TextAnnotation ta)
            {
                Size footprint = ta.FootprintSize;
                var p = new Point(ta.Position.X + Math.Min(Math.Max(footprint.Width, 8) / 2, 10),
                                  ta.Position.Y + Math.Min(Math.Max(footprint.Height, 8) / 2, 8));
                EditTextAtPosition(p, ta.PageIndex);
            }
        }

        // The other half of a text/cover pair, if any.
        private PageAnnotation? PairPartner(PageAnnotation a)
        {
            if (a.PairId.Length == 0 || !_annotations.TryGetValue(a.PageIndex, out var list)) return null;
            return list.FirstOrDefault(x => !ReferenceEquals(x, a) && x.PairId == a.PairId);
        }

        // Switch the selection from one half of a pair to the other.
        private void SelectPartner(PageAnnotation a)
        {
            var p = PairPartner(a);
            if (p is null) return;
            ClearSelection();
            RenderAllAnnotations(p.PageIndex);
            _activeCanvas = CanvasForPage(p.PageIndex);
            SelectAnnotation(p, AnnotBounds(p));
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Did the click land on a thumbnail, or the empty area below the list? Page-specific actions
            // (rotate, move, delete) only make sense on a thumbnail; the empty area gets the page-agnostic
            // menu (same one the gray area around the page uses). With no document open the menu still
            // opens, carrying only the sidebar-side section appended at the bottom.
            ListBoxItem? clickedItem = null;
            if (_doc is not null)
                for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem item) { clickedItem = item; break; }

            bool onThumb = clickedItem is not null;
            int clickedPage = -1;
            if (clickedItem?.DataContext is PageThumbnailVm clickedThumbnail)
            {
                clickedPage = clickedThumbnail.PageIndex;
                // WPF does not select a ListBoxItem on right-click. Make page actions target the
                // thumbnail that opened the menu, while preserving an existing multi-selection.
                if (!PageList.SelectedItems.Contains(clickedThumbnail))
                    PageList.SelectedItem = clickedThumbnail;
            }

            // A thumbnail gets the page card; the empty area below the list keeps the page-agnostic menu.
            var menu = onThumb ? BuildPageCard(clickedPage) : MakeThemedMenu();
            OpenSidebarMenu(menu, onThumb, PageList);
            e.Handled = true;
        }

        /// <summary>The Menu key or Shift+F10 in the page list opens the focused thumbnail's card (1.9.3 review r2 BF-004).</summary>
        private bool OpenPageCardFromKeyboard()
        {
            if (_doc is null || !PageList.IsKeyboardFocusWithin) return false;
            var item = Keyboard.FocusedElement as ListBoxItem;
            if (item?.DataContext is not PageThumbnailVm thumbnail)
            {
                if (PageList.SelectedIndex < 0) return false;
                item = PageList.ItemContainerGenerator.ContainerFromIndex(PageList.SelectedIndex) as ListBoxItem;
                thumbnail = (PageThumbnailVm)PageList.SelectedItem;
            }
            if (!PageList.SelectedItems.Contains(thumbnail)) PageList.SelectedItem = thumbnail;
            if (item is null)
            {
                // A scrolled-away thumbnail has no container; bring it into view so the card opens beside it (r3 BF-004).
                PageList.ScrollIntoView(thumbnail);
                PageList.UpdateLayout();
                item = PageList.ItemContainerGenerator.ContainerFromItem(thumbnail) as ListBoxItem;
            }
            var menu = BuildPageCard(thumbnail.PageIndex);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            OpenSidebarMenu(menu, onThumb: true, (UIElement?)item ?? PageList);
            return true;
        }

        private void OpenSidebarMenu(ContextMenu menu, bool onThumb, UIElement target)
        {
            if (!onThumb && _doc is not null)
            {
                FillPageAgnosticMenu(menu);
            }
            AppendSidebarSideSection(menu);
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        // Right-click on any other part of the sidebar - the PAGES/OUTLINES header, the page
        // controls row, the outline panel's empty space, the toggle strip - reaches this handler
        // on the two container Borders (SidebarBorder, SidebarToggleStrip). The page list's own
        // handler above marks its clicks handled, so they never double up; the outline TREE is
        // ceded entirely, because its bookmark menu opens on Preview mouse DOWN and does not mark
        // the Up handled - without the guard this would open a second menu over it.
        private void SidebarArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Button? railButton = null;
            bool rail = false;
            if (e.OriginalSource is DependencyObject d)
                for (var n = d; n != null; n = VisualTreeHelper.GetParent(n))
                {
                    if (ReferenceEquals(n, OutlineTree)) return;
                    if (n is Button button && railButton is null) railButton = button;
                    if (ReferenceEquals(n, SidebarRailGrid) || ReferenceEquals(n, SidebarRailStack))
                        rail = true;
                }
            if (rail && (railButton == LangButton || railButton == ThemeButton))
            {
                if (railButton == LangButton) _languageMenu.Open();
                else _themePicker.Toggle();
                e.Handled = true;
                return;
            }
            if (rail && railButton?.ContextMenu is ContextMenu custom)
            {
                custom.CustomPopupPlacementCallback = null;
                custom.PlacementTarget = railButton;
                custom.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                custom.ClearValue(ContextMenu.HorizontalOffsetProperty);
                custom.ClearValue(ContextMenu.VerticalOffsetProperty);
                custom.IsOpen = true;
                e.Handled = true;
                return;
            }
            var menu = MakeThemedMenu();
            if (_doc is not null)
            {
                if (!rail) FillSidebarPanelMenu(menu, e.OriginalSource as DependencyObject);
                else
                    foreach (var (button, panel) in RailPanelButtons())
                        if (ReferenceEquals(button, railButton))
                        {
                            FillSidebarPanelMenu(menu, null, panel);
                            break;
                        }
            }
            AppendSidebarSideSection(menu);
            menu.PlacementTarget = (FrameworkElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // The sidebar-side picker rides the bottom of the sidebar's own right-click menu (it moved
        // out of the Settings panel, like the toolbar picker moved onto the toolbar). The keyboard
        // gesture is shown on the side Shift+F9 would move it TO - each press steps one row down
        // this list - so putting it on the checked row would advertise a press that does nothing.
        private void AppendSidebarSideSection(ContextMenu menu)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            // One row with a flyout instead of a header and four rows at the foot of every sidebar menu.
            var sidebar = new MenuItem { Name = "SidebarMenuItem", Header = Loc("Str_Card_Sidebar"), Icon = _sidebarSide.MenuIcon() };
            menu.Items.Add(sidebar);
            menu = new ContextMenu();   // collects the flyout's rows below, then hands them to the Sidebar row
            if (_viewMode is ViewMode.TwoPage or ViewMode.TwoPageContinuous)
            {
                var paired = new MenuItem
                {
                    Name = "TwoPageThumbnailsMenuItem",
                    Header = Loc("Str_Sidebar_TwoPageThumbnails"),
                    IsCheckable = true,
                    IsChecked = App.GetSetting("TwoPageThumbnails") == "1",
                    IsEnabled = !_sidebarStripLayout
                };
                paired.Click += (_, _) =>
                {
                    App.SetSetting("TwoPageThumbnails", paired.IsChecked ? "1" : "0");
                    ApplyPageThumbnailLayout();
                };
                menu.Items.Add(paired);
            }
            foreach (SidebarSide side in new[] { SidebarSide.Left, SidebarSide.Right, SidebarSide.Top, SidebarSide.Bottom })
            {
                var item = new MenuItem { Header = Loc(side.StringKey()), IsCheckable = true, IsChecked = side == _sidebarSide, Icon = side.MenuIcon() };
                if (side == _sidebarSide.Next()) item.InputGestureText = GestureFor("Shift:F9");
                item.Click += (_, _2) => SelectSidebarSide(side);
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 4) menu.Items.Insert(1, new Separator());   // the two-page toggle above the sides
            foreach (object row in menu.Items.Cast<object>().ToList())
            {
                menu.Items.Remove(row);
                sidebar.Items.Add(row);
            }
        }

        private ContextMenu MakeThemedMenu()
        {
            var menu = new ContextMenu();
            // Code-created ContextMenus are popup roots and can miss the owning Window's implicit
            // style during construction. Attach it explicitly so every generated menu receives
            // the square, two-stage 98SE frame instead of WPF's rounded fallback chrome.
            if (TryFindResource(typeof(ContextMenu)) is Style style) menu.Style = style;
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            return menu;
        }

        // Document-wide actions for a right-click with no specific page under the cursor: the empty sidebar
        // area below the thumbnails, or the gray area around the page in the document pane.
        private void FillPageAgnosticMenu(ContextMenu menu)
        {
            // The card frame and icon strips the page menu and the thumbnail card use, so the menu around the
            // page reads like the menu on it. Every action and shortcut the old flat list had is still here.
            if (TryFindResource("CardMenu") is Style card) menu.Style = card;
            AddOwnerPasswordItem(menu);
            AddNavRow(menu);
            // One row with no label over it, the same shape as the page menu's strip, so the two menus match.
            var pages = QuickStrip(menu,
                ("\uE7C3", Loc("Str_Ctx_AddBlankPage"), "AltShift:A", AddBlankPageAtEnd, false, true),
                ("\uE8CB", Loc("Str_Pages_Reverse"), "AltShift:R", ReversePageOrder, false, true),
                ("\uE7AD", Loc("Str_Pages_FixOrientation"), "AltShift:O", FixPageOrientation, false, true),
                ("\uE809", Loc("Str_Pages_Straighten"), "AltShift:S", StraightenScannedPages, false, true),
                ("\uE89F", Loc("Str_Pages_DivideSpreads"), "AltShift:D", DivideSpreads, false, true));
            menu.Items.Add(CardSlot(pages, new Thickness(0)));
            FocusFirstActionFromKeyboard(menu, pages);
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildComparisonContextItem());
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_ZoomIn"), (s, e) => ZoomIn_Click(s!, e), GestureFor("Ctrl:Equals"), ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_ZoomOut"), (s, e) => ZoomOut_Click(s!, e), GestureFor("Ctrl:Minus"), ""));
            menu.Items.Add(new Separator());
            if (CanPasteInView())
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Paste"), (s, e) => PasteInView(PageList.SelectedIndex), GestureFor("Ctrl:V"), "\uE77F"));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_SelectAll"), (s, e) => SelectAllInView(), GestureFor("Ctrl:A"), "\uE8B3"));
            var clearAll = MakeMenuItem(Loc("Str_Lbl_Clear"), (s, e) => ClearAllAnnotations_Click(s!, e), glyph: "\uED62");
            clearAll.IsEnabled = EditAvailability().Clear;
            menu.Items.Add(clearAll);
            AppendViewItems(menu);
        }

        // Opens the page-agnostic menu for a right-click on the gray area around the page (the document
        // pane background). Wired in code so right-clicking outside the page is no longer a dead spot.
        // internal: PdfViewer's XAML binds this and forwards to it.
        internal void DocPaneBackground_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            // Only when the click really hit the background, not a page tile (those have their own menu).
            if (e.OriginalSource is DependencyObject d)
                for (var n = d; n != null; n = VisualTreeHelper.GetParent(n))
                    if (n is Canvas) return;
            var menu = MakeThemedMenu();
            FillPageAgnosticMenu(menu);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // glyph: optional Segoe MDL2 codepoint rendered in the menu's left gutter (the 16px
        // check column doubles as an icon slot - see the MenuItem template in MainWindow.xaml).
        private MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null, string? glyph = null)
        {
            gesture ??= MenuShortcutFor(header);
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            if (glyph != null)
            {
                var icon = new Glyph
                {
                    Text = glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                item.Icon = icon;
            }
            return item;
        }

        private MenuItem MakeRotateMenuItem(string header, RoutedEventHandler click, bool clockwise)
        {
            string gesture = GestureFor(clockwise ? "Ctrl:R" : "CtrlShift:R");
            var item = MakeMenuItem(header, click, gesture, glyph: "\uE7AD");
            if (item.Icon is TextBlock icon)
            {
                icon.FontSize = 15;
                if (!clockwise)
                    icon.LayoutTransform = new System.Windows.Media.ScaleTransform(-1, 1);
            }
            return item;
        }

        private MenuItem MakeTransformMenuItem(string header, RoutedEventHandler click, string? gesture = null)
        {
            var item = MakeMenuItem(header, click, gesture);
            var icon = new Grid { Width = 16, Height = 16 };
            var box = new Border
            {
                Width = 14,
                Height = 14,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(MenuItem.Foreground)) { Source = item });
            icon.Children.Add(box);
            icon.Children.Add(new Glyph
            {
                Text = "\uE7C2",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            item.Icon = icon;
            return item;
        }
    }
}
