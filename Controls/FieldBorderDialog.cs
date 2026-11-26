using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPdf.Engine.Authoring;

namespace KillerPDF;

// A text field's border: its color, width and style, with a preview on the field's own fill. Opened from
// the Border tile on a text field's right-click card.
internal sealed class FieldBorderDialog : Window
{
    private static readonly double[] Widths = [0, 0.5, 1, 1.5, 2, 3, 4];
    private static readonly (PdfFormFieldBorderStyle Style, string Key)[] Styles =
    [
        (PdfFormFieldBorderStyle.Solid, "Str_FieldBorder_Solid"),
        (PdfFormFieldBorderStyle.Dashed, "Str_FieldBorder_Dashed"),
        (PdfFormFieldBorderStyle.Beveled, "Str_FieldBorder_Beveled"),
        (PdfFormFieldBorderStyle.Inset, "Str_FieldBorder_Inset"),
        (PdfFormFieldBorderStyle.Underline, "Str_FieldBorder_Underline")
    ];

    private readonly Border _swatch;
    private readonly Button _swatchButton;
    private readonly ComboBox _width;
    private readonly ComboBox _style;
    private readonly Grid _preview;
    private readonly Color _fill;

    /// <summary>The chosen border color.</summary>
    internal Color BorderColor { get; private set; }
    /// <summary>The chosen width in points; zero means no border.</summary>
    internal double BorderWidth => _width.SelectedItem is WidthChoice choice ? choice.Points : 1;
    /// <summary>The chosen border style.</summary>
    internal PdfFormFieldBorderStyle BorderStyle =>
        _style.SelectedItem is StyleChoice choice ? choice.Style : PdfFormFieldBorderStyle.Solid;

