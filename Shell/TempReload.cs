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

namespace KillerPDF.Shell
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
            bool preserveRenderedPages = false,
            bool remapRedactionMarks = false,
            Action<Dictionary<int, List<PageAnnotation>>>? remapAnnotations = null,
            Action? onPublished = null)
        {
            if (_doc is null || _currentFile is null) return;
            // Every user command asks for the owner password before it gets here. This catches
            // any path that does not, so a restricted PDF is never handed to a rewrite the
            // engine refuses.
            if (IsOwnerRestricted)
            {
                SetStatus(Loc("Str_Restricted_Notice"));
                return;
            }
            // Capture the previous serialized working file before any rewrite. Most operations
            // mutate through finalizeSavedFile below, while the few that adjust side state before
            // arriving here pass an explicit pre-mutation entry. Push only after every save,
            // rewrite and reopen step succeeds so a failed operation creates no phantom history.
            documentUndo ??= ActiveViewer.CaptureSerializedDocumentUndoExt(_currentFile);
            var doc = _doc;
            int selectedIdx = selectedPageAfterReload ?? PageList.SelectedIndex;
            var candidateRotations = new Dictionary<int, int>(_pageRotations);
            Dictionary<int, List<PageAnnotation>>? candidateAnnotations = null;
            if (remapAnnotations is not null)
            {
                candidateAnnotations = ActiveViewer.CaptureAnnotationStateExt();
                remapAnnotations(candidateAnnotations);
            }

            // Capture page rotations, then strip them from the serialized working copy.
            // The working copy uses unrotated MediaBox dimensions for page operations.
            // Stripping /Rotate to 0 before saving means the engine renders unrotated content
            // that fits the bitmap; RotateBitmap is applied in each render path instead.
            EnsureEngineDocumentSession().CaptureRotations(candidateRotations);
            remapRotations?.Invoke(candidateRotations);

            var tempPath = App.MakeTempFile("temp");
            var serializedPath = App.MakeTempFile("serialized");
            try
            {
                doc.Save(serializedPath);
                PdfEngineIntegration.RepairHarmlessSaveArtifacts(serializedPath);
                PdfEngineIntegration.CreateZeroRotationCopy(serializedPath, tempPath);
            }
            catch (Exception saveEx) when (PdfImport.IsXRefException(saveEx))
            {
                // PdfSharpCore fails to re-save encrypted PDFs (e.g. owner-restricted RC4 files)
                // because it encounters cross-reference tokens while serializing dirty objects.
                // Primary fallback: use the engine to load the source, strip all /Rotate values,
                // remove encryption, and save. Secondary fallback: Import mode for some xref
                // issues but fails on encrypted files; kept as a last resort).
                if (!PdfEngineIntegration.TryCreateZeroRotationCopy(
                        _currentFile!, tempPath) &&
                    !PdfImport.TryImportRepairToPath(_currentFile!, tempPath, stripRotations: true))
                    throw; // re-throw original if both fallbacks fail
            }

            // Structural operations can finalize the freshly serialized working file through
            // KillerPDF.Engine before PdfSharpCore reopens it. The callback owns atomic replacement
            // of tempPath and runs only after the base save or repair path has completed.
            Controls.RedactionMarkSnapshot? remappedMarks = null;
            if (remapRedactionMarks && finalizeSavedFile is not null)
                remappedMarks = RedactionPageEdit.Apply(tempPath,
                    ActiveViewer.CaptureRedactionMarksExt(), finalizeSavedFile);
            else
                finalizeSavedFile?.Invoke(tempPath);

            // PdfSharpCore sometimes saves a file where one object's xref offset points at the
            // xref table itself (object N offset = xref table position). When PdfSharp then tries
            // to re-open that file in Modify mode it seeks to the xref table, reads the keyword
            // "xref" as a token in an object context, and throws "Unexpected token 'xref'".
            // Fix: catch the reopen failure, rewrite the saved file through engine compatibility
            // recovery, then retry the open.
            PdfWorkingDocument candidateDocument;
            try
            {
                candidateDocument = PdfWorkingDocument.Open(tempPath, null);
            }
            catch (Exception openEx) when (PdfImport.IsXRefException(openEx))
            {
                var fixedPath = App.MakeTempFile("fixed");
                if (!PdfEngineIntegration.TryCreateZeroRotationCopy(tempPath, fixedPath))
                    throw; // Engine recovery also failed; rethrow the original reopen error.
                tempPath = fixedPath;
                candidateDocument = PdfWorkingDocument.Open(tempPath, null);
            }
            // Preparation has succeeded. Publish the replacement and side state together before
            // notifying the UI or recording history; failures above leave the live document usable.
            CloseEngineDocumentSession();
            if (!preserveRenderedPages)
            {
                _secondaryRenderCts?.Cancel();
                _continuousRenderCts?.Cancel();
                _continuousSharpenCts?.Cancel();
            }
            _doc = candidateDocument;
            _currentFile = tempPath;
            doc.Close();
            _pageRotations.Clear();
            foreach (var pair in candidateRotations) _pageRotations[pair.Key] = pair.Value;
            if (!keepAnnotations || candidateAnnotations is not null) _annotations.Clear();
            if (candidateAnnotations is not null)
                foreach (var pair in candidateAnnotations) _annotations[pair.Key] = pair.Value;
            if (remappedMarks is not null) ActiveViewer.RestoreRedactionMarksExt(remappedMarks);
            // Side state that belongs to the published document changes here, never in
            // finalizeSavedFile, which runs before the reopen that can still fail.
            onPublished?.Invoke();
            if (!preserveRenderedPages)
            {
                _renderDims.Clear();
                Controls.PdfViewer.InvalidateRenderCacheExt(_active);
                _renderedPrimaryPage = -1;
                ClearSelection();
            }
            MarkDirty();
            if (documentUndo is { } completedUndo)
                ActiveViewer.PushUndoExt(completedUndo);

            if (preserveRenderedPages)
            {
                ActiveViewer.RefreshFormFieldsExt(selectedIdx);
                return;
            }

            // Clear once more after the old workers have observed cancellation. This closes the race
            // where a render worker was active when the first clear happened and published its
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
