using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json.Nodes;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;

namespace KillerPDF.Controls;

internal sealed class ColorManagementDialog : Window
{
    internal PdfDocumentProofSettings? AppliedSettings { get; private set; }

    internal ColorManagementDialog(Window owner, PdfDocumentProofSettings settings)
    {
        Title = "KillerPDF - " + L("Str_CM_Title");
        Width = 560;
        Height = 560;
        MinWidth = 420;
        MinHeight = 360;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        AutomationProperties.SetAutomationId(this, "ColorManagementDialog");
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: false);
        close.IsCancel = true;
        AutomationProperties.SetAutomationId(close, "ColorManagementClose");
        close.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_Tf_Apply"), accent: true);
        AutomationProperties.SetAutomationId(apply, "ColorManagementApply");
        var footer = UiKit.ButtonRow(close, apply);
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        var fields = new StackPanel();
        fields.Children.Add(Label("Str_CM_Hint", 12));
        var enabled = Toggle("Str_CM_Enable", "ProofEnabled", settings.Enabled);
        fields.Children.Add(enabled);
        fields.Children.Add(Label("Str_CM_Profile", 4));
        var profile = Choice("Str_CM_Profile", "ProofProfile", [L("Str_CM_NoProfile")], 0);
        fields.Children.Add(profile);
        fields.Children.Add(Label("Str_CM_Intent", 4));
        var intent = Choice("Str_CM_Intent", "ProofIntent",
            [L("Str_CM_Perceptual"), L("Str_CM_Relative"), L("Str_CM_Saturation"), L("Str_CM_Absolute")],
            settings.Options.RenderingIntent switch
            {
                PdfRenderingIntent.Perceptual => 0,
                PdfRenderingIntent.RelativeColorimetric => 1,
                PdfRenderingIntent.Saturation => 2,
                PdfRenderingIntent.AbsoluteColorimetric => 3,
                _ => throw new ArgumentOutOfRangeException(nameof(settings))
            });
        fields.Children.Add(intent);
        var bpc = Toggle("Str_CM_Bpc", "ProofBpc", settings.Options.BlackPointCompensation);
        var paper = Toggle("Str_CM_Paper", "ProofPaper", settings.Options.SimulatePaper);
        var black = Toggle("Str_CM_Black", "ProofBlack", settings.Options.SimulateBlack);
        var gamut = Toggle("Str_CM_Gamut", "ProofGamut", settings.GamutWarnings);
        foreach (var toggle in new[] { bpc, paper, black, gamut }) fields.Children.Add(toggle);
        apply.Click += (_, _) =>
        {
            var preset = JsonNode.Parse(PdfProofPreset.Capture("Document", settings).ToJson())!;
            var proof = preset["proof"]!;
            proof["enabled"] = enabled.IsChecked == true;
            proof["renderingIntent"] = new[] { "perceptual", "relativeColorimetric", "saturation", "absoluteColorimetric" }[intent.SelectedIndex];
            proof["blackPointCompensation"] = bpc.IsChecked == true;
            proof["simulatePaper"] = paper.IsChecked == true;
            proof["simulateBlack"] = black.IsChecked == true;
            proof["gamutWarnings"] = gamut.IsChecked == true;
            AppliedSettings = PdfProofPreset.FromJson(preset.ToJsonString()).ResolveProfile(settings.PrinterProfile);
            DialogResult = true;
        };
        body.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => fields.Children.OfType<CheckBox>().First().Focus();
    }

    private static CheckBox Toggle(string key, string id, bool value)
    {
        var control = UiKit.CheckBox(L(key));
        control.IsChecked = value;
        control.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, L(key));
        return control;
    }

    private static ComboBox Choice(string key, string id, string[] values, int selected)
    {
        var control = new ComboBox { ItemsSource = values, SelectedIndex = selected, Style = UiKit.DarkComboStyle,
            Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        control.SetResourceReference(FontFamilyProperty, "UiFont");
        AutomationProperties.SetAutomationId(control, id);
        AutomationProperties.SetName(control, L(key));
        return control;
    }

    private static TextBlock Label(string key, double bottom)
    {
        var label = new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, bottom) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        return label;
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
