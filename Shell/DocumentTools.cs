using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_Portfolio"),
            (_, _) => ShowDocumentReport(Loc("Str_DocumentTools_Portfolio"),
                PdfEngineIntegration.InspectPortfolio), glyph: "\uE8B7"));
        var comparison = MakeMenuItem(Loc("Str_DocumentTools_StructuralComparison"),
            (_, _) => ShowStructuralComparison(), glyph: "\uE8AB");
        comparison.IsEnabled = IsSplit
            && !string.IsNullOrWhiteSpace(Viewer.CurrentFilePathExt)
            && !string.IsNullOrWhiteSpace(ViewerB.CurrentFilePathExt);
        menu.Items.Add(comparison);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem(Loc("Str_DocumentTools_InsertToc"),
            (_, _) => InsertTableOfContents(), glyph: "\uE8FD"));
        return menu;
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
