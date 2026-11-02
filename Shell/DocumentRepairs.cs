using System.IO;
using System.Windows;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenPreflightTools() => OpenExchangeCommands("Str_DocumentTools_Preflight",
        ("Str_DocumentTools_Preflight", () => ShowDocumentReport(Loc("Str_DocumentTools_Preflight"),
            (path, _) => PdfEngineIntegration.InspectPreflight(path).ToText())),
        ("Str_DocumentTools_PreflightProfile", PreflightWithProfileFile),
        ("Str_Repair_Language", SetPreflightLanguage),
        ("Str_Repair_Metadata", FillMissingMetadata),
        ("Str_Repair_PageBox", ChooseProductionPageBox),
        ("Str_Repair_OutputIntent", AddOutputIntent));

    private void PreflightWithProfileFile()
    {
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = Loc("Str_DocumentTools_PreflightProfile"), Filter = "JSON|*.json"
        };
        if (picker.ShowDialog(this) != true) return;
        PdfPreflightProfile profile;
        try { profile = PdfPreflightProfile.FromJson(File.ReadAllText(picker.FileName)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or NotSupportedException or System.Text.Json.JsonException or IOException
            or UnauthorizedAccessException)
        {
            KillerDialog.Show(this, Loc("Str_Err_PreflightProfile") + "\n" + error.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ShowDocumentReport(Loc("Str_DocumentTools_PreflightProfile"),
            (path, _) => PdfEngineIntegration.InspectPreflight(path, profile).ToText());
    }

    private void SetPreflightLanguage()
    {
        var isSourceCurrent = CaptureDocumentOrigin();
        var input = new DocumentValuesDialog(this, Loc("Str_Repair_Language"),
            (Loc("Str_Repair_Language"), ""));
        if (input.ShowDialog() != true || !isSourceCurrent()) return;
        ReviewDocumentRepair(Loc("Str_Repair_Language") + ": " + input.Values[0], document =>
            PdfPreflightCorrectionPlan.SetDocumentLanguage(document, input.Values[0]).Apply());
    }

    private void FillMissingMetadata() => WithRepairDocument((document, isSourceCurrent) =>
    {
        var info = PdfDocumentInformation.Read(document);
        var commands = new[]
        {
            ("Str_DocInfo_Title", PdfPreflightMetadataField.Title, info.Title),
            ("Str_DocInfo_Author", PdfPreflightMetadataField.Author, info.Author),
            ("Str_DocInfo_Subject", PdfPreflightMetadataField.Subject, info.Subject),
            ("Str_DocInfo_Keywords", PdfPreflightMetadataField.Keywords, info.Keywords)
        }.Where(item => string.IsNullOrWhiteSpace(item.Item3)).ToArray();
        if (commands.Length == 0) { KillerDialog.Show(this, Loc("Str_Repair_NoChanges")); return; }
        OpenExchangeCommands("Str_Repair_Metadata", commands.Select(item =>
            (item.Item1, (Action)(() =>
            {
                var input = new DocumentValuesDialog(this, Loc("Str_Repair_Metadata"), (Loc(item.Item1), ""));
                if (input.ShowDialog() != true || !isSourceCurrent()) return;
                ReviewDocumentRepair(Loc(item.Item1) + ": " + input.Values[0], document =>
                    PdfPreflightCorrectionPlan.SetDocumentMetadata(document, item.Item2, input.Values[0]).Apply());
            }))).ToArray());
    });

    private void ChooseProductionPageBox() => OpenExchangeCommands("Str_Repair_PageBox",
        ("Str_Repair_Bleed", () => ClearProductionPageBox(PdfPageBox.Bleed)),
        ("Str_Repair_Trim", () => ClearProductionPageBox(PdfPageBox.Trim)),
        ("Str_Repair_Art", () => ClearProductionPageBox(PdfPageBox.Art)));

    private void ClearProductionPageBox(PdfPageBox box)
    {
        int page = Math.Max(0, PageList.SelectedIndex);
        string boxKey = box == PdfPageBox.Bleed ? "Str_Repair_Bleed"
            : box == PdfPageBox.Trim ? "Str_Repair_Trim" : "Str_Repair_Art";
        ReviewDocumentRepair(Loc("Str_Repair_PageBox") + ": " + Loc(boxKey) + " (" + RepairPageLabel(page) + ")",
            document => PdfPreflightCorrectionPlan.ClearProductionPageBox(document, page, box).Apply());
    }

    private void AddOutputIntent()
    {
        var isSourceCurrent = CaptureDocumentOrigin();
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = Loc("Str_Repair_OutputIntent"), Filter = "ICC|*.icc;*.icm"
        };
        if (picker.ShowDialog(this) != true || !isSourceCurrent()) return;
        var input = new DocumentValuesDialog(this, Loc("Str_Repair_OutputIntent"),
            (Loc("Str_Repair_OutputCondition"), Path.GetFileNameWithoutExtension(picker.FileName)));
        if (input.ShowDialog() != true || !isSourceCurrent()) return;
        try
        {
            PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(picker.FileName));
            ReviewDocumentRepair(Loc("Str_Repair_OutputIntent") + ": " + input.Values[0], document =>
                PdfPreflightCorrectionPlan.SetOutputIntent(document, profile, input.Values[0]).Apply());
        }
        catch (Exception error) { ShowExchangeError(error); }
    }

    private void ReviewDocumentRepair(string title, Func<PdfDocument, byte[]> build, bool accessibility = false)
    {
        if (_doc is null || !EnsureEditable()) return;
        var isSourceCurrent = CaptureDocumentOrigin();
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            byte[] original = File.ReadAllBytes(snapshot);
            byte[] proposed = build(PdfDocument.Open(original));
            if (original.AsSpan().SequenceEqual(proposed))
            {
                KillerDialog.Show(this, Loc("Str_Repair_NoChanges"));
                return;
            }
            string before = accessibility
                ? PdfAccessibilityInspector.Inspect(PdfDocument.Open(original)).ToText()
                : PdfPreflightRunner.Run(original, PdfPreflightProfile.PrintProduction).ToText();
            string report = accessibility
                ? PdfAccessibilityInspector.Inspect(PdfDocument.Open(proposed)).ToText()
                : PdfPreflightRunner.Run(proposed, PdfPreflightProfile.PrintProduction).ToText();
            var review = new ImportReviewDialog(this, title, title + "\n\n" + Loc("Str_Repair_Before") + "\n" + before
                + "\n\n" + Loc("Str_Repair_After") + "\n" + report,
                null, Loc("Str_Repair_Apply"));
            if (review.ShowDialog() != true || !isSourceCurrent()) return;
            ApplyFormWorkflow(path => PdfEngineIntegration.ApplyDocumentRepair(path, build), snapshot);
        }
        catch (Exception error) { ShowExchangeError(error); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }
}
