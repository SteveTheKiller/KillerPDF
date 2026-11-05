using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class NativePdfConversionDialog : Window
{
    private readonly ComboBox _paper;
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly TextBox _margin;
    private bool _updating;

    internal double WidthPoints { get; private set; }
    internal double HeightPoints { get; private set; }
    internal double MarginPoints { get; private set; }

    internal NativePdfConversionDialog(Window owner)
    {
        Title = L("Str_NativePdf_Convert");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(UiKit.GroupLabel(L("Str_Print_Paper")));
        _paper = new ComboBox
        {
            Name = "NativeConversionPaper", Height = 28, Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { "A4", "Letter", "Legal", L("Str_Blank_Custom") }, SelectedIndex = 0
        };
        _paper.SetResourceReference(FontFamilyProperty, "UiFont");
        AutomationProperties.SetName(_paper, L("Str_Print_Paper"));
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _paper.Style = comboStyle;
        body.Children.Add(_paper);
        var dimensions = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        _width = Dimension(dimensions, 0, "NativeConversionWidth", L("Str_Sign_Width") + " (mm)");
        _height = Dimension(dimensions, 2, "NativeConversionHeight", L("Str_Sign_Height") + " (mm)");
        body.Children.Add(dimensions);
        body.Children.Add(UiKit.GroupLabel(L("Str_Print_Margins") + " (mm)"));
        _margin = UiKit.Field();
        _margin.Name = "NativeConversionMargin";
        _margin.Text = "15";
        AutomationProperties.SetName(_margin, L("Str_Print_Margins") + " (mm)");
        body.Children.Add(_margin);
        var note = new TextBlock
        {
            Text = L("Str_NativePdf_KeepOriginal"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 12)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        note.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        body.Children.Add(note);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.Name = "NativeConversionCancel";
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var convert = UiKit.Make(L("Str_NativePdf_Convert"), accent: true);
        convert.Name = "NativeConversionStart";
        convert.IsDefault = true;
        convert.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, convert));
        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
        _paper.SelectionChanged += (_, _) => ApplyPreset();
        _width.TextChanged += DimensionChanged;
        _height.TextChanged += DimensionChanged;
        ApplyPreset();
        Loaded += (_, _) => _paper.Focus();
    }

    private static TextBox Dimension(Grid grid, int column, string name, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.GroupLabel(label));
        var field = UiKit.Field();
        field.Name = name;
        AutomationProperties.SetName(field, label);
        panel.Children.Add(field);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        return field;
    }

    private void ApplyPreset()
    {
        if (_updating || _paper.SelectedIndex == 3) return;
        (double width, double height) = _paper.SelectedIndex switch
        {
            1 => (215.9, 279.4),
            2 => (215.9, 355.6),
            _ => (210, 297)
        };
        _updating = true;
        _width.Text = width.ToString("0.###", CultureInfo.CurrentCulture);
        _height.Text = height.ToString("0.###", CultureInfo.CurrentCulture);
        _updating = false;
    }

    private void DimensionChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating) _paper.SelectedIndex = 3;
    }

    private void Commit()
    {
        if (!Read(_width, out double width) || !Read(_height, out double height)
            || !Read(_margin, out double margin) || width is < 25 or > 2000
            || height is < 25 or > 2000 || margin < 0 || margin * 2 >= Math.Min(width, height))
        {
            KillerDialog.Show(this, L("Str_NativePdf_InvalidLayout"), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        WidthPoints = width * 72 / 25.4;
        HeightPoints = height * 72 / 25.4;
        MarginPoints = margin * 72 / 25.4;
        DialogResult = true;
        Close();
    }

    private static bool Read(TextBox field, out double value) =>
        double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        && double.IsFinite(value);

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
