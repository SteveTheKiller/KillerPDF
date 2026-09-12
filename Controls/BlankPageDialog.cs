using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class BlankPageDialog : Window
{
    private readonly double _currentWidth;
    private readonly double _currentHeight;
    private readonly ComboBox _preset;
    private readonly TextBox _width;
    private readonly TextBox _height;
    private bool _updating;

    internal double WidthPoints { get; private set; }
    internal double HeightPoints { get; private set; }

    internal BlankPageDialog(Window owner, double currentWidth, double currentHeight)
    {
        _currentWidth = currentWidth;
        _currentHeight = currentHeight;
        Title = "KillerPDF - " + L("Str_Ctx_AddBlankPage");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        _preset = new ComboBox
        {
            Height = 28,
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { L("Str_Blank_CurrentPage"), "Letter", "A4", "Legal", L("Str_Blank_Custom") },
            SelectedIndex = 0
        };
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _preset.Style = comboStyle;
        _preset.SelectionChanged += (_, _) => ApplyPreset();
        body.Children.Add(_preset);

        var dimensions = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        dimensions.ColumnDefinitions.Add(new ColumnDefinition());
        _width = AddDimension(dimensions, 0, L("Str_Sign_Width") + " (in)");
        _height = AddDimension(dimensions, 2, L("Str_Sign_Height") + " (in)");
        _width.TextChanged += DimensionChanged;
        _height.TextChanged += DimensionChanged;
        body.Children.Add(dimensions);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var add = UiKit.Make(L("Str_Ctx_AddBlankPage"), accent: true);
        add.IsDefault = true;
        add.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, add));

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        ApplyPreset();
        Loaded += (_, _) => _preset.Focus();
    }

    private static TextBox AddDimension(Grid host, int column, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.GroupLabel(label));
        var field = UiKit.Field();
        panel.Children.Add(field);
        Grid.SetColumn(panel, column);
        host.Children.Add(panel);
        return field;
    }

    private void ApplyPreset()
    {
        if (_updating) return;
        (double width, double height) = _preset.SelectedIndex switch
        {
            1 => (612, 792),
            2 => (595.276, 841.89),
            3 => (612, 1008),
            4 => (ReadPoints(_width) ?? _currentWidth, ReadPoints(_height) ?? _currentHeight),
            _ => (_currentWidth, _currentHeight)
        };
        _updating = true;
        _width.Text = (width / 72).ToString("0.###", CultureInfo.CurrentCulture);
        _height.Text = (height / 72).ToString("0.###", CultureInfo.CurrentCulture);
        _updating = false;
    }

    private void DimensionChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && _preset.SelectedIndex != 4) _preset.SelectedIndex = 4;
    }

    private static double? ReadPoints(TextBox field)
    {
        return double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double inches)
            && inches >= 0.1 && inches <= 200 ? inches * 72 : null;
    }

    private void Commit()
    {
        double? width = ReadPoints(_width);
        double? height = ReadPoints(_height);
        if (width is null || height is null)
        {
            KillerDialog.Show(this, L("Str_Blank_InvalidSize"), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        WidthPoints = width.Value;
        HeightPoints = height.Value;
        DialogResult = true;
        Close();
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
