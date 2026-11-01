using System.Diagnostics;
using System.IO;
using System.Linq;
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
using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// In-process page transfer captured before a drag can move focus to another pane.
    /// The source working file and overlay annotations are snapshots of what the user dragged.
    /// </summary>
    internal sealed record PageDragPayload(
        KillerPDF.Controls.PdfViewer SourceViewer,
        string SourcePath,
        int[] PageIndices,
        Dictionary<int, int> PageRotations,
        Dictionary<int, List<PageAnnotation>> Annotations);

    public partial class MainWindow
    {
        // ============================================================
        // Drag/drop: file open
        // ============================================================

        internal void DropZone_DragOver(PdfViewer viewer, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(PageDragPayload))
                && e.Data.GetData(typeof(PageDragPayload)) is PageDragPayload pages)
            {
                FocusPane(viewer);
                if (!ReferenceEquals(pages.SourceViewer, viewer)
                    && viewer.ShowPageImportDropIndicator(e, out _))
                {
                    e.Effects = DragDropEffects.Copy;
                }
                else
                {
                    viewer.HidePageImportDropIndicator();
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                viewer.HidePageImportDropIndicator();
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
            }
            e.Handled = true;
        }

        internal void DropZone_DragOver(object _, DragEventArgs e)
            => DropZone_DragOver(ActiveViewer, e);

        // internal: PdfViewer's XAML binds these three and forwards to them.
        internal void DropZone_Drop(PdfViewer viewer, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(PageDragPayload))
                && e.Data.GetData(typeof(PageDragPayload)) is PageDragPayload pages
                && !ReferenceEquals(pages.SourceViewer, viewer)
                && viewer.ShowPageImportDropIndicator(e, out int insertAt))
            {
                viewer.HidePageImportDropIndicator();
                ImportDraggedPages(viewer, pages, insertAt);
                e.Handled = true;
                return;
            }
            viewer.HidePageImportDropIndicator();
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                FocusPane(viewer);
                OnPathsDropped((string[])e.Data.GetData(DataFormats.FileDrop)!);
                e.Handled = true;   // don't let the same drop bubble to the window-level handler
            }
        }

        internal void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private bool _pageDragArmed;
        private int _pageDropInsertionIndex = -1;
        private PageDragCursor? _pageDragCursor;
        private System.Windows.Threading.DispatcherTimer? _pageDragScrollTimer;
        private ScrollViewer? _pageDragScrollViewer;
        private double _pageDragScrollVelocity;
        private Point _pageDragPointer;
        // A plain press on one page of a multi-page selection collapses the selection to that
        // page before a drag can start, so a block of pages could never be dragged. Hold the
        // selection through the press and collapse it on release instead, as File Explorer does.
        private ListBoxItem? _pagePressPendingCollapse;
        private bool _pageListReleaseHooked;

        private void PageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ListBoxItem? pending = _pagePressPendingCollapse;
            _pagePressPendingCollapse = null;
            if (pending is null || !pending.IsSelected) return;
            object item = PageList.ItemContainerGenerator.ItemFromContainer(pending);
            if (item == DependencyProperty.UnsetValue) return;
            // Deselect the others one at a time: assigning SelectedItem is a no-op when the
            // pressed page is already the first selected one.
            foreach (object other in PageList.SelectedItems.Cast<object>()
                         .Where(selected => !ReferenceEquals(selected, item)).ToList())
                PageList.SelectedItems.Remove(other);
        }

        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pagePressPendingCollapse = null;
            _dragStartPoint = e.GetPosition(null);
            // Only arm a page-reorder drag when the press lands on a page thumbnail, not the
            // scrollbar - otherwise grabbing the scrollbar starts a page-move drag (the "insert"
            // cursor) instead of scrolling.
            _pageDragArmed = false;
            bool thumbnailContent = false;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) return;
                if (d is System.Windows.Controls.Primitives.ButtonBase) return;
                if (d is TextBlock || d is Border { Name: "ThumbBorder" }) thumbnailContent = true;
                if (d is ListBoxItem pressed)
                {
                    if (!thumbnailContent) break;
                    _pageDragArmed = true;
                    if (pressed.IsSelected && PageList.SelectedItems.Count > 1
                        && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None)
                    {
                        _pagePressPendingCollapse = pressed;
                        if (!_pageListReleaseHooked)
                        {
                            PageList.PreviewMouseLeftButtonUp += PageList_PreviewMouseLeftButtonUp;
                            _pageListReleaseHooked = true;
                        }
                        pressed.Focus();
                        e.Handled = true;
                    }
                    break;
                }
            }
            if (!_pageDragArmed) BeginPageBoxSelection(e);
        }

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (UpdatePageBoxSelection(e)) return;
            if (!_pageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                {
                    _pagePressPendingCollapse = null;   // the press became a drag
                    int[] selected = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                        .Select(page => page.PageIndex).OrderBy(index => index)];
                    CommitActiveTextBox();
                    ActiveViewer.CaptureActiveIfAny();
                    var session = ActiveViewer.ActiveSessionRef;
                    if (session?.CurrentFile is not null && !ActiveViewer.IsOwnerRestricted)
                    {
                        var annotations = new Dictionary<int, List<PageAnnotation>>();
                        foreach (int page in selected)
                            if (session.Annotations.TryGetValue(page, out var source))
                                annotations[page] = [.. source.Select(CloneAnnotation)
                                    .Where(annotation => annotation is not null)
                                    .Cast<PageAnnotation>()];
                        var payload = new PageDragPayload(
                            ActiveViewer,
                            session.CurrentFile,
                            selected,
                            session.PageRotations.ToDictionary(pair => pair.Key, pair => pair.Value),
                            annotations);
                        var data = new DataObject();
                        data.SetData(typeof(int[]), selected);
                        data.SetData(typeof(PageDragPayload), payload);
                        BitmapSource? thumbnail = PageList.SelectedItems.Cast<PageThumbnailVm>()
                            .FirstOrDefault(page => page.PageIndex == selected[0])?.Thumbnail;
                        thumbnail ??= PageThumbnailVm.BuildThumb(
                            session.CurrentFile,
                            selected[0],
                            session.PageRotations.TryGetValue(selected[0], out int rotation) ? rotation : 0);
                        _pageDragCursor = PageDragCursor.Create(thumbnail, selected.Length);
                        GiveFeedbackEventHandler feedback = PageDrag_GiveFeedback;
                        PageList.AddHandler(DragDrop.GiveFeedbackEvent, feedback);
                        DragDropEffects result = DragDropEffects.None;
                        try
                        {
                            result = DragDrop.DoDragDrop(
                                PageList, data, DragDropEffects.Copy | DragDropEffects.Move);
                        }
                        finally
                        {
                            PageList.RemoveHandler(DragDrop.GiveFeedbackEvent, feedback);
                            Mouse.OverrideCursor = null;
                            _pageDragCursor?.Dispose();
                            _pageDragCursor = null;
                            StopPageDragAutoScroll();
                            Viewer.HidePageImportDropIndicator();
                            ViewerB.HidePageImportDropIndicator();
                            if (result == DragDropEffects.None)
                                FocusPane(payload.SourceViewer);
                        }
                    }
                    HidePageDropIndicator();
                }
            }
        }

        private void PageDrag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_pageDragCursor is null)
            {
                e.UseDefaultCursors = true;
            }
            else
            {
                e.UseDefaultCursors = false;
                Mouse.OverrideCursor = _pageDragCursor.Cursor;
                Mouse.SetCursor(_pageDragCursor.Cursor);
            }
            e.Handled = true;
        }

        internal void DropZone_Drop(object _, DragEventArgs e)
            => DropZone_Drop(ActiveViewer, e);

        private void ImportDraggedPages(PdfViewer target, PageDragPayload payload, int insertionIndex)
        {
            if (payload.PageIndices.Length == 0 || !File.Exists(payload.SourcePath)) return;
            FocusPane(target);
            if (_doc is null || _currentFile is null) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();

            int insertAt = Math.Clamp(insertionIndex, 0, _doc.PageCount);
            string extracted = App.MakeTempFile("pagedrag");
            try
            {
                PdfEngineIntegration.ExtractPages(
                    payload.SourcePath, extracted, payload.PageIndices, payload.PageRotations);
                int[] importedRotations = [.. payload.PageIndices.Select(page =>
                    payload.PageRotations.TryGetValue(page, out int rotation) ? rotation : 0)];
                var imports = new[]
                {
                    new PdfEngineIntegration.ImportedDocument(extracted, importedRotations)
                };

                UndoEntry? documentUndo = CaptureDocumentUndo();
                var annotationBackup = _annotations.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                try
                {
                    PageAnnotationInsertion.Shift(_annotations, insertAt, payload.PageIndices.Length);
                    for (int outputPage = 0; outputPage < payload.PageIndices.Length; outputPage++)
                    {
                        int sourcePage = payload.PageIndices[outputPage];
                        if (!payload.Annotations.TryGetValue(sourcePage, out var source)) continue;
                        int destinationPage = insertAt + outputPage;
                        var copies = source.Select(CloneAnnotation)
                            .Where(annotation => annotation is not null)
                            .Cast<PageAnnotation>()
                            .ToList();
                        foreach (PageAnnotation annotation in copies)
                            annotation.PageIndex = destinationPage;
                        if (copies.Count > 0) _annotations[destinationPage] = copies;
                    }

                    SaveTempAndReload(
                        keepAnnotations: true,
                        preserveZoom: true,
                        finalizeSavedFile: path =>
                            PdfEngineIntegration.InsertDocuments(path, imports, insertAt),
                        remapRedactionMarks: true,
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(
                                rotations, imports, insertAt),
                        selectedPageAfterReload: insertAt,
                        documentUndo: documentUndo);
                }
                catch
                {
                    _annotations.Clear();
                    foreach (var pair in annotationBackup)
                    {
                        foreach (PageAnnotation annotation in pair.Value)
                            annotation.PageIndex = pair.Key;
                        _annotations[pair.Key] = pair.Value;
                    }
                    throw;
                }

                SetStatus(payload.PageIndices.Length == 1
                    ? Loc("Str_St_PageCopiedHere")
                    : string.Format(Loc("Str_St_PagesCopiedHere"), payload.PageIndices.Length));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Err_PageCopyFailed"), ex.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            UpdatePageDragAutoScroll(e.GetPosition(PageList));
            // #172: files dropped onto the Pages sidebar append to the open document,
            // so the list accepts FileDrop as well as its own page-reorder payload.
            if (e.Data.GetDataPresent(typeof(PageDragPayload))
                && e.Data.GetData(typeof(PageDragPayload)) is PageDragPayload pages
                && !ReferenceEquals(pages.SourceViewer, ActiveViewer))
            {
                e.Effects = DragDropEffects.Copy;
                ShowPageDropIndicator(e.GetPosition(PageList));
            }
            else if (e.Data.GetDataPresent(typeof(int[])))
            {
                e.Effects = DragDropEffects.Move;
                ShowPageDropIndicator(e.GetPosition(PageList));
            }
            else if (_doc != null && DroppedOpenablePaths(e).Length > 0)
            {
                e.Effects = DragDropEffects.Copy;
                ShowPageDropIndicator(e.GetPosition(PageList));
            }
            else
            {
                e.Effects = DragDropEffects.None;
                HidePageDropIndicator();
            }
            e.Handled = true;
        }

        private void PageList_DragLeave(object sender, DragEventArgs e)
        {
            StopPageDragAutoScroll();
            HidePageDropIndicator();
        }

        private void UpdatePageDragAutoScroll(Point pointer)
        {
            _pageDragPointer = pointer;
            // A top or bottom strip scrolls sideways, so its scroll zones are its two ends.
            double along = _sidebarStripLayout ? pointer.X : pointer.Y;
            double length = _sidebarStripLayout ? PageList.ActualWidth : PageList.ActualHeight;
            double edge = Math.Min(84, length / 3);
            double ratio = along < edge
                ? -(edge - Math.Max(0, along)) / edge
                : along > length - edge
                    ? (edge - Math.Max(0, length - along)) / edge
                    : 0;
            _pageDragScrollVelocity = Math.Sign(ratio) * ratio * ratio * 32;
            if (_pageDragScrollVelocity == 0)
            {
                StopPageDragAutoScroll();
                return;
            }

            _pageDragScrollViewer ??= FindPageListScrollViewer(PageList);
            if (_pageDragScrollViewer is null) return;
            _pageDragScrollTimer ??= new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(30),
                System.Windows.Threading.DispatcherPriority.Input,
                (_, _) =>
                {
                    if (_pageDragScrollViewer is null) return;
                    if (_sidebarStripLayout)
                        _pageDragScrollViewer.ScrollToHorizontalOffset(
                            _pageDragScrollViewer.HorizontalOffset + _pageDragScrollVelocity);
                    else
                        _pageDragScrollViewer.ScrollToVerticalOffset(
                            _pageDragScrollViewer.VerticalOffset + _pageDragScrollVelocity);
                    ShowPageDropIndicator(_pageDragPointer);
                },
                Dispatcher);
            _pageDragScrollTimer.Start();
        }

        private void StopPageDragAutoScroll()
        {
            _pageDragScrollVelocity = 0;
            _pageDragScrollTimer?.Stop();
        }

        private static ScrollViewer? FindPageListScrollViewer(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer scrollViewer) return scrollViewer;
                ScrollViewer? nested = FindPageListScrollViewer(child);
                if (nested is not null) return nested;
            }
            return null;
        }

        /// <summary>Returns the insertion slot from 0 through Count and the matching visual offset
        /// along the list: Y in a column, X in a top or bottom strip. Both the marker and the drop
        /// consume this result so what the user sees is authoritative.</summary>
        private (int Index, double Offset)? PageDropSlot(Point position)
        {
            bool strip = _sidebarStripLayout;
            double Along(Point p) => strip ? p.X : p.Y;
            ListBoxItem? last = null;
            int lastIndex = -1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
                var start = item.TranslatePoint(new Point(0, 0), PageList);
                double length = strip ? item.ActualWidth : item.ActualHeight;
                if (Along(position) < Along(start) + length / 2)
                    return (i, Along(item.TranslatePoint(new Point(0, 0), PageListFadeHost)));
                last = item;
                lastIndex = i;
            }
            if (last is null) return null;
            Point lastEnd = strip ? new Point(last.ActualWidth, 0) : new Point(0, last.ActualHeight);
            return (lastIndex + 1, Along(last.TranslatePoint(lastEnd, PageListFadeHost)));
        }

        private void ShowPageDropIndicator(Point position)
        {
            var slot = PageDropSlot(position);
            if (slot is null) { HidePageDropIndicator(); return; }
            _pageDropInsertionIndex = slot.Value.Index;
            if (PageDropIndicator.RenderTransform is TranslateTransform move)
            {
                const double bar = 3;   // the marker's thickness across the list
                double room = (_sidebarStripLayout ? PageListFadeHost.ActualWidth : PageListFadeHost.ActualHeight) - bar;
                double at = Math.Max(0, Math.Min(room, slot.Value.Offset - bar / 2));
                if (_sidebarStripLayout) move.X = at; else move.Y = at;
            }
            PageDropIndicator.Visibility = Visibility.Visible;
        }

        private void HidePageDropIndicator()
        {
            PageDropIndicator.Visibility = Visibility.Collapsed;
            _pageDropInsertionIndex = -1;
        }

        private static string[] DroppedOpenablePaths(DragEventArgs e)
            => e.Data.GetDataPresent(DataFormats.FileDrop)
                ? [.. ((string[])e.Data.GetData(DataFormats.FileDrop)!).Where(IsOpenablePath)]
                : [];

        // #172/#233: import dropped files into the open document. The page insertion marker supplies
        // an exact position; annotations and rotations after that position move with their pages.
        private async void AppendFilesToCurrentDoc(string[] files, int? insertionIndex = null)
        {
            if (_doc is null) return;
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            // Repairs below are awaited; the import goes only into the document it was dropped on.
            var isTargetCurrent = CaptureDocumentOrigin();
            var imports = new List<PdfEngineIntegration.ImportedDocument>();
            // Every file the merge does not take is counted, so the status never reports a clean merge
            // over a file that quietly went missing.
            int declined = 0;       // repairs the user turned down
            int repairFailed = 0;   // repairs that were accepted and did not work
            int unreadable = 0;     // files that could not be read, converted or merged
            string? noPages = null;
            string? failure = null;
            foreach (var f in files)
            {
                try
                {
                    IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages;
                    string importPath;
                    if (PdfImport.IsPdfPath(f))
                    {
                        importPath = f;
                        // Checking a damaged file copies its pages, so it runs off the UI thread.
                        try
                        {
                            pages = await RunBusyAsync(() =>
                            {
                                PdfEngineIntegration.ValidateImportable(f);
                                return PdfEngineIntegration.ReadPageInformation(f);
                            });
                        }
                        // Only damage a repair can mend is offered one.
                        catch (Exception ex) when (PdfEngineIntegration.IsParserDamage(ex))
                        {
                            (string? repaired, bool wasDeclined) = await RepairPdfForImportAsync(f);
                            if (repaired is null)
                            {
                                if (wasDeclined) declined++; else repairFailed++;
                                continue;
                            }
                            importPath = repaired;
                            // A repair that did not help leaves this file out, not the files beside it.
                            pages = await RunBusyAsync(() =>
                            {
                                PdfEngineIntegration.ValidateImportable(importPath);
                                return PdfEngineIntegration.ReadPageInformation(importPath);
                            });
                        }
                    }
                    else
                    {
                        importPath = App.MakeTempFile("dropimage");
                        File.WriteAllBytes(importPath, PdfEngineIntegration.MergeFiles([f]));
                        pages = PdfEngineIntegration.ReadPageInformation(importPath);
                    }
                    imports.Add(new PdfEngineIntegration.ImportedDocument(importPath,
                        [.. pages.Select(page => page.Rotation)], f));
                }
                catch (PdfEngineIntegration.ImportNoPagesException ex)
                {
                    noPages = ex.Message;
                    unreadable++;
                }
                catch (Exception ex) when (PdfEngineIntegration.IsImportFailure(ex))
                {
                    failure = ex.Message;
                    unreadable++;
                }
            }
            if (_doc is null || !isTargetCurrent())
            {
                SetStatus(Loc("Str_Merge_TargetChanged"));
                return;
            }
            int skipped = declined + repairFailed + unreadable;
            if (imports.Count == 0)
            {
                if (repairFailed > 0) SetStatus(Loc("Str_Drop_RepairFailed"));
                else if (declined > 0) SetStatus(Loc("Str_Drop_MergeCanceled"));
                else if (noPages is not null) SetStatus(noPages);
                else if (failure is not null)
                    KillerDialog.Show(this, Loc("Str_Err_MergeFailed") + "\n" + failure,
                        Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                else SetStatus(Loc("Str_Drop_NothingOpenable"));
                return;
            }
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                int insertAt = Math.Max(0, Math.Min(insertionIndex ?? _doc.PageCount, _doc.PageCount));
                int importedCount = imports.Sum(import => import.PageRotations.Count);
                SaveTempAndReload(
                    keepAnnotations: true,
                    preserveZoom: true,
                    finalizeSavedFile: path => PdfEngineIntegration.InsertDocuments(path, imports, insertAt),
                    remapAnnotations: annotations => PageAnnotationInsertion.Shift(annotations, insertAt, importedCount),
                    remapRedactionMarks: true,
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(rotations, imports, insertAt),
                    selectedPageAfterReload: insertAt,
                    documentUndo: documentUndo);
                string merged = string.Format(Loc("Str_Status_Merged"), imports.Count);
                SetStatus(skipped > 0
                    ? merged + " " + string.Format(Loc("Str_Drop_SkippedFiles"), skipped) : merged);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_MergeFailed") + "\n" + ex.Message,
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Runs the page check on a worker behind the same overlay the repair uses, so the window cannot
        // be changed underneath a merge that has not copied anything yet.
        private async System.Threading.Tasks.Task<T> RunBusyAsync<T>(Func<T> work)
        {
            var busy = ShowBusyOverlay(Loc("Str_Drop_Merging"));
            try { return await System.Threading.Tasks.Task.Run(work); }
            finally { HideBusyOverlay(busy); }
        }

        /// <summary>
        /// Runs the open path's three repair strategies against a dropped file and returns the
        /// repaired temp copy. The path is null when the user declined, which Declined says, or when
        /// nothing recovered it. The original file is never written to.
        /// </summary>
        private async System.Threading.Tasks.Task<(string? Path, bool Declined)> RepairPdfForImportAsync(string path)
        {
            var ask = KillerDialog.Show(this,
                string.Format(Loc("Str_Dlg_RepairAsk"), System.IO.Path.GetFileName(path)),
                "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return (null, true);

            var busy = ShowBusyOverlay(Loc("Str_Busy_Repairing"));
            try
            {
                // Same order as TryRepairAndOpen: lossless PDFium re-save first (keeps forms and
                // bookmarks), then a PdfSharpCore page-copy, then the rasterize that always
                // produces something openable.
                string? repaired = await System.Threading.Tasks.Task.Run(() =>
                {
                    var p = App.MakeTempFile("repaired");
                    return PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(path, p)
                        ? p : null;
                });
                repaired ??= await System.Threading.Tasks.Task.Run(() => PdfImport.RepairViaImportToFile(path));
                repaired ??= await System.Threading.Tasks.Task.Run(
                    () => PdfImport.RepairViaRasterizeToFile(path));

                if (repaired is null)
                    KillerDialog.Show(this,
                        string.Format(Loc("Str_Err_FileRepairFailed"),
                            System.IO.Path.GetFileName(path)),
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return (repaired, false);
            }
            finally { HideBusyOverlay(busy); }
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            StopPageDragAutoScroll();
            if (_doc != null
                && e.Data.GetDataPresent(typeof(PageDragPayload))
                && e.Data.GetData(typeof(PageDragPayload)) is PageDragPayload pages
                && !ReferenceEquals(pages.SourceViewer, ActiveViewer))
            {
                int insertAt = _pageDropInsertionIndex >= 0
                    ? _pageDropInsertionIndex
                    : _doc.PageCount;
                HidePageDropIndicator();
                ImportDraggedPages(ActiveViewer, pages, insertAt);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
            if (_doc != null && !e.Data.GetDataPresent(typeof(int[])))
            {
                var files = DroppedOpenablePaths(e);
                if (files.Length > 0)
                {
                    int insertAt = _pageDropInsertionIndex >= 0 ? _pageDropInsertionIndex : _doc.PageCount;
                    HidePageDropIndicator();
                    AppendFilesToCurrentDoc(files, insertAt);
                    e.Handled = true;
                    return;
                }
            }
            if (_doc is null || !e.Data.GetDataPresent(typeof(int[]))) { HidePageDropIndicator(); return; }
            int[] fromIndices = (int[])e.Data.GetData(typeof(int[]))!;
            if (fromIndices.Length == 0) { HidePageDropIndicator(); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) { HidePageDropIndicator(); return; }
            int insertionIndex = _pageDropInsertionIndex;
            if (insertionIndex < 0)
                insertionIndex = PageDropSlot(e.GetPosition(PageList))?.Index ?? fromIndices[0];
            HidePageDropIndicator();
            IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterMove(
                PageList.Items.Count, fromIndices, insertionIndex);
            if (order.SequenceEqual(Enumerable.Range(0, PageList.Items.Count))) return;
            IReadOnlyList<int> selectedAfter = [];
            try
            {
                ReloadWithRemappedAnnotations(
                    annotations => PageAnnotationInsertion.Reorder(annotations, order),
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path =>
                            selectedAfter = PdfEngineIntegration.MovePages(path, fromIndices, insertionIndex),
                        remapRedactionMarks: true,
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageMoves(
                                rotations, fromIndices, insertionIndex),
                        selectedPageAfterReload: Enumerable.Range(0, order.Count)
                            .First(index => order[index] == fromIndices[0]),
                        documentUndo: documentUndo));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PageList.SelectedItems.Clear();
                foreach (int index in selectedAfter)
                    if (index >= 0 && index < PageList.Items.Count)
                        PageList.SelectedItems.Add(PageList.Items[index]);
            }));
        }
    }
}
