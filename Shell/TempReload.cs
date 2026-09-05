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
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload(bool keepAnnotations = false, bool preserveZoom = false,
            Action<string>? finalizeSavedFile = null,
            Action<Dictionary<int, int>>? remapRotations = null,
            int? selectedPageAfterReload = null,
            UndoEntry? documentUndo = null,
            bool preserveRenderedPages = false)
        {
            if (_doc is null || _currentFile is null) return;
            // Capture the previous serialized working file before any rewrite. Most operations
            // mutate through finalizeSavedFile below, while the few that adjust side state before
            // arriving here pass an explicit pre-mutation entry. Push only after every save,
            // rewrite and reopen step succeeds so a failed operation creates no phantom history.
            documentUndo ??= ActiveViewer.CaptureSerializedDocumentUndoExt(_currentFile);
            // The serialized working file is about to change, so discard its immutable engine view.
            CloseEngineDocumentSession();
            // Stop render workers tied to the outgoing file before clearing the cache. Without this,
            // an already-running Grid or Continuous task can finish after the clear and put an old
            // page bitmap straight back into the active session.
            if (!preserveRenderedPages)
            {
                _secondaryRenderCts?.Cancel();
                _continuousRenderCts?.Cancel();
                _continuousSharpenCts?.Cancel();
            }
            // Overlay annotations are unsaved, still-editable user work. Callers that don't change
            // page identity (crop) pass keepAnnotations:true so annotations on other pages survive
            // the reload and stay selectable/movable; they are re-rendered after the doc reopens.
            if (!keepAnnotations) _annotations.Clear();
            if (!preserveRenderedPages)
            {
                _renderDims.Clear();
                Controls.PdfViewer.InvalidateRenderCacheExt(_active);   // pages changed pixels / order: drop this tab's cached bitmaps
                _renderedPrimaryPage = -1;        // force a re-render after reload even if the same page stays selected (e.g. rotate)
                ClearSelection();
            }
            MarkDirty();
            var doc = _doc;
            int selectedIdx = selectedPageAfterReload ?? PageList.SelectedIndex;

            // Capture page rotations, then strip them from the serialized working copy.
            // Docnet uses FPDF_GetPageWidth/Height (MediaBox, no rotation) to size the bitmap,
            // then renders with PDFium's page CTM which *does* include /Rotate.  For 90�/270�
            // the rendered landscape content overflows the portrait-sized bitmap and gets clipped.
            // Stripping /Rotate to 0 before saving means Docnet renders clean unrotated content
            // that fits the bitmap; RotateBitmap is applied in each render path instead.
            EnsureEngineDocumentSession().CaptureRotations(_pageRotations);
            remapRotations?.Invoke(_pageRotations);

            var tempPath = App.MakeTempFile("temp");
            var serializedPath = App.MakeTempFile("serialized");
            try
            {
                doc.Save(serializedPath);
                doc.Close();
                PdfEngineIntegration.RepairHarmlessSaveArtifacts(serializedPath);
                PdfEngineIntegration.CreateZeroRotationCopy(serializedPath, tempPath);
            }
            catch (Exception saveEx) when (PdfImport.IsXRefException(saveEx))
            {
                // PdfSharpCore fails to re-save encrypted PDFs (e.g. owner-restricted RC4 files)
                // because it encounters cross-reference tokens while serializing dirty objects.
                // Primary fallback: use PDFium (already initialized for the page preview) to
                // load the source, strip all /Rotate values, remove encryption, and save.
                // Secondary fallback: PdfSharpCore Import mode (works on some non-encrypted xref
                // issues but fails on encrypted files; kept as a last resort).
                doc.Close();
                _doc = null;
                if (!PdfiumInterop.TryCreateZeroRotationCopy(
                        _currentFile!, tempPath) &&
                    !PdfImport.TryImportRepairToPath(_currentFile!, tempPath, stripRotations: true))
                    throw; // re-throw original if both fallbacks fail
            }

            // Structural operations can finalize the freshly serialized working file through
            // KillerPDF.Engine before PdfSharpCore reopens it. The callback owns atomic replacement
            // of tempPath and runs only after the base save or repair path has completed.
            finalizeSavedFile?.Invoke(tempPath);

            // PdfSharpCore sometimes saves a file where one object's xref offset points at the
            // xref table itself (object N offset = xref table position). When PdfSharp then tries
            // to re-open that file in Modify mode it seeks to the xref table, reads the keyword
            // "xref" as a token in an object context, and throws "Unexpected token 'xref'".
            // Fix: catch the reopen failure, pipe the saved file through PDFium (which has
            // robust error recovery and will rewrite a correct xref), then retry the open.
            try
            {
                _doc = PdfWorkingDocument.Open(tempPath);
            }
            catch (Exception openEx) when (PdfImport.IsXRefException(openEx))
            {
                var fixedPath = App.MakeTempFile("fixed");
                if (!PdfiumInterop.TryCreateZeroRotationCopy(tempPath, fixedPath))
                    throw; // PDFium also failed - re-throw original reopen error
                tempPath = fixedPath;
                _doc = PdfWorkingDocument.Open(tempPath);
            }
            _currentFile = tempPath;
            if (documentUndo is { } completedUndo)
                ActiveViewer.PushUndoExt(completedUndo);

            if (preserveRenderedPages)
            {
                ActiveViewer.RefreshFormFieldsExt(selectedIdx);
                return;
            }

            // Clear once more after the old workers have observed cancellation. This closes the race
            // where a worker was already inside PDFium when the first clear happened and published its
            // stale result while the edited document was being saved and reopened.
            Controls.PdfViewer.InvalidateRenderCacheExt(_active);

            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            // In Continuous view the strip caches one rendered slot per page. After a
            // page-modifying reload (e.g. crop) it must be rebuilt so the main view reflects the
            // new pages; the slot-sizing in RenderContinuousPages makes cropped pages fit cleanly.
            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = PageList.SelectedIndex;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => ActiveViewer.SetupContinuousView(contIdx)));
                return;
            }

            // Refit synchronously so the first rendered frame uses the correct zoom. For crop we instead
            // keep the current zoom (preserveZoom) so the page doesn't jump to fit the smaller cropped size -
            // the user just wanted the cropped-away area removed, not a zoom change.
            PagePreviewPanel.ScrollToHorizontalOffset(0);
            if (preserveZoom) { _fitMode = FitMode.None; ActiveViewer.ApplyZoom(); }
            else ActiveViewer.ReapplyGridOrFit();

            // Deferred refit after layout settles for accurate ActualWidth.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                // RefreshPageView only manages secondary tiles and links. It does not repaint the
                // primary Image, which is why the thumbnail changed after an edit while the document
                // stayed stale until a view-mode switch called RenderPage. Render the primary first,
                // then fit the new page dimensions.
                int refreshPage = _viewMode == ViewMode.Grid ? 0 : _currentPage;
                if (refreshPage >= 0) ActiveViewer.RenderPage(refreshPage);
                if (preserveZoom) ActiveViewer.ApplyZoom();
                else ActiveViewer.ReapplyGridOrFit();
            }));
        }
    }
}
