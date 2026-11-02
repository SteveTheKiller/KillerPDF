using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Reduce File Size (#433): saves a smaller copy and leaves the open document as it is.
    public partial class MainWindow
    {
        private async void ReduceFileSize_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable() || !EnsureMaySaveUnprotected()) return;
            CommitActiveTextBox();
            var options = new ReduceSizeDialog(this);
            options.ShowDialog();
            if (!options.Confirmed) return;

            string seed = _originalFile ?? _currentFile;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
                Title = Loc("Str_Reduce_Title"),
                CheckFileExists = false,
                CheckPathExists = true,
                RequireFilterExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(seed) + "-reduced.pdf"
            };
            if (dlg.ShowDialog(this) != true) return;
            // The reduced copy is a new file, so a signed source or one with Reader usage rights loses them in it,
            // and WriteSavedCopyToTemp clears them before the optimizer runs. Say so before the file is touched.
            bool sourceSigned;
            try { sourceSigned = PdfEngineIntegration.HasSignedSignatures(_currentFile); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { sourceSigned = false; }   // an unreadable source is reported by the reduce itself
            if (sourceSigned)
            {
                if (KillerDialog.Show(this,
                        string.Format(Loc("Str_Reduce_ClearsSignature"), System.IO.Path.GetFileName(dlg.FileName)),
                        Loc("Str_Dlg_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                        defaultResult: MessageBoxResult.Cancel) != MessageBoxResult.OK)
                    return;
            }
            else if (!ConfirmSignatureRemoval(dlg.FileName)) return;
            CloseEngineDocumentSession();
            OfferRescaleOutOfRangePages();

            Border? busy = null;
            bool started = false;
            CancellationToken ct = default;
            try
            {
                string source = WriteSavedCopyToTemp();
                ct = BeginCancellableOp(Loc("Str_Reduce_Title"));
                started = true;
                busy = ShowBusyOverlay(Loc("Str_Reduce_Working"));
                PdfImageDownsampleOptions? images = options.Downsample
                    ? new PdfImageDownsampleOptions
                    {
                        TargetDpi = options.Dpi,
                        ThresholdDpi = options.Dpi * 1.5,
                        JpegQuality = options.Quality,
                        JpegEncoder = PdfImageJpeg.Encode
                    }
                    : null;
                string output = dlg.FileName;
                var (before, after, resampled, repairs) = await Task.Run(
                    () => PdfEngineIntegration.ReduceFileSize(source, output, images, ct), ct);
                SetStatus(string.Format(Loc("Str_Reduce_Done"), System.IO.Path.GetFileName(output),
                    Megabytes(before), Megabytes(after), resampled)
                    + (repairs.Count > 0 ? " " + Loc("Str_Save_ReferenceRepairs") : ""));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Reduce_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                if (started) EndCancellableOp(ct);
            }
        }

        private static string Megabytes(long bytes) =>
            (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.00" : "0.0",
                System.Globalization.CultureInfo.CurrentCulture) + " MB";

        // The bytes Save As would write, in a temp file, without touching the open document's
        // state: annotations and stamps burned in, form values and page rotations applied. A damaged
        // signed, certified or usage-rights document is refused, because the output stands in for it.
        // Extract Pages and Export pass throwaway: their output is a new file the protection never
        // carries into, so the copy is readied instead.
        private string WriteSavedCopyToTemp(bool forRasterization = false, bool throwaway = false)
        {
            Dictionary<int, int> rotations = SnapshotPageRotations();
            string clean = App.MakeTempFile("reduceclean");
            string copy = App.MakeTempFile("reduce");
            try
            {
                _doc!.Save(clean);
                if (throwaway) PdfEngineIntegration.PrepareThrowawayCopy(clean);
                else PdfEngineIntegration.RepairHarmlessSaveArtifacts(clean);
                PdfEngineIntegration.StripLinkAppearances(clean);
                System.IO.File.Copy(clean, copy, true);
                PdfEngineIntegration.ClearInvalidatedSignatures(copy);
                if (_annotations.Values.Any(list => list.Count > 0) || HasActiveStamps)
                {
                    PdfEngineBurn.Burn(copy, _annotations, _renderDims, _docStampSpec, null, _pageRotations,
                        forRasterization: forRasterization, measurementsAsAnnotations: true);
                    PdfEngineIntegration.StripLinkAppearances(copy);
                }
                WriteFormValuesToDocument(copy);
                WriteRedactionMarksToDocument(copy);
                WriteMeasurementsToDocument(copy);
                PdfEngineIntegration.ApplyPageRotations(copy, rotations);
                return copy;
            }
            catch { System.IO.File.Delete(copy); throw; }
            finally { System.IO.File.Delete(clean); }
        }
    }
}
