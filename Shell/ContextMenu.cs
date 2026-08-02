using System.Diagnostics;
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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Context menu
        // ============================================================

        private void ApplyGrainTexture()
        {
            // Film grain with a mix of bright AND dark specks so the texture reads
            // on any background - bright specks show on dark themes, dark specks
            // show on light themes. Denser and a touch stronger than the first pass.
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4]; // start fully transparent
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(3) != 0) continue;       // ~33% pixel density
                bool bright = rng.Next(2) == 0;        // half bright, half dark
                byte v = bright ? (byte)rng.Next(190, 255) : (byte)rng.Next(0, 50);
                byte a = (byte)rng.Next(35, 95);       // alpha for subtlety
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            // BOTH panes by name, not through GrainBrush - that member resolves to ActiveViewer, so
            // only the focused pane ever got the texture and pane B rendered flat.
            Viewer.Grain.ImageSource  = bmp;
            ViewerB.Grain.ImageSource = bmp;
            if (SidebarGrainBrush != null) SidebarGrainBrush.ImageSource = bmp;
            if (ToggleGrainBrush != null) ToggleGrainBrush.ImageSource = bmp;
            // TryFindResource, NOT the Resources indexer: the indexer looks ONLY in this window's
            // own dictionary and returns null without complaint if the key lives higher up.
            // A key defined above this window (GrainBrushShared can be) is invisible to the
            // indexer, and the failure is silent - the grain simply never gets its texture.
            // TryFindResource walks window -> app, so it works wherever the key is defined.
            if (TryFindResource("GrainBrushShared") is ImageBrush sharedGrain) sharedGrain.ImageSource = bmp;
            // The family flyout standard's grain tile (FlyoutGrain style) shares the same texture.
            if (TryFindResource("GrainTileBrush") is ImageBrush flyoutGrain) flyoutGrain.ImageSource = bmp;
        }

        /// <summary>Generated film-grain tile, exposed so secondary windows (e.g. the
        /// print preview) can paint the same texture over their document area.</summary>
        public ImageSource? GrainTexture => GrainBrush?.ImageSource;

        // One shared ContextMenu instance; its items are rebuilt on every open to match whatever is
        // under the cursor (a specific annotation -> per-type actions; empty page -> page-level actions).
        private ContextMenu _ctxMenu = null!;

        private void BuildContextMenu()
        {
            _ctxMenu = new ContextMenu();
            TextOptions.SetTextFormattingMode(_ctxMenu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(_ctxMenu, TextRenderingMode.Grayscale);
            _annotationCanvas.ContextMenu = _ctxMenu;

            // The primary canvas opens its menu automatically on right-click; rebuild items first based
            // on the cursor position. The per-tile overlays call PopulateContextMenu themselves (they open
            // the menu programmatically, which does not raise ContextMenuOpening).
            _annotationCanvas.ContextMenuOpening += (s, e) =>
                PopulateContextMenu(Mouse.GetPosition(_annotationCanvas), PageList.SelectedIndex);
        }

        // Rebuild the shared context menu for a right-click at canvas point pt on the given page. If an
        // annotation sits under the cursor it is selected and gets a menu tailored to its type; otherwise
        // the page-level menu (tools, rotate, stamp, undo, clear) is shown.
        internal void PopulateContextMenu(Point pt, int pageIdx)
        {
            _ctxMenu.Items.Clear();
            var hit = AnnotationAt(pt, pageIdx);
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
                AddAnnotationMenuItems(hit);
            }
            // Tiled views (continuous/grid/two-page) have no clickable link overlay, so resolve a link
            // under the cursor by bounds-check and offer the same menu the single-page overlay carries.
            else if (LinkAt(pt, pageIdx) is { } lk)
                AddLinkMenuItems(_ctxMenu, lk.target, lk.annotIndex, pageIdx);
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
            CoverAnnotation => "Cover",
            TextAnnotation => "Text box",
            InkAnnotation => "Drawing",
            SignatureAnnotation => "Signature",
            ImageAnnotation => "Image",
            HighlightAnnotation h => h.Style switch
            {
                HighlightStyle.Strikethrough => "Strikethrough",
                HighlightStyle.Underline => "Underline",
                _ => "Highlight"
            },
            _ => "Annotation"
        };

        // Per-type menu for a clicked annotation. Page-level items (tools, rotate, stamp) are omitted.
        private void AddAnnotationMenuItems(PageAnnotation hit)
        {
            bool multi = SelectionCount() > 1;

            // Edit sits at the top of every single-annotation menu (completes the menu). For a text box it
            // lifts the text into an editable box; for other types it just ensures the editing bar is open.
            if (!multi)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Edit"), (s, e) => EditAnnotation(hit), glyph: ""));

            // Raise / Lower one visual layer. Enabled only when something actually overlaps in that
            // direction (so they're greyed out when nothing is stacked above / below at this spot).
            int idx = -1;
            _annotations.TryGetValue(hit.PageIndex, out var pageList);
            if (pageList is not null) idx = pageList.IndexOf(hit);
            var raise = MakeMenuItem(Loc("Str_Ctx_Raise"), (s, e) => MoveAnnotationLayer(hit, +1), glyph: "");
            raise.IsEnabled = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, +1) >= 0;
            _ctxMenu.Items.Add(raise);
            var lower = MakeMenuItem(Loc("Str_Ctx_Lower"), (s, e) => MoveAnnotationLayer(hit, -1), glyph: "");
            lower.IsEnabled = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, -1) >= 0;
            _ctxMenu.Items.Add(lower);

            // Pairing: on a single paired item, offer to jump to its other half. Unpair shows whenever the
            // selection contains a pair - including when both halves are selected together (or as a group).
            if (hit.PairId.Length > 0 && !multi)
            {
                var partner = PairPartner(hit);
                if (partner is not null)
                    _ctxMenu.Items.Add(MakeMenuItem(
                        Loc(partner is CoverAnnotation ? "Str_Ctx_SelectCover" : "Str_Ctx_SelectText"),
                        (s, e) => SelectPartner(hit), glyph: ""));
            }
            if (SelectedPaired() is not null)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Unpair"), (s, e) => UnpairSelected(), glyph: ""));

            // Grouping: group an ad-hoc multi-selection, or - on a grouped item - drop just this one
            // (Remove from group) or dissolve the whole group (Ungroup).
            if (hit.GroupId.Length > 0)
            {
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RemoveFromGroup"), (s, e) => RemoveFromGroup(hit), glyph: ""));
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Ungroup"), (s, e) => UngroupAnnotation(hit), glyph: ""));
            }
            else if (multi)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Group"), (s, e) => GroupSelected(), glyph: ""));

            _ctxMenu.Items.Add(new Separator());
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Copy"), (s, e) => CopySelectedAnnotations(), "Ctrl+C", ""));
            if (_annotationClipboard.Count > 0)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Paste"), (s, e) => PasteAnnotations(hit.PageIndex), "Ctrl+V", ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeleteSel"), (s, e) => DeleteSelected(), "Delete", ""));
        }

        // Page-level menu shown when the right-click lands on empty page space (no annotation under it).
        // Tailored to the click: Copy Text only over a text selection, the placement actions drop their
        // item where the user right-clicked, and delete is "Delete Selected" when something is selected
        // (otherwise "Delete Page").
        private void AddPageMenuItems(Point pt, int pageIdx)
        {
            bool hasSelection = SelectionCount() > 0;
            bool hasTextSel   = !string.IsNullOrEmpty(_selectedText);

            // Copy Text only when there is actually selected text to copy.
            if (hasTextSel)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyText"), (s, e) => CopySelectedText(), "Ctrl+C", ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Print"), (s, e) => Print_Click(s!, e), "Ctrl+P", ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OcrPage"), (s, e) => OcrPageToClipboard(pageIdx), "Ctrl+Shift+O", ""));
            _ctxMenu.Items.Add(new Separator());

            // Placement actions - drop the item exactly where the user right-clicked (Select / Highlight /
            // Draw were removed: they only make sense as toolbar modes, not as one-shot right-click actions).
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Lbl_Text"), (s, e) =>
            {
                SetTool(EditTool.Text);
                _activeCanvas = CanvasForPage(pageIdx);
                PlaceTextBox(pt, pageIdx);
            }, glyph: ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Lbl_Image"), (s, e) =>
            {
                _activeCanvas = CanvasForPage(pageIdx);
                PlaceImageFromDialog(pt, pageIdx);
            }, glyph: ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Lbl_Signature"), (s, e) =>
            {
                _activeCanvas = CanvasForPage(pageIdx);
                if (_pendingSignature is not null) PlaceSignature(pt, pageIdx);
                else { SetTool(EditTool.Signature); ShowSignaturePopup(); }
            }, glyph: ""));
            _ctxMenu.Items.Add(new Separator());

            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCW"), (s, e) => RotatePages_Click(90), glyph: ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCCW"), (s, e) => RotatePages_Click(-90), glyph: ""));
            _ctxMenu.Items.Add(new Separator());

            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DuplicatePage"), (s, e) => DuplicatePage(pageIdx), glyph: ""));
            // Delete Selected when something is selected; otherwise Delete Page (the page under the cursor,
            // which the right-click already made the selected page).
            if (hasSelection)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeleteSel"), (s, e) => DeleteSelected(), "Delete", ""));
            else
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeletePage"), (s, e) => Delete_Click(s!, e), glyph: ""));
            _ctxMenu.Items.Add(new Separator());

            if (_annotationClipboard.Count > 0)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Paste"), (s, e) => PasteAnnotations(pageIdx), "Ctrl+V", ""));

            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampPages"), (s, e) => OpenStampTool(), glyph: ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_UndoLast"), (s, e) => Undo_Click(s!, e), "Ctrl+Z", ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Redo"), (s, e) => Redo_Click(s!, e), "Ctrl+Y", ""));
            _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_ClearPage"), (s, e) => ClearAnnotations_Click(s!, e), glyph: ""));
        }

        // Deep-copies page pageIdx and inserts the copy right after it. AddPage on a same-document page
        // would share the reference rather than duplicate, so the page is re-imported from an in-memory
        // copy of the document (the same round-trip the undo snapshot uses).
        private void DuplicatePage(int pageIdx)
        {
            if (_doc is null || pageIdx < 0 || pageIdx >= _doc.PageCount) return;
            var doc = _doc;
            try
            {
                using var ms = new MemoryStream();
                doc.Save(ms, false);
                ms.Position = 0;
                using var src = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
                var copy = doc.AddPage(src.Pages[pageIdx]);   // imported copy, appended at the end
                doc.Pages.RemoveAt(doc.PageCount - 1);
                doc.Pages.Insert(pageIdx + 1, copy);
                SaveTempAndReload();
                PageList.SelectedIndex = pageIdx + 1;
                SetStatus($"Duplicated page {pageIdx + 1}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Duplicate failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Nearest annotation that visually overlaps `a`, searching up (dir>0) or down (dir<0) the page's
        // z-order list from a's index. Returns its list index, or -1 if nothing overlaps in that direction.
        // "Layer" order is judged by what actually sits on top of / under a at its location, so Raise/Lower
        // step past only the things stacked with it - not unrelated annotations elsewhere on the page.
        private int OverlapNeighbor(List<PageAnnotation> list, int i, PageAnnotation a, int dir)
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
        private static PageAnnotation? CloneAnnotation(PageAnnotation a) => a switch
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
                ColorA = c.ColorA
            },
            HighlightAnnotation h => new HighlightAnnotation
            {
                PageIndex = h.PageIndex,
                PairId = h.PairId,
                GroupId = h.GroupId,
                Bounds = h.Bounds,
                Style = h.Style,
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
                FontName = t.FontName,
                Bold = t.Bold,
                Italic = t.Italic,
                Strike = t.Strike,
                Underline = t.Underline,
                Width = t.Width,
                Height = t.Height,
                ColorR = t.ColorR,
                ColorG = t.ColorG,
                ColorB = t.ColorB,
                ColorA = t.ColorA,
                BgR = t.BgR,
                BgG = t.BgG,
                BgB = t.BgB,
                BgA = t.BgA
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
                ColorA = ink.ColorA
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
                ImageData = s.ImageData
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
                ImageData = img.ImageData
            },
            _ => null
        };

        // Copy the current selection (primary or multi-select) into the annotation clipboard.
        private void CopySelectedAnnotations()
        {
            var sel = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) sel.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!sel.Contains(a)) sel.Add(a);
            if (sel.Count == 0) return;

            _annotationClipboard.Clear();
            foreach (var a in sel)
                if (CloneAnnotation(a) is { } c) _annotationClipboard.Add(c);

            SetStatus(_annotationClipboard.Count == 1
                ? "Copied 1 annotation" : $"Copied {_annotationClipboard.Count} annotations");
        }

        // Paste the clipboard onto pageIdx: fresh clones nudged down-right so they don't sit exactly on
        // the originals, clamped on-page, added (each its own undo step) and left selected. A pasted
        // text/cover pair keeps its pairing internally but is regenerated so it's independent of the source.
        private void PasteAnnotations(int pageIdx)
        {
            if (_annotationClipboard.Count == 0 || pageIdx < 0) return;
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

            SetStatus(pasted.Count == 1 ? "Pasted 1 annotation" : $"Pasted {pasted.Count} annotations");
        }

        // --- Edit / pairing / grouping menu actions ------------------------------------------------

        // Other members of a group are moved alongside the primary during a drag; this holds each one
        // with the position it had when the drag began so the whole group translates rigidly.
        private readonly List<(PageAnnotation a, Point orig)> _dragGroupOrig = [];

        // "Edit" menu action: inline-edit a text box. For any other annotation, selecting it (which the
        // right-click already did) opened its color/size bar, so there's nothing more to do here.
        private void EditAnnotation(PageAnnotation hit)
        {
            if (hit is TextAnnotation ta)
            {
                var p = new Point(ta.Position.X + Math.Min(Math.Max(ta.Width, 8) / 2, 10),
                                  ta.Position.Y + Math.Min(Math.Max(ta.Height, 8) / 2, 8));
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
            bool onThumb = false;
            if (_doc is not null)
                for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                    if (d is ListBoxItem) { onThumb = true; break; }

            var menu = MakeThemedMenu();
            if (onThumb)
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_InsertBlank"), (s, ev) => InsertBlankPage_Click(s!, ev), glyph: ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DuplicatePage"), (s, ev) => DuplicatePage(PageList.SelectedIndex), glyph: ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCWShort"), (s, ev) => RotatePages_Click(90), glyph: ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCCWShort"), (s, ev) => RotatePages_Click(-90), glyph: ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveUp"), (s, ev) => MoveUp_Click(s!, ev), glyph: ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveDown"), (s, ev) => MoveDown_Click(s!, ev), glyph: ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_ExtractPages"), (s, ev) => Split_Click(s!, ev), glyph: ""));
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeletePages"), (s, ev) => Delete_Click(s!, ev), glyph: ""));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampPages"), (s, ev) => OpenStampTool(), glyph: ""));
            }
            else if (_doc is not null)
            {
                FillPageAgnosticMenu(menu);
            }
            AppendSidebarSideSection(menu);
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // Right-click on any other part of the sidebar - the PAGES/OUTLINES header, the page
        // controls row, the outline panel's empty space, the toggle strip - reaches this handler
        // on the two container Borders (SidebarBorder, SidebarToggleStrip). The page list's own
        // handler above marks its clicks handled, so they never double up; the outline TREE is
        // ceded entirely, because its bookmark menu opens on Preview mouse DOWN and does not mark
        // the Up handled - without the guard this would open a second menu over it.
        private void SidebarArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d)
                for (var n = d; n != null; n = VisualTreeHelper.GetParent(n))
                    if (ReferenceEquals(n, OutlineTree)) return;
            var menu = MakeThemedMenu();
            if (_doc is not null) FillPageAgnosticMenu(menu);
            AppendSidebarSideSection(menu);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // The sidebar-side picker rides the bottom of the sidebar's own right-click menu (it moved
        // out of the Settings panel, like the toolbar picker moved onto the toolbar). The keyboard
        // gesture is shown on the side Ctrl+Shift+B would switch TO - it is a toggle, so putting it
        // on the checked row would advertise a press that does nothing.
        private void AppendSidebarSideSection(ContextMenu menu)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = Loc("Str_Sidebar"), IsEnabled = false });
            var left  = new MenuItem { Header = Loc("Str_Sidebar_Left"),  IsCheckable = true, IsChecked = !_sidebarRight };
            var right = new MenuItem { Header = Loc("Str_Sidebar_Right"), IsCheckable = true, IsChecked = _sidebarRight };
            (_sidebarRight ? left : right).InputGestureText = "Ctrl+Shift+B";
            left.Click  += (_, _2) => SelectSidebarSide(false);
            right.Click += (_, _2) => SelectSidebarSide(true);
            menu.Items.Add(left);
            menu.Items.Add(right);
        }

        private ContextMenu MakeThemedMenu()
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            return menu;
        }

        // Document-wide actions for a right-click with no specific page under the cursor: the empty sidebar
        // area below the thumbnails, or the gray area around the page in the document pane.
        private void FillPageAgnosticMenu(ContextMenu menu)
        {
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_AddBlankPage"), (s, e) => AddBlankPageAtEnd(), glyph: ""));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Print"), (s, e) => Print_Click(s!, e), "Ctrl+P", ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_ZoomIn"), (s, e) => ZoomIn_Click(s!, e), "Ctrl+=", ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_ZoomOut"), (s, e) => ZoomOut_Click(s!, e), "Ctrl+-", ""));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_UndoLast"), (s, e) => Undo_Click(s!, e), "Ctrl+Z", ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Redo"), (s, e) => Redo_Click(s!, e), "Ctrl+Y", ""));
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_Clear"), (s, e) => ClearAllAnnotations_Click(s!, e), glyph: ""));
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
        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null, string? glyph = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            if (glyph != null)
            {
                var icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                item.Icon = icon;
            }
            return item;
        }
    }
}
