using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPDF.Services;

namespace KillerPDF;

public partial class MainWindow
{
    private MenuItem BuildDocumentToolsMenu()
    {
        var menu = new MenuItem { Header = Loc("Str_DocumentTools") };
        menu.Icon = new TextBlock
        {
            Text = "\uE9D2",
            FontFamily = UiKit.IconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Preflight"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Preflight"),
                path => PdfEngineIntegration.InspectPreflight(path).ToText()), glyph: "\uE9D9"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_PrintProduction"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_PrintProduction"),
                path => PdfEngineIntegration.InspectPrintProduction(path).ToText()), glyph: "\uE7C3"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Separations"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Separations"), path =>
            {
                var production = PdfEngineIntegration.InspectPrintProduction(path);
                return PdfEngineIntegration.CreateSeparationPreview(path,
                    production.Colorants.Select(colorant => colorant.Name)).ToText();
            }), glyph: "\uE790"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Accessibility"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Accessibility"),
                path => PdfEngineIntegration.InspectAccessibility(path).ToText()), glyph: "\uE776"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Layers"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Layers"),
                path => PdfEngineIntegration.InspectLayers(path).ToText()), glyph: "\uE8A1"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_EditLayers"),
            (_, _) => EditLayers(), glyph: "\uE70F"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Portfolio"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Portfolio"),
                PdfEngineIntegration.InspectPortfolio), glyph: "\uE8B7"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_EditPortfolio"),
            (_, _) => EditPortfolio(), glyph: "\uE70F"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_InitialView"),
            (_, _) => EditInitialView(), glyph: "\uE7B3"));
        var comparison = MakeMenuItem(Loc("Str_DocumentTools_StructuralComparison"),
            (_, _) => ShowStructuralComparison(), glyph: "\uE8AB");
        comparison.IsEnabled = IsSplit
            && !string.IsNullOrWhiteSpace(Viewer.CurrentFilePathExt)
            && !string.IsNullOrWhiteSpace(ViewerB.CurrentFilePathExt);
        menu.Items.Add(comparison);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_DataMerge"),
            (_, _) => RunDataMerge(), glyph: "\uE8F1"));
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_InsertToc"),
            (_, _) => InsertTableOfContents(), glyph: "\uE8FD"));
        return menu;
    }

    private async void RunDataMerge()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        var dataDialog = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = Loc("Str_DataMerge_SelectData"),
            Filter = Loc("Str_DataMerge_DataFiles") + "|*.csv;*.json;*.xlsx|"
                + Loc("Str_Dlg_AllFiles") + "|*.*"
        };
        if (dataDialog.ShowDialog(this) != true) return;

        IReadOnlyList<IReadOnlyDictionary<string, string?>> records;
        try
        {
            records = PdfDataMergeWorkflow.LoadRecords(dataDialog.FileName);
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (records.Count == 0)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_NoRecords"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile) + "-merged.pdf";
        var outputDialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_DataMerge_SelectOutput"),
            Filter = "PDF|*.pdf",
            DefaultExt = "pdf",
            FileName = stem
        };
        if (outputDialog.ShowDialog(this) != true) return;

        CommitActiveTextBox();
        byte[] templateBytes;
        PdfDataMergePlan plan;
        try
        {
            templateBytes = File.ReadAllBytes(_currentFile);
            plan = PdfDataMergeWorkflow.CreatePlan(
                PdfDocument.Open(templateBytes), records, Path.GetFileName(outputDialog.FileName));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string outputDirectory = Path.GetDirectoryName(outputDialog.FileName)!;
        int collisions = plan.OutputFileNames.Count(name =>
            File.Exists(Path.Combine(outputDirectory, name)));
        if (collisions > 0 && KillerDialog.Show(this,
                string.Format(Loc("Str_DataMerge_Overwrite"), collisions), "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var busy = ShowBusyOverlay(Loc("Str_DataMerge_Processing"));
        CancellationToken token = BeginCancellableOp(Loc("Str_DocumentTools_DataMerge"));
        try
        {
            PdfDataMergeBatchReport report = await Task.Run(() =>
            {
                IReadOnlyList<PdfDataMergeDocumentResult> generated = PdfDataMerge.RunFormBatch(
                    PdfDocument.Open(templateBytes), plan.Records, plan.Profile,
                    PdfDataMergeOutputMode.Editable, token);
                var completed = new List<PdfDataMergeDocumentResult>(generated.Count);
                foreach (PdfDataMergeDocumentResult result in generated)
                {
                    if (!result.Succeeded || result.Data is null || result.OutputFileName is null)
                    {
                        completed.Add(result);
                        continue;
                    }
                    string destination = Path.Combine(outputDirectory, result.OutputFileName);
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporary, result.Data.Value.ToArray());
                        File.Move(temporary, destination, overwrite: true);
                        completed.Add(result);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException
                        and not StackOverflowException and not AccessViolationException)
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); }
                        catch { }
                        completed.Add(new PdfDataMergeDocumentResult(
                            result.RecordIndex, result.OutputFileName, null, ex.Message));
                    }
                }
                return PdfDataMergeBatchReport.Create(completed);
            });

            HideBusyOverlay(busy);
            SetStatus(token.IsCancellationRequested
                ? Loc("Str_DataMerge_Canceled")
                : string.Format(Loc("Str_DataMerge_Complete"),
                    report.SucceededRecords, report.FailedRecords));
            new DocumentReportDialog(this, Loc("Str_DocumentTools_DataMerge"),
                report.ToText()).ShowDialog();
        }
        catch (OperationCanceledException)
        {
            HideBusyOverlay(busy);
            SetStatus(Loc("Str_DataMerge_Canceled"));
        }
        catch (Exception ex)
        {
            HideBusyOverlay(busy);
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndCancellableOp();
        }
    }

    private void InsertTableOfContents()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var initialPlan = PdfEngineIntegration.PlanTableOfContents(_currentFile, 6);
            if (initialPlan.Entries.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_DocumentTools_TocNeedsBookmarks"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new TableOfContentsDialog(this, initialPlan.Entries.Count);
            if (dialog.ShowDialog() != true) return;

            var plan = PdfEngineIntegration.PlanTableOfContents(_currentFile, dialog.MaximumDepth);
            const int rowsPerPage = 40;
            int insertedPages = Math.Max(1, (plan.Entries.Count + rowsPerPage - 1) / rowsPerPage);
            UndoEntry? documentUndo = CaptureDocumentUndo();
            var annotationBackup = _annotations.ToDictionary(pair => pair.Key, pair => pair.Value);
            try
            {
                PageAnnotationInsertion.Shift(_annotations, 0, insertedPages);
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertTableOfContents(
                            path, dialog.ContentsTitle, dialog.MaximumDepth),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                            rotations, 0, insertedPages),
                    selectedPageAfterReload: 0,
                    documentUndo: documentUndo);
            }
            catch
            {
                _annotations.Clear();
                foreach (KeyValuePair<int, List<PageAnnotation>> pair in annotationBackup)
                {
                    foreach (PageAnnotation annotation in pair.Value)
                        annotation.PageIndex = pair.Key;
                    _annotations[pair.Key] = pair.Value;
                }
                throw;
            }
            SetStatus(string.Format(Loc("Str_DocumentTools_TocInserted"), insertedPages));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_TocFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditLayers()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var layers = PdfEngineIntegration.InspectLayers(_currentFile);
            if (layers.Groups.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_DocumentTools_NoLayers"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new LayerEditorDialog(this, layers.Groups);
            if (dialog.ShowDialog() != true) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyLayerEdits(path, dialog.Edits),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_DocumentTools_LayersUpdated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_LayersFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditInitialView()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var information = KillerPdf.Engine.Documents.PdfDocumentInformation.Read(
                EnsureEngineDocumentSession().Document);
            var dialog = new InitialViewDialog(
                this, information.InitialView, information.PageCount);
            if (dialog.ShowDialog() != true || dialog.InitialView is null) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyInitialView(path, dialog.InitialView),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_InitialView_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_InitialView_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditPortfolio()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            PdfEngineIntegration.PortfolioEditorState state =
                PdfEngineIntegration.ReadPortfolioEditorState(_currentFile);
            var dialog = new PortfolioEditorDialog(
                this, state.Collection, state.AttachmentNames);
            if (dialog.ShowDialog() != true) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                {
                    if (dialog.RemoveMetadata)
                        PdfEngineIntegration.ClearPortfolioMetadata(path);
                    else
                        PdfEngineIntegration.ApplyPortfolioPresentation(
                            path, dialog.View, dialog.InitialDocument);
                },
                documentUndo: documentUndo);
            SetStatus(Loc("Str_Portfolio_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_Portfolio_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowStructuralComparison()
    {
        string? left = Viewer.CurrentFilePathExt;
        string? right = ViewerB.CurrentFilePathExt;
        if (!IsSplit || string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_ComparisonNeedsTwo"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            string report = PdfEngineIntegration.CompareStructure(left, right).ToText();
            new DocumentReportDialog(this,
                Loc("Str_DocumentTools_StructuralComparison"), report).ShowDialog();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowDocumentReport(string title, Func<string, string> createReport)
    {
        if (_doc is null)
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        string? path = _currentFile ?? _originalFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            new DocumentReportDialog(this, title, createReport(path)).ShowDialog();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
