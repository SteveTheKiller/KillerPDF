using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

// Redaction properties: the fill color and the overlay text, typed or picked from a redaction
// code, that are drawn into each area when the redaction is applied.
internal sealed class RedactionPropertiesDialog : Window
{
    // U.S. FOIA exemptions and Privacy Act exemptions, the code sets reviewers most often cite.
    private static readonly string[] FoiaCodes =
    [
        "(b)(1)", "(b)(2)", "(b)(3)", "(b)(4)", "(b)(5)", "(b)(6)", "(b)(7)(A)", "(b)(7)(B)", "(b)(7)(C)",
        "(b)(7)(D)", "(b)(7)(E)", "(b)(7)(F)", "(b)(8)", "(b)(9)"
    ];
    private static readonly string[] PrivacyActCodes =
        ["(d)(5)", "(j)(1)", "(j)(2)", "(k)(1)", "(k)(2)", "(k)(3)", "(k)(4)", "(k)(5)", "(k)(6)", "(k)(7)"];

    private readonly Border _swatch;
    private readonly ComboBox _code;
    private readonly TextBox _overlay;
    private readonly TextBlock _problem;
    private readonly Border _preview;
    private readonly TextBlock _previewText;
    private readonly CheckBox? _applyExisting;
    private readonly Button _ok;
    private bool _syncing;

    /// <summary>The chosen fill color.</summary>
    internal Color Fill { get; private set; }
    /// <summary>The chosen overlay text, or null for none.</summary>
    internal string? OverlayText { get; private set; }
    /// <summary>True when the marks already drawn take the new properties too.</summary>
    internal bool ApplyToExisting { get; private set; }

    internal RedactionPropertiesDialog(Window owner, Color fill, string? overlayText, int existingMarks)
    {
        Fill = fill;
        Title = "KillerPDF - " + L("Str_RedactProps_Title");
        // Sized to its content: a swatch, a code list, one line of overlay text and its preview
        // (Steve, 2026-11-17).
        Width = 340;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(UiKit.GroupLabel(L("Str_RedactProps_Fill")));
        _swatch = new Border
        {
            Width = 44,
            Height = 22,
            CornerRadius = UiKit.RadControl,
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(fill),
            SnapsToDevicePixels = true
        };
        var swatchButton = new Button
        {
            Content = _swatch,
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 12),
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        System.Windows.Automation.AutomationProperties.SetName(swatchButton, L("Str_RedactProps_Fill"));
        swatchButton.Click += (_, _) =>
        {
            var picker = new ColorPickerDialog(this, Fill);
            picker.ShowDialog();
            // A picker opened from a modal dialog can report false even on OK; on Cancel the
            // selected color is still the one it opened with.
            Fill = picker.SelectedColor;
            _swatch.Background = new SolidColorBrush(Fill);
            UpdatePreview();
        };
        body.Children.Add(swatchButton);

        body.Children.Add(UiKit.GroupLabel(L("Str_RedactProps_Code")));
        _code = new ComboBox { Margin = new Thickness(0, 3, 0, 12) };
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _code.Style = comboStyle;
        _code.Items.Add(new CodeChoice(L("Str_RedactProps_NoCode"), null));
        foreach (string code in FoiaCodes) _code.Items.Add(new CodeChoice("FOIA " + code, code));
        foreach (string code in PrivacyActCodes) _code.Items.Add(new CodeChoice("Privacy Act " + code, code));
        _code.SelectedIndex = 0;
        _code.SelectionChanged += (_, _) =>
        {
            if (_syncing || _code.SelectedItem is not CodeChoice { Code: string code }) return;
            _syncing = true;
            _overlay!.Text = code;
            _syncing = false;
            UpdatePreview();
        };
        body.Children.Add(_code);

        body.Children.Add(UiKit.GroupLabel(L("Str_RedactProps_Overlay")));
        _overlay = UiKit.Field();
        _overlay.Margin = new Thickness(0, 3, 0, 4);
        _overlay.Text = overlayText ?? string.Empty;
        _overlay.TextChanged += (_, _) =>
        {
            if (!_syncing) SelectCode();
            UpdatePreview();
        };
        body.Children.Add(_overlay);
        _problem = new TextBlock
        {
            Text = L("Str_RedactProps_Unsupported"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 4)
        };
        body.Children.Add(_problem);

        body.Children.Add(new FrameworkElement { Height = 8 });
        body.Children.Add(UiKit.GroupLabel(L("Str_RedactProps_Preview")));
        _previewText = new TextBlock
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _preview = new Border
        {
            Height = 30,
            Margin = new Thickness(0, 3, 0, 12),
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            Child = _previewText
        };
        body.Children.Add(_preview);

        if (existingMarks > 0)
        {
            _applyExisting = UiKit.CheckBox(string.Format(L("Str_RedactProps_ApplyExisting"), existingMarks));
            _applyExisting.IsChecked = true;
            _applyExisting.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(_applyExisting);
        }

        body.Children.Add(new TextBlock
        {
            Text = L("Str_RedactProps_Hint"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _ok = UiKit.Make(L("Str_Btn_OK"), true);
        _ok.IsDefault = true;
        _ok.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, _ok));

        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
        SelectCode();
        UpdatePreview();
        Loaded += (_, _) => _overlay.Focus();
    }

    // The code list follows the typed text, so a typed code shows as picked and other text as none.
    private void SelectCode()
    {
        string text = _overlay.Text.Trim();
        int index = 0;
        for (int item = 1; item < _code.Items.Count; item++)
            if (_code.Items[item] is CodeChoice { Code: string code } && code == text) { index = item; break; }
        _syncing = true;
        _code.SelectedIndex = index;
        _syncing = false;
    }

    private void UpdatePreview()
    {
        string text = _overlay.Text.Trim();
        bool supported = text.Length == 0 || PdfContentRedaction.SupportsOverlayText(text);
        _problem.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        _ok.IsEnabled = supported;
        _preview.Background = new SolidColorBrush(Fill);
        double luminance = (0.2126 * Fill.R + 0.7152 * Fill.G + 0.0722 * Fill.B) / 255;
        _previewText.Foreground = luminance < 0.5 ? Brushes.White : Brushes.Black;
        _previewText.Text = text;
    }

    private void Commit()
    {
        string text = _overlay.Text.Trim();
        if (text.Length > 0 && !PdfContentRedaction.SupportsOverlayText(text)) return;
        OverlayText = text.Length == 0 ? null : text;
        ApplyToExisting = _applyExisting?.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private sealed record CodeChoice(string Label, string? Code)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
