using System;
using System.IO;
using System.Linq;
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
        return menu;
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
