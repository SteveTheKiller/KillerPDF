using System.Globalization;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>The panels the sidebar can show, one rail button each.</summary>
    internal enum SidebarPanel { Pages, Outlines, Comments, Attachments, Layers, Signatures }

    // ============================================================
    // Sidebar panels on the rail
    //
    // Pages and Outline keep their own controls (Shell/SidebarOutline.cs). Comments, Attachments,
    // Layers and Signatures share one list panel built here, styled like the Outline tree. Each
    // reads the working copy in the background, so a large file never stalls the window, and a
    // newer refresh drops the result of an older one. Unavailable rail buttons are hidden; the
    // open panel's button is lit while the sidebar is open.
    // ============================================================
    public partial class MainWindow
    {
        private SidebarPanel _sidebarPanel = SidebarPanel.Pages;
        private double _savedListPanelWidth = 260;
        private CommentsFilter _commentsFilter = CommentsFilter.All;
        private int _sidebarPanelGeneration;
        private DispatcherTimer? _sidebarPanelRefreshTimer;
        private TreeView? _sidebarPanelTree;

        private enum CommentsFilter { All, Comments, Measurements }

        private void SidebarCommentsTab_Click(object sender, RoutedEventArgs e) => OpenSidebarPanel(SidebarPanel.Comments);
        private void SidebarAttachmentsTab_Click(object sender, RoutedEventArgs e) => OpenSidebarPanel(SidebarPanel.Attachments);
        private void SidebarLayersTab_Click(object sender, RoutedEventArgs e) => OpenSidebarPanel(SidebarPanel.Layers);
        private void SidebarSignaturesTab_Click(object sender, RoutedEventArgs e) => OpenSidebarPanel(SidebarPanel.Signatures);

        /// <summary>
        /// A rail panel button: shows that panel, opening the sidebar when it is closed. The open
        /// panel's own button opens the sidebar position menu.
        /// </summary>
        private void OpenSidebarPanel(SidebarPanel panel)
        {
            if (!_sidebarCollapsed && _sidebarPanel == panel)
            {
                var button = RailPanelButtons().First(entry => entry.Panel == panel).Button;
                var menu = MakeThemedMenu();
                AppendSidebarSideSection(menu);
                FlyoutPlacement.UsePane(ContentPane, _sidebarSide);
                FlyoutPlacement.Attach(menu, button);
                menu.IsOpen = true;
                return;
            }
            switch (panel)
            {
                case SidebarPanel.Pages: SwitchSidebarToPagesTab(); break;
                case SidebarPanel.Outlines: SwitchSidebarToOutlinesTab(); break;
                default: SwitchSidebarToListPanel(panel); break;
            }
            if (_sidebarCollapsed) SidebarToggle_Click(this, new RoutedEventArgs());
            SyncRailPanelButtons();
        }

        // ── Widths: Pages, Outline and the list panels each remember their own ───────────────

        private double SidebarMaxFor(SidebarPanel panel) =>
            _sidebarSide.IsStrip() ? SidebarMaxStrip
            : panel == SidebarPanel.Pages ? SidebarMaxPages : SidebarMaxOutlines;

        /// <summary>Remembers <paramref name="width"/> (screen pixels) for the open panel.</summary>
        private void RememberSidebarWidth(double width)
        {
            double clamped = Math.Min(width, SbPx(SidebarMaxFor(_sidebarPanel)));
            switch (_sidebarPanel)
            {
                case SidebarPanel.Pages: _savedPagesWidth = clamped; break;
                case SidebarPanel.Outlines: _savedOutlinesWidth = clamped; break;
                default: _savedListPanelWidth = clamped; break;
            }
        }

        /// <summary>The width the open panel last had, in screen pixels.</summary>
        private double SavedSidebarWidth() => _sidebarPanel switch
        {
            SidebarPanel.Pages => _savedPagesWidth,
            SidebarPanel.Outlines => _savedOutlinesWidth,
            _ => _savedListPanelWidth,
        };

        // ── Switching ───────────────────────────────────────────────────────────────────────

        private void SwitchSidebarToListPanel(SidebarPanel panel)
        {
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0) RememberSidebarWidth(_sidebarCol.ActualWidth);
            _sidebarPanel = panel;
            _sidebarShowingOutlines = false;
            PageList.Visibility = Visibility.Collapsed;
            OutlineScrollViewer.Visibility = Visibility.Collapsed;
            OutlineExpandCollapseAllButton.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarListPanel.Visibility = Visibility.Visible;
            SidebarPanelTitle.SetResourceReference(TextBlock.TextProperty, panel switch
            {
                SidebarPanel.Comments => "Str_TabComments",
                SidebarPanel.Attachments => "Str_TabAttachments",
                SidebarPanel.Layers => "Str_TabLayers",
                _ => "Str_TabSignatures",
            });
            SidebarPanelEditButton.Visibility = Visibility.Visible;
            SidebarPanelEditButton.SetResourceReference(FrameworkElement.ToolTipProperty, panel switch
            {
                SidebarPanel.Comments => "Str_DocumentTools_Comments",
                SidebarPanel.Attachments => "Str_DocumentTools_EditAttachments",
                SidebarPanel.Layers => "Str_DocumentTools_EditLayers",
                _ => "Str_Lbl_DigitalSig",
            });
            if (panel == SidebarPanel.Signatures)
                SidebarPanelEditButton.ToolTip = ShortcutTip("Str_Lbl_DigitalSig", "CtrlShift:G");
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SbPx(SidebarMaxFor(panel));
            if (!_sidebarCollapsed)
            {
                double target = _savedListPanelWidth;
                Dispatcher.BeginInvoke(DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
            SyncRailPanelButtons();
            RefreshSidebarPanel();
        }

        /// <summary>Shows Pages or Outline in the shared header and hides the list panel.</summary>
        private void ShowControlPanelHeader(SidebarPanel panel)
        {
            SidebarListPanel.Visibility = Visibility.Collapsed;
            SidebarPanelEditButton.Visibility = Visibility.Collapsed;
            SidebarPanelTitle.SetResourceReference(TextBlock.TextProperty,
                panel == SidebarPanel.Outlines ? "Str_TabOutlines" : "Str_TabPages");
            SyncRailPanelButtons();
        }

        /// <summary>Lights the open panel's rail button while the sidebar is open.</summary>
        private void SyncRailPanelButtons()
        {
            foreach (var (button, panel) in RailPanelButtons())
                button.Tag = !_sidebarCollapsed && panel == _sidebarPanel ? "on" : null;
        }

        private IEnumerable<(Button Button, SidebarPanel Panel)> RailPanelButtons()
        {
            yield return (SidebarPagesTab, SidebarPanel.Pages);
            yield return (SidebarOutlinesTab, SidebarPanel.Outlines);
            yield return (SidebarCommentsTab, SidebarPanel.Comments);
            yield return (SidebarAttachmentsTab, SidebarPanel.Attachments);
            yield return (SidebarLayersTab, SidebarPanel.Layers);
            yield return (SidebarSignaturesTab, SidebarPanel.Signatures);
        }

        /// <summary>The panel buttons work only while a document is open. Outline's button is
        /// further gated by LoadOutlines, which knows whether there is anything to show.</summary>
        private void SyncRailPanelAvailability(bool hasDoc)
        {
            SyncPageNavigationButtons();
            foreach (var (button, panel) in RailPanelButtons())
                if (panel != SidebarPanel.Outlines) button.IsEnabled = hasDoc;
            if (!hasDoc) SidebarOutlinesTab.IsEnabled = false;
            ScheduleToolbarAvailabilityRefresh();
        }

        private void SidebarPanelEdit_Click(object sender, RoutedEventArgs e)
        {
            switch (_sidebarPanel)
            {
                case SidebarPanel.Comments: EditComments(); break;
                case SidebarPanel.Attachments: EditAttachments(); break;
                case SidebarPanel.Layers: EditLayers(); break;
                case SidebarPanel.Signatures: OpenSidebarSigning(); break;
            }
        }

        private void FillSidebarPanelMenu(ContextMenu menu, DependencyObject? source, SidebarPanel? panel = null)
        {
            TreeViewItem? clicked = null;
            for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is TreeViewItem item) { clicked = item; break; }
            if (clicked is not null) clicked.IsSelected = true;
            void Add(string key, Action action) => menu.Items.Add(MakeMenuItem(Loc(key), (_, _) => action()));
            switch (panel ?? _sidebarPanel)
            {
                case SidebarPanel.Attachments:
                    Add("Str_Attachment_Add", AddSidebarAttachment);
                    if (clicked?.Tag is PdfAttachmentInfo attachment)
                        Add("Str_Btn_Save", () => SaveAttachmentCopy(attachment));
                    Add("Str_DocumentTools_EditAttachments", EditAttachments);
                    break;
                case SidebarPanel.Comments:
                    Add("Str_Comments_Add", () => EditComments(addNew: true));
                    Add("Str_DocumentTools_Comments", () => EditComments());
                    menu.Items.Add(new Separator());
                    Add("Str_Measure_Distance", () => SelectMeasurement(MeasurementKind.Distance));
                    Add("Str_Measure_Perimeter", () => SelectMeasurement(MeasurementKind.Perimeter));
                    Add("Str_Measure_Area", () => SelectMeasurement(MeasurementKind.Area));
                    Add("Str_Measure_Angle", () => SelectMeasurement(MeasurementKind.Angle));
                    Add("Str_Measure_TT_Scale", () => OpenMeasureScale());
                    Add("Str_DocumentTools_Measurements", ShowMeasurementReport);
                    break;
                case SidebarPanel.Layers:
                    Add("Str_DocumentTools_EditLayers", EditLayers);
                    break;
                case SidebarPanel.Signatures:
                    Add("Str_Lbl_DigitalSig", OpenSidebarSigning);
                    Add("Str_DocumentTools_Signatures", ShowSignaturesReport);
                    menu.Items.Add(MakeMenuItem(Loc("Str_CertCatalog_Title"), (_, _) => OpenCertificateCatalog(),
                        GestureFor("CtrlShift:D") + ", " + ShortcutTable.DocumentToolKey("Str_CertCatalog_Title")));
                    break;
                case SidebarPanel.Outlines:
                    Add("Str_Ctx_BmAdd", () => AddBookmarkInto(null));
                    break;
                default:
                    FillPageAgnosticMenu(menu);
                    break;
            }
        }

        private void AddSidebarAttachment()
        {
            if (_doc is null) return;
            var source = ActiveViewer;
            var session = source.ActiveSessionExt;
            var document = _doc;
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title = Loc("Str_Attachment_Add"), Filter = Loc("Str_Filter_AllFiles") + "|*.*",
                CheckFileExists = true, CheckPathExists = true
            };
            if (picker.ShowDialog(this) != true || !ReferenceEquals(ActiveViewer, source)
                || !ReferenceEquals(source.ActiveSessionExt, session) || !ReferenceEquals(_doc, document)) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            try
            {
                CommitActiveTextBox();
                byte[] bytes = File.ReadAllBytes(picker.FileName);
                string name = Path.GetFileName(picker.FileName);
                if (PdfEngineIntegration.ReadAttachmentEditorItems(_currentFile!).Any(item =>
                    string.Equals(item.FileName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(Loc("Str_Attachment_Invalid"));
                UndoEntry? undo = CaptureDocumentUndo();
                SaveTempAndReload(keepAnnotations: true,
                    finalizeSavedFile: path => PdfEngineIntegration.AddAttachment(path, name, bytes),
                    documentUndo: undo);
                RefreshSidebarPanel();
                SetStatus(Loc("Str_Attachment_Updated"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Attachment_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSignaturesReport() => ShowDocumentReport(Loc("Str_DocumentTools_Signatures"),
            path => PdfEngineIntegration.InspectSignatures(path).ToText());

        // ── Refresh ─────────────────────────────────────────────────────────────────────────

        /// <summary>Rebuilds the comments list a moment after an edit, so a burst of changes
        /// (dragging a measurement point) rebuilds it once.</summary>
        private void ScheduleSidebarPanelRefresh()
        {
            if (_sidebarPanel != SidebarPanel.Comments || _sidebarCollapsed) return;
            _sidebarPanelRefreshTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350),
                DispatcherPriority.Background, (_, _) =>
                {
                    _sidebarPanelRefreshTimer!.Stop();
                    RefreshSidebarPanel();
                }, Dispatcher);
            _sidebarPanelRefreshTimer.Stop();
            _sidebarPanelRefreshTimer.Start();
        }

        /// <summary>Rebuilds the open list panel from the working copy.</summary>
        private async void RefreshSidebarPanel()
        {
            if (_sidebarPanel is SidebarPanel.Pages or SidebarPanel.Outlines) return;
            int generation = ++_sidebarPanelGeneration;
            SidebarPanel panel = _sidebarPanel;
            string? path = _doc is not null && !string.IsNullOrWhiteSpace(_currentFile) && File.Exists(_currentFile)
                ? _currentFile : null;
            if (path is null)
            {
                ShowSidebarList(panel, null, Loc("Str_Msg_OpenFirst"));
                return;
            }

            object? data;
            try
            {
                data = await Task.Run<object?>(() => panel switch
                {
                    SidebarPanel.Comments => PdfEngineIntegration.ReadCommentThreads(path),
                    SidebarPanel.Attachments => PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))),
                    SidebarPanel.Layers => PdfEngineIntegration.InspectLayers(path),
                    _ => PdfEngineIntegration.InspectSignatures(path),
                });
            }
            catch (Exception ex)
            {
                if (generation != _sidebarPanelGeneration) return;
                ShowSidebarList(panel, null, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message);
                return;
            }
            if (generation != _sidebarPanelGeneration || panel != _sidebarPanel) return;

            switch (data)
            {
                case IReadOnlyList<PdfCommentThread> threads: BuildCommentsPanel(threads); break;
                case IReadOnlyList<PdfAttachmentInfo> attachments: BuildAttachmentsPanel(attachments); break;
                case PdfOptionalContentInfo layers: BuildLayersPanel(layers); break;
                case PdfSignatureInspectionReport signatures: BuildSignaturesPanel(signatures); break;
            }
        }

        /// <summary>Puts <paramref name="items"/> in the list panel under an optional
        /// <paramref name="top"/> row, or shows <paramref name="empty"/> when there are none.</summary>
        private void ShowSidebarList(SidebarPanel panel, IReadOnlyList<TreeViewItem>? items, string empty,
            FrameworkElement? top = null)
        {
            if (panel != _sidebarPanel) return;
            SidebarListPanel.Children.Clear();
            if (top is not null)
            {
                DockPanel.SetDock(top, Dock.Top);
                SidebarListPanel.Children.Add(top);
            }
            if (items is null || items.Count == 0)
            {
                var message = new TextBlock
                {
                    Text = empty,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    Margin = new Thickness(12, 6, 10, 6),
                    VerticalAlignment = VerticalAlignment.Top
                };
                message.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                SidebarListPanel.Children.Add(message);
                _sidebarPanelTree = null;
                return;
            }

            var tree = new TreeView
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                Style = (Style)FindResource("OutlineTreeStyle"),
                ItemContainerStyle = (Style)FindResource("OutlineItemStyle")
            };
            // Rows added as TreeViewItems are their own containers, so ItemContainerStyle reaches
            // only the top level; the nested rows take the Outline style here.
            var itemStyle = (Style)FindResource("OutlineItemStyle");
            void Styled(TreeViewItem item)
            {
                item.Style = itemStyle;
                foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>()) Styled(child);
            }
            foreach (TreeViewItem item in items)
            {
                Styled(item);
                tree.Items.Add(item);
            }
            tree.SelectedItemChanged += SidebarPanelTree_SelectedItemChanged;
            tree.MouseDoubleClick += SidebarPanelTree_MouseDoubleClick;
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = tree
            };
            ApplyTreeStripLayout(tree, scroll, _sidebarStripLayout);
            // The tree's own scroll viewer swallows the wheel, as in the Outline panel.
            tree.PreviewMouseWheel += (_, e) =>
            {
                ScrollSidebarTree(scroll, e.Delta);
                e.Handled = true;
            };
            SidebarListPanel.Children.Add(scroll);
            _sidebarPanelTree = tree;
        }

        // A row like the Outline tree's: a muted glyph, the text, and an optional muted detail.
        private static TreeViewItem PanelRow(string glyph, string text, string? detail, object? tag,
            string? tooltip = null, FrameworkElement? leading = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (leading is not null) row.Children.Add(leading);
            if (glyph.Length > 0)
            {
                var icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                row.Children.Add(icon);
            }
            var main = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            main.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            row.Children.Add(main);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var muted = new TextBlock
                {
                    Text = detail,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                muted.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                row.Children.Add(muted);
            }
            return new TreeViewItem { Header = row, Tag = tag, ToolTip = tooltip, IsExpanded = true };
        }

        private TreeViewItem PageGroup(int pageIndex) =>
            PanelRow("", string.Format(Loc("Str_MeasureReport_Page"), pageIndex + 1), null, pageIndex);

        private static string Snippet(string text, int length = 60)
        {
            string flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= length ? flat : flat[..(length - 1)].TrimEnd() + "\u2026";
        }

        private void SidebarPanelTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem { Tag: { } tag } || _doc is null) return;
            switch (tag)
            {
                case MeasurementAnnotation measurement:
                    RecordNavJump();
                    ActiveViewer.RevealMeasurementExt(measurement);
                    break;
                case PdfCommentInfo comment when comment.PageIndex >= 0 && comment.PageIndex < _doc.PageCount:
                    RecordNavJump();
                    PageList.SelectedIndex = comment.PageIndex;
                    break;
                case int page when page >= 0 && page < _doc.PageCount:
                    RecordNavJump();
                    PageList.SelectedIndex = page;
                    break;
            }
        }

        private void SidebarPanelTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_sidebarPanelTree?.SelectedItem is not TreeViewItem { Tag: { } tag }) return;
            switch (tag)
            {
                case PdfCommentInfo: EditComments(); e.Handled = true; break;
                case PdfAttachmentInfo attachment: SaveAttachmentCopy(attachment); e.Handled = true; break;
                case PdfSignatureInspectionEntry: ShowSignaturesReport(); e.Handled = true; break;
            }
        }

        // ── Comments, with measurements as a filter ─────────────────────────────────────────

        private void BuildCommentsPanel(IReadOnlyList<PdfCommentThread> threads)
        {
            var filter = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 8, 4) };
            void Chip(CommentsFilter value, string key)
            {
                var label = new TextBlock { Text = Loc(key), FontFamily = UiKit.UiFont, FontSize = 12 };
                var chip = new Button
                {
                    Content = label,
                    Style = (Style)FindResource("SidebarTabBtn"),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                chip.SetResourceReference(Control.ForegroundProperty,
                    _commentsFilter == value ? "PrimaryBrush" : "MutedTextBrush");
                chip.Click += (_, _) =>
                {
                    _commentsFilter = value;
                    BuildCommentsPanel(threads);
                };
                filter.Children.Add(chip);
            }
            Chip(CommentsFilter.All, "Str_Panel_FilterAll");
            Chip(CommentsFilter.Comments, "Str_DocumentTools_Comments");
            Chip(CommentsFilter.Measurements, "Str_DocumentTools_Measurements");

            var pages = new SortedDictionary<int, TreeViewItem>();
            TreeViewItem Group(int page)
            {
                if (!pages.TryGetValue(page, out TreeViewItem? group)) pages[page] = group = PageGroup(page);
                return group;
            }
            TreeViewItem CommentRow(PdfCommentThread thread)
            {
                PdfCommentInfo comment = thread.Comment;
                string who = string.IsNullOrWhiteSpace(comment.Author) ? comment.Subtype : comment.Author!;
                string text = string.IsNullOrWhiteSpace(comment.Contents) ? comment.Subtype : Snippet(comment.Contents, 48);
                TreeViewItem row = PanelRow("\uE90A", text, who, comment,
                    string.IsNullOrWhiteSpace(comment.Contents) ? null : comment.Contents);
                foreach (PdfCommentThread reply in thread.Replies) row.Items.Add(CommentRow(reply));
                return row;
            }

            if (_commentsFilter != CommentsFilter.Measurements)
                foreach (PdfCommentThread thread in threads)
                    Group(thread.Comment.PageIndex).Items.Add(CommentRow(thread));
            if (_commentsFilter != CommentsFilter.Comments)
                foreach (MeasurementAnnotation measurement in ActiveViewer.MeasurementsInOrderExt())
                    Group(measurement.PageIndex).Items.Add(PanelRow("\uED5E",
                        ActiveViewer.MeasureKindName(measurement.Kind),
                        ActiveViewer.MeasurementLabelExt(measurement), measurement));

            string empty = Loc(_commentsFilter switch
            {
                CommentsFilter.Comments => "Str_Panel_NoComments",
                CommentsFilter.Measurements => "Str_Panel_NoMeasurements",
                _ => "Str_Panel_NoCommentsOrMeasurements",
            });
            ShowSidebarList(SidebarPanel.Comments, [.. pages.Values], empty, filter);
        }

        // ── Attachments ─────────────────────────────────────────────────────────────────────

        private void BuildAttachmentsPanel(IReadOnlyList<PdfAttachmentInfo> attachments)
        {
            var rows = attachments.Select(attachment => PanelRow("\uE723", attachment.FileName,
                FormatBytes(attachment.Data.Length), attachment,
                string.IsNullOrWhiteSpace(attachment.Description)
                    ? Loc("Str_Panel_AttachmentHint")
                    : attachment.Description + "\n" + Loc("Str_Panel_AttachmentHint"))).ToList();
            ShowSidebarList(SidebarPanel.Attachments, rows, Loc("Str_Attachment_None"));
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return size.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        /// <summary>Saves a copy of an attachment. Nothing is opened or run.</summary>
        private void SaveAttachmentCopy(PdfAttachmentInfo attachment)
        {
            // A copy leaves a restricted PDF with the file inside it, so it waits for the owner password.
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            string name = Path.GetFileName(attachment.FileName);
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            var dialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Title = Loc("Str_TabAttachments"),
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = string.IsNullOrWhiteSpace(name) ? "attachment" : name
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllBytes(dialog.FileName, attachment.Data.ToArray());
                SetStatus(string.Format(Loc("Str_Panel_AttachmentSaved"), Path.GetFileName(dialog.FileName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                KillerDialog.Show(this, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Layers ──────────────────────────────────────────────────────────────────────────

        private void BuildLayersPanel(PdfOptionalContentInfo layers)
        {
            var rows = new List<TreeViewItem>();
            foreach (PdfOptionalContentGroupInfo group in layers.Groups)
            {
                var check = UiKit.CheckBox("");
                check.IsChecked = layers.ViewVisibleGroupObjectNumbers.Contains(group.ObjectNumber);
                check.IsEnabled = !group.IsLocked;
                check.Margin = new Thickness(0, 0, 4, 0);
                check.VerticalAlignment = VerticalAlignment.Center;
                check.Click += (_, _) => SetLayerVisibility(layers, group, check.IsChecked == true, check);
                rows.Add(PanelRow(group.IsLocked ? "\uE72E" : "", group.Name, null, group,
                    Loc(group.IsLocked ? "Str_Panel_LayerLocked" : "Str_Panel_LayerToggle"), check));
            }
            ShowSidebarList(SidebarPanel.Layers, rows, Loc("Str_DocumentTools_NoLayers"));
        }

        /// <summary>Shows or hides a layer in the document itself, as one undoable change.</summary>
        private void SetLayerVisibility(PdfOptionalContentInfo layers, PdfOptionalContentGroupInfo target,
            bool visible, CheckBox check)
        {
            if (!EnsureEditable())
            {
                check.IsChecked = !visible;
                return;
            }
            CommitActiveTextBox();
            PdfEngineIntegration.LayerEdit[] edits = [new(
                target.ObjectNumber, target.Name, visible, target.IsLocked)];
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path => PdfEngineIntegration.ApplyLayerEdits(path, edits),
                    documentUndo: documentUndo);
                SetStatus(Loc("Str_DocumentTools_LayersUpdated"));
            }
            catch (Exception ex)
            {
                check.IsChecked = !visible;
                KillerDialog.Show(this, Loc("Str_DocumentTools_LayersFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Signatures ──────────────────────────────────────────────────────────────────────

        private void BuildSignaturesPanel(PdfSignatureInspectionReport report)
        {
            var rows = new List<TreeViewItem>();
            foreach (PdfSignatureInspectionEntry entry in report.Entries)
            {
                PdfSignatureInfo signature = entry.Signature;
                string name = string.IsNullOrWhiteSpace(signature.SignerName) ? signature.FieldName : signature.SignerName!;
                string status = !signature.IsSigned ? Loc("Str_Panel_SigUnsigned")
                    : entry.Verification.ValidationStatus switch
                    {
                        PdfSignatureValidationStatus.Valid => Loc("Str_Panel_SigValid"),
                        PdfSignatureValidationStatus.Invalid => Loc("Str_Panel_SigInvalid"),
                        _ => Loc("Str_Panel_SigIncomplete"),
                    };
                if (signature.IsSigned && entry.Revision?.HasLaterChanges == true)
                    status += ", " + Loc("Str_Panel_SigChanged");
                string? when = signature.SigningTime?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                string tooltip = string.Join("\n", new[] { signature.FieldName, when, signature.Reason, signature.Location }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                rows.Add(PanelRow("\uEB95", name, status, entry, tooltip));
            }
            var sign = UiKit.Make(Loc("Str_Lbl_DigitalSig"), true);
            sign.HorizontalAlignment = HorizontalAlignment.Left;
            sign.Margin = new Thickness(12, 4, 10, 6);
            sign.ToolTip = ShortcutTip("Str_Lbl_DigitalSig", "CtrlShift:G");
            sign.Click += (_, _) => OpenSidebarSigning();
            ShowSidebarList(SidebarPanel.Signatures, rows, Loc("Str_Panel_NoSignatures"), sign);
        }
    }
}
