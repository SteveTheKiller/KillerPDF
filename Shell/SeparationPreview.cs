using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Separations in the Document tools (#264): the page plate by plate, with the earlier text
    // report one click away.
    public partial class MainWindow
    {
        private void ShowSeparationPreview()
        {
            if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }
            CommitActiveTextBox();
            string file = _currentFile;
            OpenedProtection protection = _protection;
            void Report() => ShowDocumentReport(Loc("Str_DocumentTools_Separations"), (path, reportProtection) =>
            {
                var production = PdfEngineIntegration.InspectPrintProduction(path, reportProtection);
                return PdfEngineIntegration.CreateSeparationPreview(path,
                    production.Colorants.Select(colorant => colorant.Name), reportProtection).ToText();
            });
            try
            {
                var production = PdfEngineIntegration.InspectPrintProduction(file, protection);
                var selection = PdfEngineIntegration.CreateSeparationPreview(file,
                    production.Colorants.Select(colorant => colorant.Name), protection);
                SeparationPreviewDialog.Plate[] plates = [.. selection.Plates.Select(plate =>
                    new SeparationPreviewDialog.Plate(plate.Name, plate.PageIndexes.ToHashSet()))];
                new SeparationPreviewDialog(this, file, protection, _doc.PageCount, _currentPage, plates, Report).ShowDialog();
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Sep_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
