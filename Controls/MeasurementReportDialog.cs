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

        var body = new Grid { Margin = new Thickness(20, 6, 20, 16) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string report = ReportText(results);
        var table = new Grid { Margin = new Thickness(12, 8, 12, 10) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FillTable(table, results);
        var panel = new Border
        {
            Background = UiKit.Brush("TextFieldBrush", UiKit.Brush("BgCanvas")),
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadControl,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
                Content = table
            }
        };
        Grid.SetRow(panel, 0);
        body.Children.Add(panel);

        var copy = UiKit.Make(L("Str_DocumentTools_Copy"), accent: false);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(report);
                copy.Content = L("Str_DocumentTools_Copied");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        var csv = UiKit.Make(L("Str_MeasureReport_SaveCsv"), accent: false);
        csv.Click += (_, _) => Save("csv", "CSV|*.csv", () => PdfMeasurementReport.ToCsv(_results));
        var json = UiKit.Make(L("Str_MeasureReport_SaveJson"), accent: false);
        json.Click += (_, _) => Save("json", "JSON|*.json", () => PdfMeasurementReport.ToJson(_results));
        bool any = results.Count > 0;
        copy.IsEnabled = csv.IsEnabled = json.IsEnabled = any;
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = UiKit.ButtonRow(copy, csv, json, close);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
    }

    // The count, then a header per page over its measurements: type, value, and scale.
    private static void FillTable(Grid table, IReadOnlyList<PdfMeasurementResult> results)
    {
        void Cell(string text, int column, int span = 1, string brush = "TextBrush",
            FontWeight? weight = null, Thickness? margin = null)
        {
            if (column == 0) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new TextBlock
            {
                Text = text,
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                FontWeight = weight ?? FontWeights.Normal,
                Foreground = UiKit.Brush(brush),
                Margin = margin ?? new Thickness(0, 2, column < 2 ? 28 : 0, 2)
            };
            Grid.SetRow(cell, table.RowDefinitions.Count - 1);
            Grid.SetColumn(cell, column);
            Grid.SetColumnSpan(cell, span);
            table.Children.Add(cell);
        }

        if (results.Count == 0)
        {
            Cell(L("Str_MeasureReport_None"), 0, 3, "MutedTextBrush");
            return;
        }
        Cell(string.Format(L("Str_MeasureReport_Count"), results.Count), 0, 3, "MutedTextBrush");
        foreach (var page in results.GroupBy(result => result.PageIndex))
        {
            Cell(string.Format(L("Str_MeasureReport_Page"), page.Key + 1), 0, 3,
                weight: FontWeights.SemiBold, margin: new Thickness(0, 10, 0, 3));
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
