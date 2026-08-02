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
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload(bool keepAnnotations = false, bool preserveZoom = false)
        {
            if (_doc is null || _currentFile is null) return;
            // We're about to replace the working file with a fresh temp; release the cached PDFium link
            // handle for the outgoing file now (it reopens for the new temp on the post-reload re-render).
            CloseLinkPdfiumDoc();
            // Overlay annotations are unsaved, still-editable user work. Callers that don't change
            // page identity (crop) pass keepAnnotations:true so annotations on other pages survive
            // the reload and stay selectable/movable; they are re-rendered after the doc reopens.
            if (!keepAnnotations) _annotations.Clear();
            _renderDims.Clear();
            InvalidateRenderCache(_active);   // pages changed pixels / order: drop this tab's cached bitmaps
            _renderedPrimaryPage = -1;        // force a re-render after reload even if the same page stays selected (e.g. rotate)
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;

            // Capture page rotations, then strip them from the document before saving.
            // Docnet uses FPDF_GetPageWidth/Height (MediaBox, no rotation) to size the bitmap,
            // then renders with PDFium's page CTM which *does* include /Rotate.  For 90�/270�
            // the rendered landscape content overflows the portrait-sized bitmap and gets clipped.
            // Stripping /Rotate to 0 before saving means Docnet renders clean unrotated content
            // that fits the bitmap; RotateBitmap is applied in each render path instead.
            _pageRotations.Clear();
            for (int i = 0; i < doc.PageCount; i++)
            {
                int rot = ((doc.Pages[i].Rotate % 360) + 360) % 360;
                _pageRotations[i] = rot;
                doc.Pages[i].Rotate = 0;
            }

            var tempPath = App.MakeTempFile("temp");
            try
            {
                PdfScrub.ScrubEmptyOutlines(doc);   // #103: never write a dangling /Outlines reference
                PdfScrub.ScrubDegenerateCropBoxes(doc);   // never write a zero-size /CropBox (Adobe out-of-range)
                doc.Save(tempPath);
                doc.Close();
            }
            catch (Exception saveEx) when (PdfImport.IsXRefException(saveEx))
            {
                // PdfSharpCore fails to re-save encrypted PDFs (e.g. owner-restricted RC4 files)
                // because it encounters cross-reference tokens while serialising dirty objects.
                // Primary fallback: use PDFium (already initialised for the page preview) to
                // load the source, strip all /Rotate values, remove encryption, and save.
                // Secondary fallback: PdfSharpCore Import mode (works on some non-encrypted xref
                // issues but fails on encrypted files; kept as a last resort).
                doc.Close();
                _doc = null;
                if (!PdfiumInterop.TryPdfiumSaveWithZeroRotations(_currentFile!, tempPath) &&
                    !PdfImport.TryImportRepairToPath(_currentFile!, tempPath, stripRotations: true))
                    throw; // re-throw original if both fallbacks fail
            }
            // PdfSharpCore sometimes saves a file where one object's xref offset points at the
            // xref table itself (object N offset = xref table position). When PdfSharp then tries
            // to re-open that file in Modify mode it seeks to the xref table, reads the keyword
            // "xref" as a token in an object context, and throws "Unexpected token 'xref'".
            // Fix: catch the reopen failure, pipe the saved file through PDFium (which has
            // robust error recovery and will rewrite a correct xref), then retry the open.
            try
            {
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception openEx) when (PdfImport.IsXRefException(openEx))
            {
                var fixedPath = App.MakeTempFile("fixed");
                if (!PdfiumInterop.TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                    throw; // PDFium also failed - re-throw original reopen error
                tempPath = fixedPath;
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempPath;

            // Restore rotations in the reopened in-memory doc so saves, form fields,
            // and all other operations see the correct rotation values.
            foreach (var kv in _pageRotations)
                _doc.Pages[kv.Key].Rotate = kv.Value;

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
                    (Action)(() => SetupContinuousView(contIdx)));
                return;
            }

            // Refit synchronously so the first rendered frame uses the correct zoom. For crop we instead
            // keep the current zoom (preserveZoom) so the page doesn't jump to fit the smaller cropped size -
            // the user just wanted the cropped-away area removed, not a zoom change.
            PagePreviewPanel.ScrollToHorizontalOffset(0);
            if (preserveZoom) { _fitMode = FitMode.None; ApplyZoom(); }
            else ReapplyGridOrFit();

            // Deferred refit after layout settles for accurate ActualWidth.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                if (preserveZoom) ApplyZoom();
                else ReapplyGridOrFit();
            }));
        }
    }
}
