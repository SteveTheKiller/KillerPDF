using System.IO;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private async void RecognizeFormFields()
    {
        if (_doc is null || !EnsureEditable()) return;
        string? snapshot = null;
        var busy = ShowBusyOverlay(Loc("Str_FormTools_Recognize"));
        CancellationToken token = BeginCancellableOp(Loc("Str_FormTools_Recognize"));
        try
        {
            snapshot = CreateExchangeSnapshot();
            var sourceViewer = ActiveViewer;
            var sourceSession = sourceViewer.ActiveSessionExt;
            var sourceDocument = _doc;
            bool IsSourceCurrent() => ReferenceEquals(ActiveViewer, sourceViewer)
                && ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
                && ReferenceEquals(_doc, sourceDocument);
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(snapshot));
            PdfFormRecognitionReview proposals = await Task.Run(() =>
                PdfFormRecognizer.Recognize(document, cancellationToken: token), token);
            HideBusyOverlay(busy);
            if (!IsSourceCurrent() || token.IsCancellationRequested) return;
            if (proposals.Proposals.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_FormTools_None"));
                return;
            }
            var review = new FormRecognitionReviewDialog(this, document, proposals);
            if (review.ShowDialog() != true || review.Result is null) return;
            if (!IsSourceCurrent() || token.IsCancellationRequested) return;
            ApplyFormWorkflow(path => PdfEngineIntegration.ApplyRecognizedFields(path, review.Result), snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowExchangeError(error); }
        finally
        {
            HideBusyOverlay(busy);
            EndCancellableOp(token);
            if (snapshot is not null) File.Delete(snapshot);
        }
    }

    private void ConvertXfaForm()
    {
        if (_doc is null || !EnsureEditable()) return;
        var sourceViewer = ActiveViewer;
        var sourceSession = sourceViewer.ActiveSessionExt;
        var sourceDocument = _doc;
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            var review = new ImportReviewDialog(this, Loc("Str_FormTools_ConvertXfa"),
                Loc("Str_FormTools_XfaConfirm"), Loc("Str_FormTools_FlattenOutput"),
                Loc("Str_FormTools_ConvertXfa"));
            if (review.ShowDialog() != true) return;
            if (!ReferenceEquals(ActiveViewer, sourceViewer)
                || !ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
                || !ReferenceEquals(_doc, sourceDocument)) return;
            // Validate the chosen conversion before starting the document reload.
            _ = PdfXfaAcroFormConverter.Convert(PdfDocument.Open(File.ReadAllBytes(snapshot)),
                review.Option ? PdfXfaConversionMode.Flattened : PdfXfaConversionMode.Editable);
            ApplyFormWorkflow(path => PdfEngineIntegration.WriteExchangeFile(path,
                PdfXfaAcroFormConverter.Convert(PdfDocument.Open(File.ReadAllBytes(path)),
                    review.Option ? PdfXfaConversionMode.Flattened : PdfXfaConversionMode.Editable)), snapshot);
        }
        catch (Exception error) { ShowExchangeError(error); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private void FlattenFormFields()
    {
        if (_doc is null || !EnsureEditable()) return;
        var sourceViewer = ActiveViewer;
        var sourceSession = sourceViewer.ActiveSessionExt;
        var sourceDocument = _doc;
        if (KillerDialog.Show(this, Loc("Str_FormTools_FlattenConfirm"), Loc("Str_FormTools_Flatten"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(ActiveViewer, sourceViewer)
            || !ReferenceEquals(sourceViewer.ActiveSessionExt, sourceSession)
            || !ReferenceEquals(_doc, sourceDocument)) return;
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            ApplyFormWorkflow(path => PdfEngineIntegration.WriteExchangeFile(path,
                PdfFormFlattener.Flatten(PdfDocument.Open(File.ReadAllBytes(path)))), snapshot);
        }
        catch (Exception error) { ShowExchangeError(error); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private void ApplyFormWorkflow(Action<string> apply, string snapshot)
    {
        UndoEntry? undo = CaptureDocumentUndo();
        if (undo is { } captured) undo = captured with { DocBytes = File.ReadAllBytes(snapshot) };
        SaveTempAndReload(keepAnnotations: true, documentUndo: undo, finalizeSavedFile: path =>
        {
            WriteFormValuesToDocument(path);
            apply(path);
        }, onPublished: () =>
        {
            ActiveViewer.FormTextValuesRef.Clear();
            ActiveViewer.FormChoiceValuesRef.Clear();
            ActiveViewer.FormMultiChoiceValuesRef.Clear();
            ActiveViewer.FormCheckValuesRef.Clear();
            ActiveViewer.FormRadioValuesRef.Clear();
            ActiveViewer.FormFontSizesRef.Clear();
            ActiveViewer.FormTextStylesRef.Clear();
        });
        MarkDirty();
    }
}
