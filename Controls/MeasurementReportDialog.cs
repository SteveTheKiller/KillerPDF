using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

// The Measurements report: every measurement in the document, page by page, with its value and
// the scale it was taken at, as a table. Copy puts the list on the clipboard with tabs between
// the columns, so it pastes into a spreadsheet as cells; Save writes it as CSV or JSON. A saved file
// is a data export of the document, so Save asks mayExport first, while the list and Copy stay free.
internal sealed class MeasurementReportDialog : Window
{
    private readonly IReadOnlyList<PdfMeasurementResult> _results;
    private readonly string _baseName;
    private readonly Func<bool> _mayExport;

    internal MeasurementReportDialog(Window owner, IReadOnlyList<PdfMeasurementResult> results, string baseName,
        Func<bool> mayExport)
    {
        _results = results;
        _baseName = baseName;
        _mayExport = mayExport;
        Title = "KillerPDF - " + L("Str_MeasureReport_Title");
        // The window fits the list: small for a few measurements, growing with the list until it
        // reaches most of the screen's height, after which the list scrolls.
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;
        MaxWidth = 900;
        MaxHeight = Math.Min(720, SystemParameters.WorkArea.Height * 0.8);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };

        string report = ReportText(results);
        bool any = results.Count > 0;
        var table = new Grid { Margin = new Thickness(12, 4, 12, 10) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FillTable(table, results);
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = table,
            // The empty well keeps a little height for its hint.
            MinHeight = any ? 0 : 64
        };
        System.Windows.Automation.AutomationProperties.SetName(scroller, L("Str_MeasureReport_Title"));

        // Copying confirms on the footer's left, where the copy icon's tooltip alone would go unseen.
        var copied = new TextBlock { Text = L("Str_DocumentTools_Copied"), Visibility = Visibility.Hidden };
        copied.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Button copy = DialogParts.IconAction("Str_DocumentTools_Copy", 0xE8C8, (_, _) =>
        {
            try
            {
                Clipboard.SetText(report);
                copied.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
        Button csv = DialogParts.IconAction("Str_MeasureReport_SaveCsv", 0xE898,
            (_, _) => Save("csv", "CSV|*.csv", () => PdfMeasurementReport.ToCsv(_results)));
        Button json = DialogParts.IconAction("Str_MeasureReport_SaveJson", 0xE74E,
            (_, _) => Save("json", "JSON|*.json", () => PdfMeasurementReport.ToJson(_results)));
        copy.IsEnabled = csv.IsEnabled = json.IsEnabled = any;
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        DockPanel footer = DialogParts.Footer([close], copied);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        // The count heads the list, with the copy and save icons beside it.
        string heading = any ? string.Format(L("Str_MeasureReport_Count"), results.Count) : L("Str_MeasureReport_Title");
        DockPanel header = DialogParts.Header(heading, copy, csv, json);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(DialogParts.Well(scroller, any ? null : DialogParts.EmptyHint("Str_MeasureReport_None")));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
    }

    // A header per page over its measurements: type, value, and scale.
    private static void FillTable(Grid table, IReadOnlyList<PdfMeasurementResult> results)
    {
        void Cell(string text, int column, int span = 1, string brush = "TextBrush",
            FontWeight? weight = null, Thickness? margin = null)
        {
            if (column == 0) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = weight ?? FontWeights.Normal,
                Margin = margin ?? new Thickness(0, 2, column < 2 ? 28 : 0, 2)
            };
            cell.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            cell.SetResourceReference(TextBlock.ForegroundProperty, brush);
            Grid.SetRow(cell, table.RowDefinitions.Count - 1);
            Grid.SetColumn(cell, column);
            Grid.SetColumnSpan(cell, span);
            table.Children.Add(cell);
        }

        bool first = true;
        foreach (var page in results.GroupBy(result => result.PageIndex))
        {
            Cell(string.Format(L("Str_MeasureReport_Page"), page.Key + 1), 0, 3,
                weight: FontWeights.SemiBold, margin: new Thickness(0, first ? 4 : 10, 0, 3));
            first = false;
            foreach (PdfMeasurementResult result in page)
            {
                Cell(result.Kind, 0);
                Cell(result.Label ?? "", 1);
                Cell(result.Profile != "-" ? result.Profile : "", 2, brush: "MutedTextBrush");
            }
        }
    }

    // The same list as text, with a tab between columns: "Distance<tab>12.50 ft<tab>1 in = 10 ft".
    private static string ReportText(IReadOnlyList<PdfMeasurementResult> results)
    {
        if (results.Count == 0) return L("Str_MeasureReport_None");
        var text = new StringBuilder();
        text.AppendLine(string.Format(L("Str_MeasureReport_Count"), results.Count));
        foreach (var page in results.GroupBy(result => result.PageIndex))
        {
            text.AppendLine();
            text.AppendLine(string.Format(L("Str_MeasureReport_Page"), page.Key + 1));
            foreach (PdfMeasurementResult result in page)
            {
                text.Append(result.Kind).Append('\t').Append(result.Label ?? "");
                if (result.Profile != "-") text.Append('\t').Append(result.Profile);
                text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }

    private void Save(string extension, string filter, Func<string> content)
    {
        string? ChoosePath()
        {
            var dialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = filter,
                Title = L("Str_MeasureReport_Title"),
                CheckFileExists = false,
                CheckPathExists = true,
                RequireFilterExtension = true,
                FileName = _baseName + " - " + L("Str_MeasureReport_Title") + "." + extension
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        try
        {
            if (Services.GatedReportSave.Save(_mayExport, ChoosePath, content) is not string saved) return;
            KillerDialog.Show(this, string.Format(L("Str_MeasureReport_Saved"), Path.GetFileName(saved)),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
