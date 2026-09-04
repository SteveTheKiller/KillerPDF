using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class FlattenOptionsDialog : Window
{
    private readonly ComboBox _colorMode;
    private readonly TextBox _dpi;
    private readonly Slider _threshold;
    private readonly TextBlock _thresholdValue;
    private readonly CheckBox _useJpeg;
    private readonly Slider _jpegQuality;
    private readonly TextBlock _jpegQualityValue;

    internal bool Confirmed { get; private set; }
    internal PdfRasterize.FlattenOptions Options { get; private set; } = new();

    internal FlattenOptionsDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_Dlg_SaveFlattened");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        Style? comboStyle = owner.TryFindResource("DarkComboBox") as Style;
        Style? sliderStyle = owner.TryFindResource("DarkSlider") as Style;
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

        body.Children.Add(UiKit.GroupLabel(L("Str_Tf_ColorMode")));
        _colorMode = new ComboBox
        {
            ItemsSource = new[]
            {
                L("Str_Print_Color"), L("Str_Tf_Grayscale"), L("Str_Tf_BlackWhite")
            },
            SelectedIndex = 0,
            Margin = new Thickness(0, 3, 0, 10),
        };
        if (comboStyle is not null) _colorMode.Style = comboStyle;
        body.Children.Add(_colorMode);

        body.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Dpi")));
        _dpi = UiKit.Field();
        _dpi.Text = "150";
        _dpi.Margin = new Thickness(0, 3, 0, 10);
        body.Children.Add(_dpi);

        body.Children.Add(UiKit.GroupLabel(L("Str_Tf_Threshold")));
        _threshold = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = 160,
            IsEnabled = false,
            TickFrequency = 5,
            SmallChange = 1,
            LargeChange = 10,
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (sliderStyle is not null) _threshold.Style = sliderStyle;
        _thresholdValue = ValueLabel("160");
        _threshold.ValueChanged += (_, e) =>
            _thresholdValue.Text = Math.Round(e.NewValue).ToString();
        body.Children.Add(_threshold);
        body.Children.Add(_thresholdValue);

        _useJpeg = UiKit.CheckBox(L("Str_Tf_UseJpeg"));
        _useJpeg.Margin = new Thickness(0, 10, 0, 2);
        body.Children.Add(_useJpeg);
        _jpegQuality = new Slider
        {
            Minimum = 25,
            Maximum = 100,
            Value = 85,
            IsEnabled = false,
            TickFrequency = 5,
            SmallChange = 1,
            LargeChange = 5,
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (sliderStyle is not null) _jpegQuality.Style = sliderStyle;
        _jpegQualityValue = ValueLabel("85%");
        _jpegQuality.ValueChanged += (_, e) =>
            _jpegQualityValue.Text = $"{e.NewValue:0}%";
        body.Children.Add(_jpegQuality);
        body.Children.Add(_jpegQualityValue);

        _colorMode.SelectionChanged += (_, _) => UpdateEnabledControls();
        _useJpeg.Checked += (_, _) => UpdateEnabledControls();
        _useJpeg.Unchecked += (_, _) => UpdateEnabledControls();

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => _dpi.Focus();
    }

    private void UpdateEnabledControls()
    {
        bool bitonal = _colorMode.SelectedIndex == (int)PageColorMode.BlackAndWhite;
        _threshold.IsEnabled = bitonal;
        if (bitonal) _useJpeg.IsChecked = false;
        _useJpeg.IsEnabled = !bitonal;
        _jpegQuality.IsEnabled = !bitonal && _useJpeg.IsChecked == true;
    }

    private void Commit()
    {
        double dpi = double.TryParse(_dpi.Text.Trim(), out double parsed)
            && parsed >= 24 && parsed <= 1200 ? parsed : 150;
        Options = new PdfRasterize.FlattenOptions(
            dpi,
            (PageColorMode)Math.Max(0, _colorMode.SelectedIndex),
            (int)Math.Round(_threshold.Value),
            _useJpeg.IsChecked == true,
            (int)Math.Round(_jpegQuality.Value));
        Options.Validate();
        Confirmed = true;
        Close();
    }

    private static TextBlock ValueLabel(string text) => new()
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = UiKit.Brush("MutedTextBrush"),
        FontFamily = UiKit.MonoFont,
        FontSize = 10,
    };

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
