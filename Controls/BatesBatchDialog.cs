using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class BatesBatchDialog : Window
{
    private readonly List<TextBox> _inputs = [];
    private readonly ComboBox _position = new() { Style = UiKit.DarkComboStyle };
    private readonly CheckBox _overlap;
    internal PdfMacroStep? Step { get; private set; }
    internal PdfBatesMacroOptions? Options { get; private set; }
    internal BatesBatchDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_Production_Bates"); Width = 620; SizeToContent = SizeToContent.Height; MinWidth = 500;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
        UseLayoutRounding = true; DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_Tf_Apply"), accent: true);
        apply.IsDefault = true; apply.Click += (_, _) => Apply();
        // One footer row: the overlap choice on the left, Cancel and Apply on the right.
        _overlap = UiKit.CheckBox(L("Str_Production_AllowOverlap"));
        DockPanel buttons = DialogParts.Footer([cancel, apply], _overlap);
        DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        var fields = new StackPanel();
        var pairs = new Grid();
        pairs.ColumnDefinitions.Add(new ColumnDefinition());
        pairs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        pairs.ColumnDefinitions.Add(new ColumnDefinition());
        fields.Children.Add(pairs);
        foreach (var (key, value) in new[] { ("Str_Stamp_StartAt", "1"), ("Str_Production_Digits", "6"),
            ("Str_PageLabels_Prefix", ""), ("Str_Production_Suffix", ""), ("Str_Stamp_FontSize", "10"),
            ("Str_Print_Margins", "18") })
        {
            int row = _inputs.Count / 2 * 2, column = _inputs.Count % 2 * 2;
            if (column == 0)
            {
                pairs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                pairs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var label = UiKit.GroupLabel(L(key)); label.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(label, row); Grid.SetColumn(label, column); pairs.Children.Add(label);
            var input = UiKit.Field(); input.Text = value; input.Margin = new Thickness(0, 4, 0, 10);
            System.Windows.Automation.AutomationProperties.SetName(input, L(key));
            Grid.SetRow(input, row + 1); Grid.SetColumn(input, column);
            _inputs.Add(input); pairs.Children.Add(input);
        }
        fields.Children.Add(UiKit.GroupLabel(L("Str_Stamp_Position")));
        _position.ItemsSource = new[] { "Str_Pos_BottomRight", "Str_Pos_BottomCenter", "Str_Pos_BottomLeft",
            "Str_Pos_TopRight", "Str_Pos_TopCenter", "Str_Pos_TopLeft" }.Select(L).ToArray();
        _position.SelectedIndex = 0; _position.Margin = new Thickness(0, 5, 0, 0); fields.Children.Add(_position);
        body.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
    }

    private void Apply()
    {
        try
        {
            int position = _position.SelectedIndex;
            var alignments = new[] { PdfPageFurnitureAlignment.Right, PdfPageFurnitureAlignment.Center, PdfPageFurnitureAlignment.Left };
            double margin = double.Parse(_inputs[5].Text, CultureInfo.CurrentCulture);
            var options = new PdfBatesMacroOptions
            {
                StartNumber = long.Parse(_inputs[0].Text, CultureInfo.CurrentCulture),
                DigitCount = int.Parse(_inputs[1].Text, CultureInfo.CurrentCulture), Prefix = _inputs[2].Text, Suffix = _inputs[3].Text,
                FontSize = double.Parse(_inputs[4].Text, CultureInfo.CurrentCulture), HorizontalMargin = margin, VerticalMargin = margin,
                Edge = position < 3 ? PdfPageFurnitureEdge.Footer : PdfPageFurnitureEdge.Header,
                Alignment = alignments[position % 3], AllowCollisions = _overlap.IsChecked == true
            };
            Step = PdfPageFurnitureMacro.BatesBatchStep(options);
            Options = options;
            DialogResult = true;
        }
        catch (Exception error) { KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + error.Message); }
    }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
