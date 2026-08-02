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
        // Sidebar outline/bookmark panel
        // ============================================================

        private void SidebarPagesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToPagesTab();
        private void SidebarOutlinesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToOutlinesTab();

        private const double SidebarMaxPages = 234;   // stops when the 200px-capped thumbnail fills (200 + margins + scrollbar)
        private const double SidebarMaxOutlines = 480;
        private const double SidebarMinOpen = 120;   // narrowest readable width before labels/header clip

        private void SwitchSidebarToPagesTab()
        {
            _sidebarShowingOutlines = false;
            PageList.Visibility = Visibility.Visible;
            OutlineScrollViewer.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = _doc != null ? Visibility.Visible : Visibility.Collapsed;   // no empty box when nothing is open
            SidebarPagesTab.Foreground = (Brush)FindResource("PrimaryBrush");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("MutedTextBrush");
            // Save current outlines width before snapping back to pages.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxOutlines));

            SidebarSplitter.IsEnabled = true;   // pages are resizable too now (drag the splitter)
            _sidebarCol.MaxWidth = SbPx(SidebarMaxPages);
            if (!_sidebarCollapsed)
            {
                double target = _savedPagesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        private void SwitchSidebarToOutlinesTab()
        {
            // Save current pages width, then restore (or auto-fit) the outlines width.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxPages));

            _sidebarShowingOutlines = true;
            PageList.Visibility = Visibility.Collapsed;
            OutlineScrollViewer.Visibility = Visibility.Visible;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarPagesTab.Foreground = (Brush)FindResource("MutedTextBrush");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("PrimaryBrush");
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SbPx(SidebarMaxOutlines);
            if (!_sidebarCollapsed)
            {
                if (!_outlinesFitted)
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)AutoFitOutlineWidth);
                else
                {
                    double target = _savedOutlinesWidth;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)(() => _sidebarCol.Width = new GridLength(target)));
                }
            }
        }

        /// <summary>
        /// Sizes the sidebar to fit the widest outline item by measuring each item's
        /// text width via FormattedText plus its indentation depth.
        /// </summary>
        private void AutoFitOutlineWidth()
        {
            if (_sidebarCollapsed) return;

            var typeface = new Typeface(
                OutlineTree.FontFamily, OutlineTree.FontStyle,
                OutlineTree.FontWeight, OutlineTree.FontStretch);
            double em = OutlineTree.FontSize;
            double max = 0;

            void Walk(ItemCollection items, int depth)
            {
                foreach (TreeViewItem node in items)
                {
                    if (node.Tag is not OutlineNodeRef) continue;   // ghost add-row: no text to measure
                    var ft = new System.Windows.Media.FormattedText(
                        node.Header?.ToString() ?? string.Empty,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, em, Brushes.White,
                        /*pixelsPerDip*/ 1.0);
                    // 19 px indent per level + 19 px toggle + text + 12 px item padding
                    double w = depth * 19.0 + 19.0 + ft.Width + 12.0;
                    if (w > max) max = w;
                    if (node.Items.Count > 0)
                        Walk(node.Items, depth + 1);
                }
            }

            Walk(OutlineTree.Items, 0);

            // TreeView outer padding (8 px) + sidebar margins + scrollbar gutter (~36 px).
            // Measured widths are logical (the tree lives in the scaled grid); the column
            // is screen px, so convert.
            double target = SbPx(Math.Max(160.0, Math.Min(max + 44.0, SidebarMaxOutlines)));
            _savedOutlinesWidth = target;
            _outlinesFitted = true;
            _sidebarCol.Width = new GridLength(target);
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressOutlineNav) return;   // programmatic re-select (e.g. after a move) must not jump the view
            if (e.NewValue is TreeViewItem item && item.Tag is OutlineNodeRef nref && nref.PageIndex >= 0 && _doc is not null)
            {
                if (nref.PageIndex < _doc.PageCount)
                {
                    RecordNavJump();   // Alt+Left retraces the bookmark hop
                    PageList.SelectedIndex = nref.PageIndex;
                }
            }
        }

        // The TreeView's own scroll viewer swallows the wheel before the outer one sees it, so the
        // Outlines list wouldn't scroll. Forward the wheel to the outer scroll viewer.
        private void OutlineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            OutlineScrollViewer.ScrollToVerticalOffset(OutlineScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        internal void LoadOutlines()
        {
            _outlinesFitted = false;   // triggers auto-fit on next tab switch
            _bmExtraSel.Clear();       // outlines may be gone after a rebuild/undo - selection resets
            OutlineTree.Items.Clear();
            try
            {
                // #103: _doc.Outlines lazily CREATES an empty outlines object on documents that
                // have none, and PdfSharpCore's writer then emits the catalog's /Outlines
                // reference without ever writing the object - a dangling xref entry that strict
                // parsers (PdfSharpCore itself included) refuse to reopen. Peek at the catalog
                // read-only and only touch .Outlines when the document really has one.
                bool hasOutlines = _doc?.Internals.Catalog.Elements.ContainsKey("/Outlines") == true;
                var outlines = hasOutlines ? _doc!.Outlines : null;
                if (outlines is null || outlines.Count == 0)
                {
                    // #133: stay enabled on an editable document so the user can open the panel and
                    // add a first bookmark (the ghost row is then the only entry); read-only
                    // documents keep the old gating.
                    SidebarOutlinesTab.IsEnabled = CanEditBookmarks;
                    if (CanEditBookmarks) OutlineTree.Items.Add(BuildAddBookmarkGhostRow());
                    return;
                }
                SidebarOutlinesTab.IsEnabled = true;
                if (CanEditBookmarks) OutlineTree.Items.Add(BuildAddBookmarkGhostRow());
                AddOutlineItems(OutlineTree.Items, outlines);
            }
            catch
            {
                // Malformed outline - show a placeholder and don't crash
                SidebarOutlinesTab.IsEnabled = false;
            }
        }

        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                string title = PdfOutlines.FixRawUnicodeTitle(outline.Title ?? string.Empty);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(title) ? Loc("Str_Outline_Untitled") : title,
                    IsExpanded = true,
                    Tag = new OutlineNodeRef(outline, outlines, pageIdx),
                    ToolTip = pageIdx >= 0 ? string.Format(Loc("Str_PageLabel"), pageIdx + 1) : null,
                    Style = (Style)FindResource("OutlineItemStyle")
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines);
                target.Add(item);
            }
        }

        /// <summary>
        /// PdfSharpCore only fills DestinationPage when the bookmark's /Dest is a literal array.
        /// Bookmarks pointing at a *named* destination leave it null - wkhtmltopdf writes
        /// /Dest /__WKANCHOR_n into a flat catalog /Dests dictionary, and since most HTML-to-PDF
        /// invoice and statement generators are wkhtmltopdf underneath, that path is common.
        /// Fall back to ResolveDest (Links.cs), which already walks /Dests and the /Names /Dests
        /// name tree for the link layer.
        /// </summary>
        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc!.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }

            var dest = outline.Elements.GetValue("/Dest");
            if (dest is null
                && outline.Elements.GetValue("/A") is PdfSharpCore.Pdf.PdfDictionary action
                && action.Elements.GetName("/S") == "/GoTo")
            {
                dest = action.Elements.GetValue("/D");
            }
            return ResolveDest(dest) ?? -1;
        }

        // ============================================================
        // Bookmark editing (#133): add / rename / delete
        // ============================================================

        /// <summary>Ties a TreeViewItem to its PdfOutline and the collection that contains it.</summary>
        private sealed class OutlineNodeRef
        {
            public readonly PdfSharpCore.Pdf.PdfOutline Outline;
            public readonly PdfSharpCore.Pdf.PdfOutlineCollection Parent;
            public readonly int PageIndex;
            public OutlineNodeRef(PdfSharpCore.Pdf.PdfOutline outline,
                                  PdfSharpCore.Pdf.PdfOutlineCollection parent, int pageIndex)
            { Outline = outline; Parent = parent; PageIndex = pageIndex; }
        }

        // PdfSharpCore cannot save a document opened read-only (owner-password or XRef-fallback
        // opens), so bookmark editing is hidden there rather than failing at save time.
        private bool CanEditBookmarks => _doc is not null && !_doc.IsReadOnly;

        // Multi-select (#133 phase 2). WPF's TreeView is hard single-select, so its built-in
        // selection stays the "primary" item and Ctrl/Shift clicks maintain this extra set on top.
        // Keyed by PdfOutline object so the selection survives tree rebuilds within one document.
        private readonly HashSet<PdfSharpCore.Pdf.PdfOutline> _bmExtraSel = new();
        private bool _suppressOutlineNav;

        /// <summary>All bookmark rows in visual order (optionally only rows currently visible,
        /// i.e. with every ancestor expanded). The ghost add-row is never included.</summary>
        private static void FlattenBookmarkItems(ItemCollection items, bool visibleOnly,
                                                 List<(TreeViewItem Item, OutlineNodeRef Ref)> into)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r) into.Add((it, r));
                if (!visibleOnly || it.IsExpanded)
                    FlattenBookmarkItems(it.Items, visibleOnly, into);
            }
        }

        /// <summary>Paints/clears the extra-selection look. The item template's IsSelected trigger
        /// drives Bd.Background/BorderBrush + Foreground; extras set the same three locally (local
        /// values outrank template triggers) and ClearValue restores normal styling.</summary>
        private void ApplyExtraSelectionVisuals()
        {
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(OutlineTree.Items, visibleOnly: false, all);
            foreach (var (it, r) in all)
            {
                it.ApplyTemplate();
                var bd = it.Template?.FindName("Bd", it) as Border;
                if (_bmExtraSel.Contains(r.Outline))
                {
                    if (bd is not null)
                    {
                        bd.Background = UiKit.Brush("SelectionBg");
                        bd.BorderBrush = UiKit.Brush("PrimaryBrush");
                    }
                    it.Foreground = Brushes.White;   // matches the IsSelected trigger
                }
                else
                {
                    if (bd is not null)
                    {
                        bd.ClearValue(Border.BackgroundProperty);
                        bd.ClearValue(Border.BorderBrushProperty);
                    }
                    it.ClearValue(ForegroundProperty);
                }
            }
        }

        private void ClearBookmarkMultiSelection()
        {
            if (_bmExtraSel.Count == 0) return;
            _bmExtraSel.Clear();
            ApplyExtraSelectionVisuals();
        }

        // True when the click landed on the expand/collapse toggle - those pass through untouched.
        private static bool IsExpanderClick(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
            {
                if (d is System.Windows.Controls.Primitives.ToggleButton) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OutlineTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsExpanderClick(e.OriginalSource as DependencyObject)) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (tvi?.Tag is not OutlineNodeRef nref || !CanEditBookmarks || (!ctrl && !shift))
            {
                // Plain click, ghost row, or empty space: default single-selection behavior.
                ClearBookmarkMultiSelection();
                return;
            }
            if (ctrl)
            {
                // Fold the primary into the set so the whole selection lives in one place, then toggle.
                if (OutlineTree.SelectedItem is TreeViewItem prim && prim.Tag is OutlineNodeRef pr)
                    _bmExtraSel.Add(pr.Outline);
                if (!_bmExtraSel.Add(nref.Outline)) _bmExtraSel.Remove(nref.Outline);
            }
            else
            {
                // Shift: range from the primary to the clicked row, in visible order.
                _bmExtraSel.Clear();
                var flat = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
                FlattenBookmarkItems(OutlineTree.Items, visibleOnly: true, flat);
                var primary = (OutlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
                int ia = primary is null ? -1 : flat.FindIndex(t => ReferenceEquals(t.Ref, primary));
                int ib = flat.FindIndex(t => ReferenceEquals(t.Item, tvi));
                if (ib < 0) return;
                if (ia < 0) ia = ib;
                for (int k = Math.Min(ia, ib); k <= Math.Max(ia, ib); k++)
                    _bmExtraSel.Add(flat[k].Ref.Outline);
            }
            ApplyExtraSelectionVisuals();
            e.Handled = true;   // keep the built-in primary selection where it is
        }

        private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!CanEditBookmarks) return;
            if (e.OriginalSource is TextBox) return;   // inline rename in progress: Delete edits text, not bookmarks
            var primary = (OutlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
            if (e.Key == Key.Delete && (primary is not null || _bmExtraSel.Count > 0))
            {
                e.Handled = true;
                DeleteSelectedBookmarks(primary);
            }
            else if (e.Key == Key.F2 && primary is not null && OutlineTree.SelectedItem is TreeViewItem tvi)
            {
                e.Handled = true;
                BeginInlineRename(tvi, primary);
            }
        }

        /// <summary>The add action lives as a dim first row inside the tree itself (#133): a + glyph
        /// and "Add bookmark", brightening on hover. Tag stays null so the selection handler, the
        /// context menu, width auto-fit, and the refresh walks all treat it as a non-bookmark row.</summary>
        private TreeViewItem BuildAddBookmarkGhostRow()
        {
            var icon = new TextBlock
            {
                Text = "\uE710",   // Segoe MDL2 Add
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var text = new TextBlock { Text = Loc("Str_Ctx_BmAdd"), VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
            panel.Children.Add(icon);
            panel.Children.Add(text);
            var item = new TreeViewItem
            {
                Header = panel,
                ToolTip = Loc("Str_TT_AddBookmark"),
                Style = (Style)FindResource("OutlineItemStyle")
            };
            item.MouseEnter += (_, _2) => panel.Opacity = 1.0;
            item.MouseLeave += (_, _2) => panel.Opacity = 0.55;
            item.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; AddBookmarkInto(null); };
            return item;
        }

        /// <summary>Adds a bookmark pointing at the current page - to the root list, or as a child of
        /// <paramref name="parent"/> - titled "Page N", then drops straight into an inline rename of
        /// the new entry (no dialog). Esc keeps the default title.</summary>
        private void AddBookmarkInto(OutlineNodeRef? parent)
        {
            if (!CanEditBookmarks) return;
            if (parent is not null && !ReferenceEquals(parent.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref (doc was reloaded)
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc!.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();   // bookmark ops ride the document-snapshot undo like crop/page ops do
            var col = parent is null ? _doc.Outlines : parent.Outline.Outlines;
            var added = col.Add(string.Format(Loc("Str_Bm_DefaultTitle"), page + 1), _doc.Pages[page], true);
            PdfOutlines.ScrubStaleOutlineLinkKeys(_doc);
            MarkDirty(true);
            RefreshOutlines();
            if (FindOutlineItem(OutlineTree.Items, added) is { } tvi && tvi.Tag is OutlineNodeRef nref)
            {
                tvi.BringIntoView();
                BeginInlineRename(tvi, nref);
            }
        }

        /// <summary>Swaps a tree item's header for an inline TextBox (rename-in-place; also used right
        /// after adding). Enter or clicking elsewhere commits, Esc cancels.</summary>
        private void BeginInlineRename(TreeViewItem tvi, OutlineNodeRef nref)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref (doc was reloaded)
            string current = PdfOutlines.FixRawUnicodeTitle(nref.Outline.Title ?? string.Empty);
            // UiKit.Field: self-templated, so the OS-default white box / blue focus chrome never shows.
            var box = UiKit.Field();
            box.Text = current;
            box.MinWidth = 110;
            box.FontSize = OutlineTree.FontSize;
            box.Padding = new Thickness(3, 1, 3, 1);
            box.BorderBrush = UiKit.Brush("PrimaryBrush");   // accent border = active in-place edit
            box.CaretBrush = UiKit.Brush("PrimaryBrush");
            bool done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                string t = box.Text.Trim();
                if (t.Length > 0 && t != current)
                {
                    PushDocUndo();
                    nref.Outline.Title = t;   // the setter writes a proper Unicode string, healing mojibake entries
                    MarkDirty(true);
                    RefreshOutlines();
                }
                else
                    tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            void Cancel()
            {
                if (done) return;
                done = true;
                tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            box.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)  { ke.Handled = true; Commit(); }
                if (ke.Key == Key.Escape) { ke.Handled = true; Cancel(); }
            };
            box.LostFocus += (_, _2) => Commit();
            tvi.Header = box;
            // The box can't take focus until it has been laid out - focus it after render.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                (Action)(() => { box.Focus(); box.SelectAll(); }));
        }

        /// <summary>Finds the tree item for a PdfOutline, expanding collapsed ancestors on the way.</summary>
        private static TreeViewItem? FindOutlineItem(ItemCollection items, object outline)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r && ReferenceEquals(r.Outline, outline)) return it;
                if (FindOutlineItem(it.Items, outline) is { } hit) { it.IsExpanded = true; return hit; }
            }
            return null;
        }

        /// <summary>Deletes the multi-selection if one exists, plus the clicked/primary item. One
        /// confirm covers the whole set; one undo entry restores it.</summary>
        private void DeleteSelectedBookmarks(OutlineNodeRef? clicked)
        {
            if (!CanEditBookmarks) return;
            if (clicked is not null && !ReferenceEquals(clicked.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref (doc was reloaded)

            // Gather targets: the extra set, the primary, and the clicked item, deduplicated.
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(OutlineTree.Items, visibleOnly: false, all);
            var targets = new List<OutlineNodeRef>();
            foreach (var (_, r) in all)
                if (_bmExtraSel.Contains(r.Outline)) targets.Add(r);
            void AddTarget(OutlineNodeRef? r)
            {
                if (r is not null && !targets.Any(t => ReferenceEquals(t.Outline, r.Outline))) targets.Add(r);
            }
            AddTarget((OutlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef);
            AddTarget(clicked);
            if (targets.Count == 0) return;

            // A target with a selected ancestor is covered by deleting the ancestor - drop it so the
            // remaining targets are independent (their parent collections stay valid during removal).
            var chosen = new HashSet<object>(targets.Select(t => (object)t.Outline));
            bool Covered(PdfSharpCore.Pdf.PdfOutline o)
            {
                for (var p = o.Parent; p is not null; p = p.Parent)
                    if (chosen.Contains(p)) return true;
                return false;
            }
            targets = targets.Where(t => !Covered(t.Outline)).ToList();

            int total = targets.Sum(t => 1 + PdfOutlines.CountOutlines(t.Outline.Outlines));
            if (total > 1)
            {
                string msg = targets.Count == 1
                    ? string.Format(Loc("Str_Bm_DeleteKids"), total - 1)
                    : string.Format(Loc("Str_Bm_DeleteMulti"), total);
                var r = KillerDialog.Show(this, msg, Loc("Str_Dlg_AppTitle"),
                                          MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            PushDocUndo();   // one Ctrl+Z restores the whole set
            foreach (var t in targets)
                PdfOutlines.RemoveOutlineRecursive(t.Parent, t.Outline);
            PdfOutlines.ScrubStaleOutlineLinkKeys(_doc);
            MarkDirty(true);
            RefreshOutlines();   // also clears _bmExtraSel via LoadOutlines
        }

        /// <summary>Moves a bookmark one position up or down among its siblings.</summary>
        private void MoveBookmark(OutlineNodeRef nref, int delta)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref (doc was reloaded)
            int i = nref.Parent.IndexOf(nref.Outline);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= nref.Parent.Count) return;
            PushDocUndo();
            // RemoveAt drops the object from the xref table; Insert/Add puts it straight back.
            nref.Parent.RemoveAt(i);
            if (j >= nref.Parent.Count) nref.Parent.Add(nref.Outline);
            else nref.Parent.Insert(j, nref.Outline);
            PdfOutlines.ScrubStaleOutlineLinkKeys(_doc);
            MarkDirty(true);
            RefreshOutlines();
            // Keep the moved item selected, without the page-jump side effect.
            if (FindOutlineItem(OutlineTree.Items, nref.Outline) is { } moved)
            {
                _suppressOutlineNav = true;
                try { moved.IsSelected = true; moved.BringIntoView(); }
                finally { _suppressOutlineNav = false; }
            }
        }

        /// <summary>Repoints a bookmark at the current page as a plain go-to-page destination.</summary>
        private void SetBookmarkDestination(OutlineNodeRef nref)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref (doc was reloaded)
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc!.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();
            nref.Outline.DestinationPage = _doc.Pages[page];
            // Plain jump: /XYZ null null null keeps the reader's current zoom/position behavior.
            nref.Outline.PageDestinationType = PdfSharpCore.Pdf.PdfPageDestinationType.Xyz;
            nref.Outline.Left = double.NaN;
            nref.Outline.Top = double.NaN;
            nref.Outline.Zoom = double.NaN;
            MarkDirty(true);
            RefreshOutlines();
        }

        /// <summary>Removes every bookmark in the document (one confirm, one undo entry).</summary>
        private void DeleteAllBookmarks()
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (_doc.Internals.Catalog.Elements["/Outlines"] is null) return;   // nothing to do, and never plant one
            if (_doc.Outlines.Count == 0) return;
            var r = KillerDialog.Show(this, Loc("Str_Bm_DeleteAllConfirm"), Loc("Str_Dlg_AppTitle"),
                                      MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            PushDocUndo();
            while (_doc.Outlines.Count > 0)
                PdfOutlines.RemoveOutlineRecursive(_doc.Outlines, _doc.Outlines[_doc.Outlines.Count - 1]);
            PdfOutlines.ScrubStaleOutlineLinkKeys(_doc);
            MarkDirty(true);
            RefreshOutlines();
        }

        // CountOutlines, RemoveOutlineRecursive and the stale-link-key scrubs live in
        // Services/PdfOutlines.cs (KillerUI refactor) - pure functions over the outline tree.

        /// <summary>Rebuilds the outline panel after an edit, keeping collapsed branches collapsed
        /// (the PdfOutline objects survive the rebuild, so they key the state).</summary>
        private void RefreshOutlines()
        {
            var collapsed = new HashSet<object>();
            void Capture(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (!it.IsExpanded && it.Tag is OutlineNodeRef r) collapsed.Add(r.Outline);
                    Capture(it.Items);
                }
            }
            Capture(OutlineTree.Items);
            // LoadOutlines re-arms the sidebar width auto-fit (_outlinesFitted = false), which is right
            // for a NEW document but wrong here: after a bookmark edit the next tab switch would re-fit
            // and override the width the user dragged the sidebar to. The panel must stay where the
            // user put it - preserve the flag across the rebuild.
            bool fitted = _outlinesFitted;
            LoadOutlines();
            _outlinesFitted = fitted;
            if (collapsed.Count == 0) return;
            void Restore(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (it.Tag is OutlineNodeRef r && collapsed.Contains(r.Outline)) it.IsExpanded = false;
                    Restore(it.Items);
                }
            }
            Restore(OutlineTree.Items);
        }

        /// <summary>Right-click on the outline panel: bookmark menu for the item under the cursor,
        /// or the add-bookmark menu on empty space. Hidden entirely on read-only documents.</summary>
        private void OutlineTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanEditBookmarks) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            if (tvi?.Tag is OutlineNodeRef nref)
            {
                // Right-click outside the multi-selection collapses it to the clicked item (the
                // file-explorer convention); inside it, the menu acts on the whole set.
                bool inMulti = _bmExtraSel.Contains(nref.Outline);
                if (!inMulti) ClearBookmarkMultiSelection();
                _suppressOutlineNav = true;
                try { tvi.IsSelected = true; }   // WPF doesn't select on right-click by itself
                finally { _suppressOutlineNav = false; }

                if (inMulti && _bmExtraSel.Count > 1)
                {
                    menu.Items.Add(MakeMenuItem($"{Loc("Str_Ctx_BmDelete")} ({_bmExtraSel.Count})",
                                                (_, _2) => DeleteSelectedBookmarks(nref), "Delete"));
                }
                else
                {
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmRename"), (_, _2) => BeginInlineRename(tvi, nref), "F2"));
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmAddChild"), (_, _2) => AddBookmarkInto(nref)));
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmSetDest"), (_, _2) => SetBookmarkDestination(nref)));
                    menu.Items.Add(new Separator());
                    int idx = nref.Parent.IndexOf(nref.Outline);
                    var up = MakeMenuItem(Loc("Str_Ctx_BmMoveUp"), (_, _2) => MoveBookmark(nref, -1));
                    up.IsEnabled = idx > 0;
                    menu.Items.Add(up);
                    var down = MakeMenuItem(Loc("Str_Ctx_BmMoveDown"), (_, _2) => MoveBookmark(nref, +1));
                    down.IsEnabled = idx >= 0 && idx < nref.Parent.Count - 1;
                    menu.Items.Add(down);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmDelete"), (_, _2) => DeleteSelectedBookmarks(nref), "Delete"));
                }
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmAdd"), (_, _2) => AddBookmarkInto(null)));
                bool hasAny = _doc?.Internals.Catalog.Elements["/Outlines"] is not null
                              && OutlineTree.Items.Count > 1;   // ghost row + at least one real entry
                if (hasAny)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmDeleteAll"), (_, _2) => DeleteAllBookmarks()));
                }
            }
            menu.PlacementTarget = OutlineTree;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static TreeViewItem? OutlineItemAt(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);   // e.g. a Run inside the header
            return d as TreeViewItem;
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolLine_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Line);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolShape_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Shape);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }
    }
}
