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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF
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
        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            // Only arm a page-reorder drag when the press lands on a page thumbnail, not the
            // scrollbar - otherwise grabbing the scrollbar starts a page-move drag (the "insert"
            // cursor) instead of scrolling.
            _pageDragArmed = false;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) break;
                if (d is ListBoxItem) { _pageDragArmed = true; break; }
            }
        }

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_pageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                {
                    int[] selected = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                        .Select(page => page.PageIndex).OrderBy(index => index)];
                    CommitActiveTextBox();
                    ActiveViewer.CaptureActiveIfAny();
                    var session = ActiveViewer.ActiveSessionRef;
                    if (session?.CurrentFile is not null)
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
            double edge = Math.Min(84, PageList.ActualHeight / 3);
            double ratio = pointer.Y < edge
                ? -(edge - Math.Max(0, pointer.Y)) / edge
                : pointer.Y > PageList.ActualHeight - edge
                    ? (edge - Math.Max(0, PageList.ActualHeight - pointer.Y)) / edge
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

        /// <summary>Returns the insertion slot from 0 through Count and the matching visual Y.
        /// Both the marker and the drop consume this result so what the user sees is authoritative.</summary>
        private (int Index, double Y)? PageDropSlot(Point position)
        {
            ListBoxItem? last = null;
            int lastIndex = -1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
                var top = item.TranslatePoint(new Point(0, 0), PageList);
                if (position.Y < top.Y + item.ActualHeight / 2)
                    return (i, item.TranslatePoint(new Point(0, 0), PageListFadeHost).Y);
                last = item;
                lastIndex = i;
            }
            if (last is null) return null;
            double bottom = last.TranslatePoint(new Point(0, last.ActualHeight), PageListFadeHost).Y;
            return (lastIndex + 1, bottom);
        }

        private void ShowPageDropIndicator(Point position)
        {
            var slot = PageDropSlot(position);
            if (slot is null) { HidePageDropIndicator(); return; }
            _pageDropInsertionIndex = slot.Value.Index;
            if (PageDropIndicator.RenderTransform is TranslateTransform move)
                move.Y = Math.Max(0, Math.Min(PageListFadeHost.ActualHeight - PageDropIndicator.Height,
                    slot.Value.Y - PageDropIndicator.Height / 2));
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
            CommitActiveTextBox();
            var imports = new List<PdfEngineIntegration.ImportedDocument>();
            foreach (var f in files)
            {
                string? importPath = null;
                if (PdfImport.IsPdfPath(f))
                {
                    importPath = f;
                    try { PdfEngineIntegration.ValidateDocument(importPath); }
                    catch { importPath = await RepairPdfForImportAsync(f); }
                }
                else
                {
                    try
                    {
                        importPath = App.MakeTempFile("dropimage");
                        File.WriteAllBytes(importPath, PdfEngineIntegration.MergeFiles([f]));
                    }
                    catch { importPath = null; }
                }
                if (importPath is null) continue;
                try
                {
                    var pages = PdfEngineIntegration.ReadPageInformation(importPath);
                    imports.Add(new PdfEngineIntegration.ImportedDocument(importPath,
                        [.. pages.Select(page => page.Rotation)]));
                }
                catch { /* skip anything still unreadable after repair */ }
            }
            if (_doc is null) return;
            if (imports.Count == 0) { SetStatus(Loc("Str_Drop_NothingOpenable")); return; }
            UndoEntry? documentUndo = CaptureDocumentUndo();
            int insertAt = Math.Max(0, Math.Min(insertionIndex ?? _doc.PageCount, _doc.PageCount));
            int importedCount = imports.Sum(import => import.PageRotations.Count);
            if (insertAt < _doc.PageCount && importedCount > 0)
            {
                var shifted = new Dictionary<int, List<PageAnnotation>>();
                foreach (var pair in _annotations)
                {
                    int page = pair.Key >= insertAt ? pair.Key + importedCount : pair.Key;
                    foreach (PageAnnotation annotation in pair.Value) annotation.PageIndex = page;
                    shifted[page] = pair.Value;
                }
                _annotations.Clear();
                foreach (var pair in shifted) _annotations[pair.Key] = pair.Value;
            }
            SaveTempAndReload(
                keepAnnotations: true,
                preserveZoom: true,
                finalizeSavedFile: path => PdfEngineIntegration.InsertDocuments(path, imports, insertAt),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(rotations, imports, insertAt),
                selectedPageAfterReload: insertAt,
                documentUndo: documentUndo);
            SetStatus(string.Format(Loc("Str_Status_Merged"), files.Length));
        }

        /// <summary>
        /// Runs the open path's three repair strategies against a dropped file and returns the
        /// repaired temp copy, or null if the user declined or nothing recovered it. The original
        /// file is never written to.
        /// </summary>
        private async System.Threading.Tasks.Task<string?> RepairPdfForImportAsync(string path)
        {
            var ask = KillerDialog.Show(this,
                string.Format(Loc("Str_Dlg_RepairAsk"), System.IO.Path.GetFileName(path)),
                "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return null;

            var busy = ShowBusyOverlay(Loc("Str_Busy_Repairing"));
            try
            {
                // Same order as TryRepairAndOpen: lossless PDFium re-save first (keeps forms and
                // bookmarks), then a PdfSharpCore page-copy, then the rasterize that always
                // produces something openable.
                string? repaired = await System.Threading.Tasks.Task.Run(() =>
                {
                    var p = App.MakeTempFile("repaired");
                    return PdfiumInterop.TryPdfiumStripEncryption(path, p) ? p : null;
                });
                repaired ??= await System.Threading.Tasks.Task.Run(() => PdfImport.RepairViaImportToFile(path));
                repaired ??= await System.Threading.Tasks.Task.Run(
                    () => PdfImport.RepairViaRasterizeToFile(path));

                if (repaired is null)
                    KillerDialog.Show(this,
                        string.Format(Loc("Str_Err_FileRepairFailed"),
                            System.IO.Path.GetFileName(path)),
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return repaired;
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
            int insertionIndex = _pageDropInsertionIndex;
            if (insertionIndex < 0)
                insertionIndex = PageDropSlot(e.GetPosition(PageList))?.Index ?? fromIndices[0];
            HidePageDropIndicator();
            IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterMove(
                PageList.Items.Count, fromIndices, insertionIndex);
            if (order.SequenceEqual(Enumerable.Range(0, PageList.Items.Count))) return;
            IReadOnlyList<int> selectedAfter = [];
            SaveTempAndReload(
                finalizeSavedFile: path =>
                    selectedAfter = PdfEngineIntegration.MovePages(path, fromIndices, insertionIndex),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterPageMoves(
                        rotations, fromIndices, insertionIndex),
                selectedPageAfterReload: Enumerable.Range(0, order.Count)
                    .First(index => order[index] == fromIndices[0]));
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
