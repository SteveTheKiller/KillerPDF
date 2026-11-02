using System.IO;
using System.Security.Cryptography;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenGuidedTasks()
    {
        if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        try
        {
            if (_doc.IsReadOnly || IsOwnerRestricted || PdfSignatureReader.Read(EnsureEngineDocumentSession().Document).Any(item => item.IsSigned))
            { KillerDialog.Show(this, Loc("Str_Guided_Unavailable")); return; }
            if (!EnsureMayWrite(PdfOutput.Printable)) return;
            CommitActiveTextBox();
            long revision = _documentEditRevision;
            Func<bool> origin = CaptureDocumentOrigin();
            var sourceViewer = ActiveViewer;
            List<PdfRedactionRegion> marked = sourceViewer.PendingRedactionRegions();
            string sourcePath = _currentFile, originalPath = _originalFile ?? sourcePath;
            OpenedProtection protection = _protection;
            byte[] hash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            void Current(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!origin() || revision != _documentEditRevision || !File.Exists(sourcePath)
                    || !SHA256.HashData(File.ReadAllBytes(sourcePath)).AsSpan().SequenceEqual(hash))
                    throw new InvalidOperationException(Loc("Str_Guided_Changed"));
            }
            async Task<GuidedTaskResult> Run(GuidedTasksDialog owner, GuidedTaskRequest request, IProgress<string> progress, CancellationToken token)
            {
                Current(token);
                string destination = PdfEngineIntegration.PlanBatchOutputs([originalPath], request.Output).Single();
                _ = PdfEngineIntegration.PlanBatchOutputs([sourcePath], destination);
                bool overwrite = File.Exists(destination);
                if (overwrite && KillerDialog.Show(owner, string.Format(Loc("Str_Guided_Replace"), destination), Loc("Str_Guided_Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) throw new OperationCanceledException();
                Current(token);
                if (request.Kind == GuidedTaskKind.Scan && request.Ocr
                    && !await EnsureOcrModelsReadyAsync(owner, token)) throw new OperationCanceledException();
                Current(token);
                string? snapshot = null;
                try
                {
                    snapshot = WriteSavedCopyToTemp();
                    byte[] source = await File.ReadAllBytesAsync(snapshot, token);
                    Current(token);
                    byte[] result;
                    if (request.Kind == GuidedTaskKind.Privacy)
                    {
                        progress.Report(Loc("Str_Guided_Privacy"));
                        List<PdfRedactionRegion> regions = [.. marked];
                        if (request.Search)
                        {
                            // The saved copy holds the tab's own bytes, so it reads under the tab's protection.
                            var find = new RedactionFindDialog(owner, () => SearchService.LoadPagesForReview(snapshot, protection),
                                () => PdfEngineIntegration.OpenForReading(File.ReadAllBytes(snapshot), protection));
                            if (find.ShowDialog() != true) throw new OperationCanceledException();
                            Current(token);
                            foreach (PdfRedactionMatch match in find.Checked)
                                foreach (PdfContentBounds area in match.Areas.Count > 0 ? match.Areas : [match.Bounds])
                                    if (!regions.Any(region => region.PageIndex == match.PageIndex && region.Bounds == area))
                                        regions.Add(new(match.PageIndex, area, match.ReasonCode, match.OverlayText));
                        }
                        if (regions.Count == 0) throw new InvalidOperationException(Loc("Str_Guided_NoSelection"));
                        PdfContentRedactionPlan plan = await Task.Run(() => PdfEngineIntegration.AnalyzeRedaction(PdfDocument.Open(source), regions), token);
                        Current(token);
                        var approved = new List<int>(); var skipped = new HashSet<int>();
                        if (!sourceViewer.ApproveRasterPages(plan.RasterPages, approved, skipped, owner)) throw new OperationCanceledException();
                        Current(token);
                        regions.RemoveAll(region => skipped.Contains(region.PageIndex));
                        if (regions.Count == 0) throw new InvalidOperationException(Loc("Str_Guided_NoSelection"));
                        if (KillerDialog.Show(owner, string.Format(Loc("Str_Guided_RedactConfirm"), regions.Count,
                            regions.Select(region => region.PageIndex).Distinct().Count()), Loc("Str_Guided_Privacy"),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) throw new OperationCanceledException();
                        Current(token);
                        await Task.Run(() => PdfEngineIntegration.ApplyRedaction(snapshot, regions, approved), token);
                        result = await File.ReadAllBytesAsync(snapshot, token);
                    }
                    else if (request.Kind == GuidedTaskKind.Form)
                    {
                        progress.Report(Loc("Str_FormTools_Recognize"));
                        PdfDocument document = PdfDocument.Open(source);
                        PdfFormRecognitionReview proposals = await Task.Run(() => PdfFormRecognizer.Recognize(document, cancellationToken: token), token);
                        Current(token);
                        if (proposals.Proposals.Count == 0) throw new InvalidOperationException(Loc("Str_FormTools_None"));
                        var review = new FormRecognitionReviewDialog(owner, document, proposals);
                        if (review.ShowDialog() != true || review.Result is null) throw new OperationCanceledException();
                        Current(token);
                        if (!review.Result.Proposals.Any(proposal => proposal.Status == PdfFormProposalStatus.Accepted))
                            throw new InvalidOperationException(Loc("Str_Guided_NoSelection"));
                        result = await Task.Run(() => review.Result.ApplyAccepted(document), token);
                    }
                    else
                    {
                        PdfMacro macro = GuidedTasksDialog.Macro(request, Loc(request.Kind == GuidedTaskKind.Email ? "Str_Guided_Email" : "Str_Guided_Scan"));
                        PdfMacroDispatcher dispatcher = CreateMacroDispatcher();
                        PdfMacroFileResult completed = await Task.Run(() => AssertSingleResult(dispatcher.RunReport(macro,
                            [new ReadOnlyMemory<byte>(source)], cancellationToken: token)), token);
                        token.ThrowIfCancellationRequested();
                        if (!completed.Succeeded || completed.Data is not ReadOnlyMemory<byte> data)
                            throw new InvalidOperationException(completed.Error ?? Loc("Str_Macro_Failed"));
                        result = data.ToArray();
                    }
                    Current(token);
                    WriteMacroOutput(destination, result, overwrite, token);
                    return new(destination, source.LongLength, result.LongLength);
                }
                finally { if (snapshot is not null) File.Delete(snapshot); }
            }
            var dialog = new GuidedTasksDialog(this, originalPath, _appScale, marked.Count, Run);
            dialog.ShowDialog();
            if (dialog.OpenPath is string path && File.Exists(path)) OpenFromExternal(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowExchangeError(error); }
    }
}
