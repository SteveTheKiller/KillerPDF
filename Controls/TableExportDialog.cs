using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class TableExportDialog : Window
{
    private readonly PdfDocument _document;
    private readonly int _pageIndex;
    private readonly int _rotation;
    private readonly BitmapSource _preview;
    private readonly Canvas _overlay;
    private readonly Rectangle _box = new() { StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Rectangle _cellBox = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBox[] _area = new TextBox[4];
    private readonly Grid _cells = new();
    private readonly List<(int Row, int Column, TextBox Input)> _editors = [];
    private readonly TextBlock _status;
    private readonly Button _detect;
    private readonly Button _export;
    private readonly ComboBox _format;
    private readonly StackPanel _selection;
    private PdfExtractedTable? _table;
    private CancellationTokenSource? _operation;
    private Point _start;
    private bool _settingArea;
    private bool _closed;

    internal PdfExtractedTable? Result { get; private set; }
    internal PdfTableExportFormat Format => _format.SelectedIndex == 1 ? PdfTableExportFormat.Csv : PdfTableExportFormat.Xlsx;

    internal TableExportDialog(Window owner, PdfDocument document, int pageIndex,
        BitmapSource preview, int rotation, double scale)
    {
        _document = document; _pageIndex = pageIndex; _preview = preview; _rotation = rotation;
        Title = "KillerPDF - " + L("Str_TableExport_Title");
        Width = Math.Min(620 * Math.Clamp(scale, 0.7, 2.5), SystemParameters.WorkArea.Width - 40);
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        SizeToContent = SizeToContent.Height;
        MinWidth = 480; MinHeight = 360; UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        AutomationProperties.SetAutomationId(this, "TableExportDialog");

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16),
            LayoutTransform = new ScaleTransform(Math.Clamp(scale, 0.7, 2.5), Math.Clamp(scale, 0.7, 2.5)) };
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _format = new ComboBox { ItemsSource = new[] { "XLSX", "CSV" }, SelectedIndex = 0,
            Style = UiKit.DarkComboStyle, MinWidth = 85, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_format, L("Str_ExportImg_Format"));
        AutomationProperties.SetAutomationId(_format, "TableExportFormat");
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), false);
        cancel.Height = 30; cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        AutomationProperties.SetAutomationId(cancel, "TableExportCancel");
        _export = UiKit.Make(L("Str_ExportImg_Export"), true);
        _export.Height = 30;
        _export.IsEnabled = false; _export.Click += (_, _) => Commit();
        AutomationProperties.SetAutomationId(_export, "TableExportSave");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_format); actions.Children.Add(cancel); actions.Children.Add(_export);
        footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);

        var content = new StackPanel();
        _selection = new StackPanel();
        _selection.Children.Add(Note("Str_TableExport_Hint"));
        var page = new Grid { Width = preview.PixelWidth, Height = preview.PixelHeight, Margin = new Thickness(0, 6, 0, 8) };
        page.Children.Add(new Image { Source = preview, Stretch = Stretch.Fill });
        _overlay = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.Cross };
        _box.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
        _cellBox.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
        _overlay.Children.Add(_box); _overlay.Children.Add(_cellBox); page.Children.Add(_overlay);
        var pageView = new Viewbox { Child = page, Stretch = Stretch.Uniform, MaxHeight = 240,
            HorizontalAlignment = HorizontalAlignment.Left };
        _selection.Children.Add(pageView);
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            _start = Clamp(e.GetPosition(_overlay)); _overlay.CaptureMouse();
            InvalidateTable(); Draw(new Rect(_start, _start)); e.Handled = true;
        };
        _overlay.MouseMove += (_, e) => { if (_overlay.IsMouseCaptured) Draw(new Rect(_start, Clamp(e.GetPosition(_overlay)))); };
        _overlay.MouseLeftButtonUp += (_, e) =>
        {
            if (!_overlay.IsMouseCaptured) return;
            Rect rect = new(_start, Clamp(e.GetPosition(_overlay)));
            _overlay.ReleaseMouseCapture(); SetArea(rect); e.Handled = true;
        };

        _selection.Children.Add(UiKit.GroupLabel(L("Str_TableExport_Area")));
        var fields = new WrapPanel();
        string[] labels = ["Str_Pos_Left", "Str_Pos_Top", "Str_Sign_Width", "Str_Sign_Height"];
        string[] ids = ["TableAreaLeft", "TableAreaTop", "TableAreaWidth", "TableAreaHeight"];
        for (int i = 0; i < 4; i++)
        {
            var field = new StackPanel { Width = 78, Margin = new Thickness(0, 4, 8, 6) };
            field.Children.Add(UiKit.GroupLabel(L(labels[i])));
            TextBox input = UiKit.Field(); _area[i] = input;
            input.Text = i < 2 ? "0" : "100";
            AutomationProperties.SetName(input, L(labels[i]) + " (%)");
            AutomationProperties.SetAutomationId(input, ids[i]);
            input.TextChanged += (_, _) =>
            {
                if (_settingArea) return;
                InvalidateTable();
                if (TryRegion(out Rect region)) Draw(region);
                else _box.Visibility = Visibility.Collapsed;
            };
            field.Children.Add(input); fields.Children.Add(field);
        }
        _selection.Children.Add(fields);
        _detect = UiKit.Make(L("Str_TableExport_Detect"), false);
        _detect.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(_detect, "TableDetect");
        _detect.Click += async (_, _) => await Detect();
        _selection.Children.Add(_detect); content.Children.Add(_selection);
        _status = Note(""); _status.Margin = new Thickness(0, 8, 0, 6);
        AutomationProperties.SetAutomationId(_status, "TableExportStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        content.Children.Add(_status);
        content.Children.Add(Note("Str_TableExport_Review"));
        content.Children.Add(new ScrollViewer { Content = _cells, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Draw(new Rect(0, 0, preview.PixelWidth, preview.PixelHeight));
        Loaded += (_, _) => _detect.Focus();
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
    }

    private void InvalidateTable()
    {
        _table = null; _export.IsEnabled = false; _editors.Clear(); _cells.Children.Clear();
        _cells.RowDefinitions.Clear(); _cells.ColumnDefinitions.Clear();
        _cellBox.Visibility = Visibility.Collapsed;
        if (_status is not null) _status.Text = "";
    }

    private bool TryRegion(out Rect region)
    {
        region = Rect.Empty;
        var values = new double[4];
        for (int i = 0; i < values.Length; i++)
            if (_area[i] is null || !double.TryParse(_area[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i])
                || !double.IsFinite(values[i]) || values[i] < 0 || values[i] > 100) return false;
        if (values[2] <= 0 || values[3] <= 0 || values[0] + values[2] > 100.001 || values[1] + values[3] > 100.001) return false;
        region = new Rect(values[0] * _preview.PixelWidth / 100, values[1] * _preview.PixelHeight / 100,
            values[2] * _preview.PixelWidth / 100, values[3] * _preview.PixelHeight / 100);
        return true;
    }

    private void SetArea(Rect region)
    {
        _settingArea = true;
        double[] values = [region.Left / _preview.PixelWidth, region.Top / _preview.PixelHeight,
            region.Width / _preview.PixelWidth, region.Height / _preview.PixelHeight];
        for (int i = 0; i < 4; i++) _area[i].Text = (values[i] * 100).ToString("0.###", CultureInfo.CurrentCulture);
        _settingArea = false; InvalidateTable();
        if (TryRegion(out Rect valid)) Draw(valid); else _box.Visibility = Visibility.Collapsed;
    }

    private async Task Detect()
    {
        if (_operation is not null) return;
        InvalidateTable();
        if (!TryRegion(out Rect region)) { _status.Text = L("Str_TableExport_InvalidArea"); return; }
        Draw(region);
        var page = PdfPageInformation.Read(_document)[_pageIndex];
        var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(region, page.Width, page.Height,
            _preview.PixelWidth, _preview.PixelHeight, _rotation);
        var bounds = new PdfContentBounds(Math.Clamp(left, 0, page.Width), Math.Clamp(bottom, 0, page.Height),
            Math.Clamp(right, 0, page.Width), Math.Clamp(top, 0, page.Height));
        _operation = new CancellationTokenSource();
        CancellationToken token = _operation.Token;
        _selection.IsEnabled = false; _status.Text = L("Str_Busy_Extracting");
        try
        {
            PdfTableExtractionResult result = await Task.Run(() => PdfTableExtractor.Extract(_document, _pageIndex, bounds, token), token);
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (!result.Succeeded) { _status.Text = L("Str_TableExport_Unsupported"); return; }
            _table = result.Table!; ShowTable(_table);
            _status.Text = string.Format(L("Str_TableExport_Summary"), _table.RowCount, _table.ColumnCount);
            _export.IsEnabled = true;
            _editors.FirstOrDefault().Input?.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_closed) _status.Text = L("Str_Err_ExportFailed") + "\n" + error.Message;
        }
        finally
        {
            _operation.Dispose(); _operation = null;
            if (!_closed) _selection.IsEnabled = true;
        }
    }

    private void ShowTable(PdfExtractedTable table)
    {
        for (int column = 0; column <= table.ColumnCount; column++)
            _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = column == 0 ? GridLength.Auto : new GridLength(150) });
        for (int row = 0; row <= table.RowCount; row++) _cells.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < table.ColumnCount; column++) AddCell(UiKit.GroupLabel((column + 1).ToString()), 0, column + 1);
        for (int row = 0; row < table.RowCount; row++)
        {
            AddCell(UiKit.GroupLabel((row + 1).ToString()), row + 1, 0);
            for (int column = 0; column < table.ColumnCount; column++)
            {
                var cell = table.GetCell(row, column);
                TextBox input = UiKit.Field(); input.Text = cell.Text; input.AcceptsReturn = true;
                input.TextWrapping = TextWrapping.Wrap; input.MinHeight = 30; input.MaxLength = 32767;
                AutomationProperties.SetName(input, string.Format(L("Str_TableExport_Cell"), row + 1, column + 1));
                AutomationProperties.SetAutomationId(input, $"TableCell_{row}_{column}");
                input.GotKeyboardFocus += (_, _) => Highlight(cell.Bounds);
                AddCell(input, row + 1, column + 1); _editors.Add((row, column, input));
            }
        }
    }

    private void AddCell(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(2); Grid.SetRow(element, row); Grid.SetColumn(element, column); _cells.Children.Add(element);
    }

    private void Highlight(PdfContentBounds bounds)
    {
        var page = PdfPageInformation.Read(_document)[_pageIndex];
        Rect box = RedactionGeometry.PageToCanvas(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top,
            page.Width, page.Height, _preview.PixelWidth, _preview.PixelHeight, _rotation);
        SetBox(_cellBox, box);
    }

    private void Commit()
    {
        if (_table is null || _operation is not null) return;
        PdfExtractedTable result = _table;
        foreach (var editor in _editors)
            if (result.GetCell(editor.Row, editor.Column).Text != editor.Input.Text)
                result = result.WithCellText(editor.Row, editor.Column, editor.Input.Text);
        Result = result; Close();
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _preview.PixelWidth), Math.Clamp(point.Y, 0, _preview.PixelHeight));
    private void Draw(Rect region) => SetBox(_box, region);
    private static void SetBox(Rectangle box, Rect region)
    {
        Canvas.SetLeft(box, region.Left); Canvas.SetTop(box, region.Top);
        box.Width = region.Width; box.Height = region.Height; box.Visibility = Visibility.Visible;
    }
    private static TextBlock Note(string key)
    {
        var note = new TextBlock { Text = key.Length == 0 ? "" : L(key), TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont, FontSize = 12 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return note;
    }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
