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
using Microsoft.Win32;
using KillerPDF.Services;

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
            OutlineExpandCollapseAllButton.Visibility = Visibility.Collapsed;
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
            OutlineExpandCollapseAllButton.Visibility = Visibility.Visible;
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
            CaptureOutlineExpandState();   // remember the outgoing tree's expanded branches (per file)
            _outlineStateFile = _originalFile ?? _currentFile;
            OutlineTree.Items.Clear();
            try
            {
                IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> outlines = ReadEngineBookmarks();
                if (outlines.Count == 0)
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
                ApplyOutlineExpandState();   // re-apply the user's expand/collapse choices for this file
            }
            catch
            {
                // Malformed outline - show a placeholder and don't crash
                SidebarOutlinesTab.IsEnabled = false;
            }
            finally
            {
                UpdateOutlineExpandCollapseButton();
            }
        }

        private IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> ReadEngineBookmarks()
        {
            if (_doc is null) return [];
            byte[] bytes;
            if (_doc.IsReadOnly && !string.IsNullOrWhiteSpace(_currentFile) && File.Exists(_currentFile))
                bytes = File.ReadAllBytes(_currentFile);
            else
            {
                using var stream = new MemoryStream();
                _doc.Save(stream);
                bytes = stream.ToArray();
            }
            return PdfEngineIntegration.ReadBookmarks(bytes);
        }

        private void AddOutlineItems(ItemCollection target,
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> outlines, int depth = 0)
        {
            foreach (KillerPdf.Engine.Documents.PdfBookmarkInfo outline in outlines)
            {
                int pageIdx = outline.DestinationPageIndex ?? -1;
                string title = outline.Title;
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(title) ? Loc("Str_Outline_Untitled") : title,
                    // Top level starts open, deeper levels start folded (the Acrobat default) - a
                    // deep outline is otherwise a wall on open. ApplyOutlineExpandState overrides
                    // this with the user's own choices once the file has been seen this session.
                    IsExpanded = depth == 0,
                    Tag = new OutlineNodeRef(outline),
                    ToolTip = pageIdx >= 0 ? string.Format(Loc("Str_PageLabel"), pageIdx + 1) : null,
                    Style = (Style)FindResource("OutlineItemStyle")
                };
                item.Expanded += OutlineItemExpansionChanged;
                item.Collapsed += OutlineItemExpansionChanged;
                if (outline.Children.Count > 0)
                    AddOutlineItems(item.Items, outline.Children, depth + 1);
                target.Add(item);
            }
        }

        private void OutlineItemExpansionChanged(object sender, RoutedEventArgs e) =>
            UpdateOutlineExpandCollapseButton();

        private void OutlineExpandCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = HasExpandedOutline(OutlineTree.Items);
            SetOutlineExpansion(OutlineTree.Items, !collapse);
            CaptureOutlineExpandState();
            UpdateOutlineExpandCollapseButton();
        }

        private static bool HasExpandedOutline(ItemCollection items)
        {
            foreach (var value in items)
                if (value is TreeViewItem item && item.Tag is OutlineNodeRef &&
                    item.Items.Count > 0 && item.IsExpanded)
                    return true;
            return false;
        }

        private static void SetOutlineExpansion(ItemCollection items, bool expanded)
        {
            foreach (var value in items)
            {
                if (value is not TreeViewItem item || item.Tag is not OutlineNodeRef) continue;
                if (item.Items.Count > 0) item.IsExpanded = expanded;
                SetOutlineExpansion(item.Items, expanded);
            }
        }

        private void UpdateOutlineExpandCollapseButton()
        {
            bool hasBranches = OutlineTree.Items.OfType<TreeViewItem>()
                .Any(item => item.Tag is OutlineNodeRef && item.Items.Count > 0);
            bool collapse = hasBranches && HasExpandedOutline(OutlineTree.Items);
            string key = collapse ? "Str_Outline_CollapseAll" : "Str_Outline_ExpandAll";
            OutlineExpandCollapseAllButton.IsEnabled = hasBranches;
            OutlineExpandVerticalStroke.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            OutlineExpandCollapseAllButton.SetResourceReference(FrameworkElement.ToolTipProperty, key);
        }

        // Sticky expand/collapse per file, keyed by index path ("2/0/1", ghost row excluded).
        // LoadOutlines rebuilds the tree from scratch on every tab switch and temp-reload, which
        // used to re-expand everything the user had folded - the tree read as force-expanded.
        // Keyed by _originalFile (not _currentFile, which temp-reload repoints at a temp path).
        private readonly Dictionary<string, HashSet<string>> _outlineExpandState = [];
        private string? _outlineStateFile;

        /// <summary>Records which outline nodes are expanded in the tree currently on screen,
        /// against the file it belongs to. Runs before LoadOutlines clears the tree.</summary>
        private void CaptureOutlineExpandState()
        {
            if (_outlineStateFile is null) return;
            var expanded = new HashSet<string>();
            bool any = false;
            void Walk(ItemCollection items, string prefix)
            {
                int i = 0;
                foreach (var o in items)
                {
                    if (o is not TreeViewItem it || it.Tag is not OutlineNodeRef) continue;
                    string path = prefix.Length == 0 ? i.ToString() : prefix + "/" + i;
                    any = true;
                    if (it.IsExpanded) expanded.Add(path);
                    Walk(it.Items, path);
                    i++;
                }
            }
            Walk(OutlineTree.Items, "");
            if (any) _outlineExpandState[_outlineStateFile] = expanded;
        }

        /// <summary>Restores the recorded expand/collapse state for the freshly built tree. A file
        /// not seen this session keeps the depth default from AddOutlineItems.</summary>
        private void ApplyOutlineExpandState()
        {
            if (_outlineStateFile is null
                || !_outlineExpandState.TryGetValue(_outlineStateFile, out var expanded)) return;
            void Walk(ItemCollection items, string prefix)
            {
                int i = 0;
                foreach (var o in items)
                {
                    if (o is not TreeViewItem it || it.Tag is not OutlineNodeRef) continue;
                    string path = prefix.Length == 0 ? i.ToString() : prefix + "/" + i;
                    it.IsExpanded = expanded.Contains(path);
                    Walk(it.Items, path);
                    i++;
                }
            }
            Walk(OutlineTree.Items, "");
        }

        // ============================================================
        // Bookmark editing (#133): add / rename / delete
        // ============================================================

        /// <summary>Ties a row to engine-owned bookmark data and stable PDF object identity.</summary>
        private sealed class OutlineNodeRef(KillerPdf.Engine.Documents.PdfBookmarkInfo bookmark)
        {
            public readonly KillerPdf.Engine.Documents.PdfBookmarkInfo Bookmark = bookmark;
            public (int ObjectNumber, int Generation) Identity =>
                (Bookmark.ObjectNumber, Bookmark.Generation);
            public int PageIndex => Bookmark.DestinationPageIndex ?? -1;
        }

        private void ApplyEngineBookmarkEdit(
            Func<IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo>,
                IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo>> edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo>? replacement = null;
            SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                finalizeSavedFile: path =>
                {
                    replacement = edit(PdfEngineIntegration.ReadBookmarks(File.ReadAllBytes(path)));
                    PdfEngineIntegration.ReplaceBookmarks(path, replacement);
                });
            LoadOutlines();
            MarkDirty(true);
        }

        private static IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> TransformBookmarks(
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> items,
            (int ObjectNumber, int Generation) identity,
            Func<KillerPdf.Engine.Documents.PdfBookmarkInfo,
                KillerPdf.Engine.Documents.PdfBookmarkInfo> transform)
        {
            return [.. items.Select(item => item.ObjectNumber == identity.ObjectNumber
                    && item.Generation == identity.Generation
                ? transform(item)
                : item with { Children = TransformBookmarks(item.Children, identity, transform) })];
        }

        private static IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> RemoveBookmarks(
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> items,
            IReadOnlySet<(int ObjectNumber, int Generation)> identities)
        {
            return
            [
                .. items.Where(item =>
                        !identities.Contains((item.ObjectNumber, item.Generation)))
                    .Select(item => item with
                    {
                        Children = RemoveBookmarks(item.Children, identities)
                    })
            ];
        }

        private static List<KillerPdf.Engine.Documents.PdfBookmarkInfo> MoveBookmarkModel(
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> items,
            (int ObjectNumber, int Generation) identity, int delta)
        {
            var result = items.ToList();
            int index = result.FindIndex(item =>
                item.ObjectNumber == identity.ObjectNumber && item.Generation == identity.Generation);
            if (index >= 0)
            {
                int target = index + delta;
                if (target >= 0 && target < result.Count)
                    (result[index], result[target]) = (result[target], result[index]);
                return result;
            }
            return [.. result.Select(item => item with
            {
                Children = MoveBookmarkModel(item.Children, identity, delta)
            })];
        }

        private static (int Index, int Count) FindBookmarkSiblingPosition(
            IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> items,
            (int ObjectNumber, int Generation) identity)
        {
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if ((item.ObjectNumber, item.Generation) == identity) return (index, items.Count);
                var nested = FindBookmarkSiblingPosition(item.Children, identity);
                if (nested.Index >= 0) return nested;
            }
            return (-1, 0);
        }

        // PdfSharpCore cannot save a document opened read-only (owner-password or XRef-fallback
        // opens), so bookmark editing is hidden there rather than failing at save time.
        private bool CanEditBookmarks => _doc is not null && !_doc.IsReadOnly;

        // Multi-select (#133 phase 2). WPF's TreeView is hard single-select, so its built-in
        // selection stays the "primary" item and Ctrl/Shift clicks maintain this extra set on top.
        // Keyed by stable engine bookmark identity so no parser objects survive a tree rebuild.
        private readonly HashSet<(int ObjectNumber, int Generation)> _bmExtraSel = [];
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
                if (_bmExtraSel.Contains(r.Identity))
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
                    _bmExtraSel.Add(pr.Identity);
                if (!_bmExtraSel.Add(nref.Identity)) _bmExtraSel.Remove(nref.Identity);
            }
            else
            {
                // Shift: range from the primary to the clicked row, in visible order.
                _bmExtraSel.Clear();
                var flat = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
                FlattenBookmarkItems(OutlineTree.Items, visibleOnly: true, flat);
                int ia = (OutlineTree.SelectedItem as TreeViewItem)?.Tag is not OutlineNodeRef primary ? -1 : flat.FindIndex(t => ReferenceEquals(t.Ref, primary));
                int ib = flat.FindIndex(t => ReferenceEquals(t.Item, tvi));
                if (ib < 0) return;
                if (ia < 0) ia = ib;
                for (int k = Math.Min(ia, ib); k <= Math.Max(ia, ib); k++)
                    _bmExtraSel.Add(flat[k].Ref.Identity);
            }
            ApplyExtraSelectionVisuals();
            e.Handled = true;   // keep the built-in primary selection where it is
        }

        private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;   // inline rename in progress: Delete edits text, not bookmarks
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Undo_Click(this, e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Z
                && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) Redo_Click(this, e);
                e.Handled = true;
                return;
            }
            if (!CanEditBookmarks) return;
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
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc!.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            string title = string.Format(Loc("Str_Bm_DefaultTitle"), page + 1);
            var added = new KillerPdf.Engine.Documents.PdfBookmarkInfo
            {
                ObjectNumber = 0,
                Generation = 0,
                Title = title,
                IsOpen = true,
                Style = KillerPdf.Engine.Authoring.PdfBookmarkStyle.Regular,
                DestinationPageIndex = page,
                Destination = KillerPdf.Engine.Authoring.PdfDestination.At(),
                Children = []
            };
            ApplyEngineBookmarkEdit(items => parent is null
                ? [.. items, added]
                : TransformBookmarks(items, parent.Identity,
                    item => item with { Children = [.. item.Children, added] }));
            var rows = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(OutlineTree.Items, visibleOnly: false, rows);
            var (Item, Ref) = rows.LastOrDefault(row => row.Ref.Bookmark.Title == title
                && row.Ref.PageIndex == page);
            if (Item is { } tvi && tvi.Tag is OutlineNodeRef nref)
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
            string current = nref.Bookmark.Title;
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
                    ApplyEngineBookmarkEdit(items => TransformBookmarks(
                        items, nref.Identity, item => item with { Title = t }));
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

        /// <summary>Finds the tree item for a bookmark identity, expanding collapsed ancestors.</summary>
        private static TreeViewItem? FindOutlineItem(
            ItemCollection items, (int ObjectNumber, int Generation) identity)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r && r.Identity == identity) return it;
                if (FindOutlineItem(it.Items, identity) is { } hit) { it.IsExpanded = true; return hit; }
            }
            return null;
        }

        /// <summary>Deletes the multi-selection if one exists, plus the clicked/primary item. One
        /// confirm covers the whole set; one undo entry restores it.</summary>
        private void DeleteSelectedBookmarks(OutlineNodeRef? clicked)
        {
            if (!CanEditBookmarks) return;

            // Gather targets: the extra set, the primary, and the clicked item, deduplicated.
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(OutlineTree.Items, visibleOnly: false, all);
            var targets = new List<OutlineNodeRef>();
            foreach (var (_, r) in all)
                if (_bmExtraSel.Contains(r.Identity)) targets.Add(r);
            void AddTarget(OutlineNodeRef? r)
            {
                if (r is not null && !targets.Any(t => t.Identity == r.Identity)) targets.Add(r);
            }
            AddTarget((OutlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef);
            AddTarget(clicked);
            if (targets.Count == 0) return;

            // A target with a selected ancestor is covered by deleting the ancestor - drop it so the
            // remaining targets are independent (their parent collections stay valid during removal).
            var chosen = new HashSet<(int ObjectNumber, int Generation)>(targets.Select(t => t.Identity));
            bool Covered(OutlineNodeRef node)
            {
                bool FindAncestor(IReadOnlyList<KillerPdf.Engine.Documents.PdfBookmarkInfo> items,
                    HashSet<(int, int)> ancestors)
                {
                    foreach (var item in items)
                    {
                        var id = (item.ObjectNumber, item.Generation);
                        if (id == node.Identity) return ancestors.Any(chosen.Contains);
                        var nested = new HashSet<(int, int)>(ancestors) { id };
                        if (FindAncestor(item.Children, nested)) return true;
                    }
                    return false;
                }
                return FindAncestor(ReadEngineBookmarks(), []);
            }
            targets = [.. targets.Where(t => !Covered(t))];

            int Count(KillerPdf.Engine.Documents.PdfBookmarkInfo item) =>
                1 + item.Children.Sum(Count);
            int total = targets.Sum(t => Count(t.Bookmark));
            if (total > 1)
            {
                string msg = targets.Count == 1
                    ? string.Format(Loc("Str_Bm_DeleteKids"), total - 1)
                    : string.Format(Loc("Str_Bm_DeleteMulti"), total);
                var r = KillerDialog.Show(this, msg, Loc("Str_Dlg_AppTitle"),
                                          MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            var removed = new HashSet<(int ObjectNumber, int Generation)>(targets.Select(t => t.Identity));
            ApplyEngineBookmarkEdit(items => RemoveBookmarks(items, removed));
        }

        /// <summary>Moves a bookmark one position up or down among its siblings.</summary>
        private void MoveBookmark(OutlineNodeRef nref, int delta)
        {
            if (!CanEditBookmarks) return;
            ApplyEngineBookmarkEdit(items => MoveBookmarkModel(items, nref.Identity, delta));
        }

        /// <summary>Repoints a bookmark at the current page as a plain go-to-page destination.</summary>
        private void SetBookmarkDestination(OutlineNodeRef nref)
        {
            if (!CanEditBookmarks) return;
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc!.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            ApplyEngineBookmarkEdit(items => TransformBookmarks(items, nref.Identity,
                item => item with
                {
                    DestinationPageIndex = page,
                    NamedDestination = null,
                    Destination = KillerPdf.Engine.Authoring.PdfDestination.At()
                }));
        }

        /// <summary>Removes every bookmark in the document (one confirm, one undo entry).</summary>
        private void DeleteAllBookmarks()
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (ReadEngineBookmarks().Count == 0) return;
            var r = KillerDialog.Show(this, Loc("Str_Bm_DeleteAllConfirm"), Loc("Str_Dlg_AppTitle"),
                                      MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            ApplyEngineBookmarkEdit(_ => []);
        }

        /// <summary>Rebuilds the outline panel after an edit, keeping every branch's expand/collapse
        /// state when bookmark object identities survive the rebuild.</summary>
        private void RefreshOutlines()
        {
            // Both states are keyed by surviving engine identities. Index paths shift when a
            // bookmark is added or removed, so the path-keyed state LoadOutlines restores can land
            // on the wrong siblings after an edit. This object-keyed pass corrects every node that
            // existed before the edit; only genuinely new nodes keep the build default.
            var expandedBy = new Dictionary<(int ObjectNumber, int Generation), bool>();
            void Capture(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (it.Tag is OutlineNodeRef r) expandedBy[r.Identity] = it.IsExpanded;
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
            if (expandedBy.Count == 0) return;
            void Restore(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (it.Tag is OutlineNodeRef r && expandedBy.TryGetValue(r.Identity, out bool ex))
                        it.IsExpanded = ex;
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
            var menu = MakeThemedMenu();
            if (tvi?.Tag is OutlineNodeRef nref)
            {
                // Right-click outside the multi-selection collapses it to the clicked item (the
                // file-explorer convention); inside it, the menu acts on the whole set.
                bool inMulti = _bmExtraSel.Contains(nref.Identity);
                if (!inMulti) ClearBookmarkMultiSelection();
                _suppressOutlineNav = true;
                try { tvi.IsSelected = true; }   // WPF doesn't select on right-click by itself
                finally { _suppressOutlineNav = false; }

                if (inMulti && _bmExtraSel.Count > 1)
                {
                    menu.Items.Add(MakeMenuItem($"{Loc("Str_Ctx_BmDelete")} ({_bmExtraSel.Count})",
                                                (_, _2) => DeleteSelectedBookmarks(nref), "Delete", ""));
                }
                else
                {
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmRename"), (_, _2) => BeginInlineRename(tvi, nref), "F2", ""));
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmAddChild"), (_, _2) => AddBookmarkInto(nref), glyph: ""));
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmSetDest"), (_, _2) => SetBookmarkDestination(nref), glyph: ""));
                    menu.Items.Add(new Separator());
                    (int idx, int siblingCount) = FindBookmarkSiblingPosition(
                        ReadEngineBookmarks(), nref.Identity);
                    var up = MakeMenuItem(Loc("Str_Ctx_BmMoveUp"), (_, _2) => MoveBookmark(nref, -1), glyph: "");
                    up.IsEnabled = idx > 0;
                    menu.Items.Add(up);
                    var down = MakeMenuItem(Loc("Str_Ctx_BmMoveDown"), (_, _2) => MoveBookmark(nref, +1), glyph: "");
                    down.IsEnabled = idx >= 0 && idx < siblingCount - 1;
                    menu.Items.Add(down);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmDelete"), (_, _2) => DeleteSelectedBookmarks(nref), "Delete", ""));
                }
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmAdd"), (_, _2) => AddBookmarkInto(null), glyph: ""));
                bool hasAny = OutlineTree.Items.Count > 1;   // ghost row + at least one real entry
                if (hasAny)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_BmDeleteAll"), (_, _2) => DeleteAllBookmarks(), glyph: ""));
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
        private void ToolFormField_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.FormField);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolLine_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Line);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolShape_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Shape);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolMeasure_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Measure);
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