    internal FieldBorderDialog(Window owner, Color? color, double width, PdfFormFieldBorderStyle style, Color fill)
    {
        // A field without a border of its own starts from a 1-point black solid line.
        bool hasBorder = color.HasValue && width > 0;
        BorderColor = color ?? Colors.Black;
        _fill = fill;
        Title = "KillerPDF - " + L("Str_FieldBorder_Title");
        Width = 320;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        _swatch = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = UiKit.RadControl,
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(BorderColor),
            SnapsToDevicePixels = true
        };
        _swatchButton = new Button
        {
            Content = _swatch,
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        System.Windows.Automation.AutomationProperties.SetName(_swatchButton, L("Str_FieldBorder_Color"));
        _swatchButton.Click += (_, _) =>
        {
            var picker = new ColorPickerDialog(this, BorderColor);
            picker.ShowDialog();
            // A picker opened from a modal dialog can report false even on OK; on Cancel the
            // selected color is still the one it opened with.
            BorderColor = picker.SelectedColor;
            _swatch.Background = new SolidColorBrush(BorderColor);
            UpdatePreview();
        };
        // Color and width share a row: the swatch on the left, a short width list beside it.
        _width = new ComboBox { Width = 120, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _width.Style = comboStyle;
        double startWidth = hasBorder ? width : 1;
        List<double> widths = [.. Widths];
        if (!widths.Contains(startWidth)) { widths.Add(startWidth); widths.Sort(); }
        foreach (double points in widths)
            _width.Items.Add(new WidthChoice(points == 0 ? L("Str_FieldBorder_None")
                : string.Format(L("Str_FieldBorder_Points"), points.ToString("0.##", CultureInfo.CurrentCulture)), points));
        _width.SelectedIndex = widths.IndexOf(startWidth);
        _width.SelectionChanged += (_, _) => UpdatePreview();
        System.Windows.Automation.AutomationProperties.SetName(_width, L("Str_FieldBorder_Width"));
        var colorAndWidth = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        colorAndWidth.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        colorAndWidth.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        colorAndWidth.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        colorAndWidth.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var colorLabel = UiKit.GroupLabel(L("Str_FieldBorder_Color"));
        var widthLabel = UiKit.GroupLabel(L("Str_FieldBorder_Width"));
        Grid.SetColumn(widthLabel, 1);
        Grid.SetRow(_swatchButton, 1);
        Grid.SetRow(_width, 1);
        Grid.SetColumn(_width, 1);
        widthLabel.Margin = new Thickness(widthLabel.Margin.Left + 24, widthLabel.Margin.Top, widthLabel.Margin.Right, widthLabel.Margin.Bottom);
        _width.Margin = new Thickness(24, 3, 0, 0);
        colorAndWidth.Children.Add(colorLabel);
        colorAndWidth.Children.Add(widthLabel);
        colorAndWidth.Children.Add(_swatchButton);
        colorAndWidth.Children.Add(_width);
        body.Children.Add(colorAndWidth);

        body.Children.Add(UiKit.GroupLabel(L("Str_FieldBorder_Style")));
        _style = new ComboBox { Margin = new Thickness(0, 3, 0, 12) };
        if (owner.TryFindResource("DarkComboBox") is Style styleComboStyle) _style.Style = styleComboStyle;
        foreach ((PdfFormFieldBorderStyle option, string key) in Styles)
            _style.Items.Add(new StyleChoice(L(key), option));
        _style.SelectedIndex = hasBorder ? Array.FindIndex(Styles, entry => entry.Style == style) : 0;
        if (_style.SelectedIndex < 0) _style.SelectedIndex = 0;
        _style.SelectionChanged += (_, _) => UpdatePreview();
        System.Windows.Automation.AutomationProperties.SetName(_style, L("Str_FieldBorder_Style"));
        body.Children.Add(_style);

        body.Children.Add(UiKit.GroupLabel(L("Str_FieldBorder_Preview")));
        _preview = new Grid { Height = 30, Margin = new Thickness(0, 3, 0, 14) };
        body.Children.Add(_preview);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = UiKit.Make(L("Str_Btn_OK"), true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        body.Children.Add(UiKit.ButtonRow(cancel, ok));

        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
        UpdatePreview();
    }

    // The field's fill with the chosen border drawn the way the saved field draws it: a full outline, a
    // dashed outline, or a line along the bottom. Beveled and inset add their light and dark inner edges.
    private void UpdatePreview()
    {
        double points = BorderWidth;
        bool none = points <= 0;
        _swatchButton.IsEnabled = !none;
        _style.IsEnabled = !none;
        _swatch.Opacity = none ? 0.4 : 1;
        _preview.Children.Clear();
        _preview.Children.Add(new Border
        {
            Background = new SolidColorBrush(_fill),
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(none ? 1 : 0)
        });
        if (none) return;
        double px = Math.Max(1, points * 96 / 72);
        var stroke = new SolidColorBrush(BorderColor);
        switch (BorderStyle)
        {
            case PdfFormFieldBorderStyle.Underline:
                _preview.Children.Add(new Border { BorderBrush = stroke, BorderThickness = new Thickness(0, 0, 0, px) });
                break;
            case PdfFormFieldBorderStyle.Dashed:
                _preview.Children.Add(new Rectangle
                {
                    Stroke = stroke,
                    StrokeThickness = px,
                    StrokeDashArray = [3 * 96 / 72 / px],
                    SnapsToDevicePixels = true
                });
                break;
            default:
                _preview.Children.Add(new Border { BorderBrush = stroke, BorderThickness = new Thickness(px) });
                if (BorderStyle is PdfFormFieldBorderStyle.Beveled or PdfFormFieldBorderStyle.Inset)
                {
                    bool beveled = BorderStyle == PdfFormFieldBorderStyle.Beveled;
                    Color light = beveled ? Colors.White : Color.FromRgb(0x80, 0x80, 0x80);
                    Color dark = beveled ? Color.FromRgb(0x80, 0x80, 0x80) : Color.FromRgb(0xBF, 0xBF, 0xBF);
                    var inner = new Thickness(px);
                    _preview.Children.Add(new Border
                    {
                        Margin = inner,
                        BorderBrush = new SolidColorBrush(light),
                        BorderThickness = new Thickness(px, px, 0, 0)
                    });
                    _preview.Children.Add(new Border
                    {
                        Margin = inner,
                        BorderBrush = new SolidColorBrush(dark),
                        BorderThickness = new Thickness(0, 0, px, px)
                    });
                }
                break;
        }
    }

    private sealed record WidthChoice(string Label, double Points)
    {
        public override string ToString() => Label;
    }

    private sealed record StyleChoice(string Label, PdfFormFieldBorderStyle Style)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
