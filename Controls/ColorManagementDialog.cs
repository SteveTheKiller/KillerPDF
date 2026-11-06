using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json.Nodes;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPDF.Services;

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
        var library = new ProofProfileLibrary();
        List<ProofProfileEntry> entries;
        bool libraryReadable = true;
        try { entries = library.GetProfiles().ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            entries = [];
            libraryReadable = false;
        }
        if (settings.PrinterProfileSha256 is { } requested && !entries.Any(entry => entry.ContentSha256 == requested))
            entries.Insert(0, new ProofProfileEntry(L("Str_CM_UnavailableProfile"), requested));
        int selected = entries.FindIndex(entry => entry.ContentSha256 == settings.PrinterProfileSha256) + 1;
        var profile = Choice("Str_CM_Profile", "ProofProfile",
            [L("Str_CM_NoProfile"), .. entries.Select(entry => entry.Name)], selected);
        profile.IsEnabled = libraryReadable;
        apply.IsEnabled = libraryReadable;
        fields.Children.Add(profile);
        var profileStatus = Label(libraryReadable ? "Str_CM_ProfileUnavailable" : "Str_CM_LibraryUnavailable", 4);
        AutomationProperties.SetAutomationId(profileStatus, "ProofProfileStatus");
        fields.Children.Add(profileStatus);
        bool importBusy = false;
        bool closed = false;
        Closed += (_, _) => closed = true;
        var import = UiKit.Make(L("Str_CM_ImportProfile"), accent: false);
        import.IsEnabled = libraryReadable;
        import.HorizontalAlignment = HorizontalAlignment.Left;
        import.Margin = new Thickness(0, 4, 0, 0);
        AutomationProperties.SetAutomationId(import, "ProofImportProfile");
        fields.Children.Add(import);
        var importStatus = Label("Str_CM_ImportFailed", 4);
        importStatus.Text = "";
        importStatus.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(importStatus, "ProofImportStatus");
        fields.Children.Add(importStatus);
        import.Click += async (_, _) =>
        {
            if (importBusy || !libraryReadable) return;
            var picker = new FileDialog(FileDialogMode.Open)
            {
                Title = L("Str_CM_ImportProfile"),
                Filter = L("Str_CM_ProfileFilter") + "|*.icc;*.icm",
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) != true) return;
            string path = picker.FileName;
            importBusy = true;
            import.IsEnabled = profile.IsEnabled = apply.IsEnabled = false;
            importStatus.Text = "";
            importStatus.Visibility = Visibility.Collapsed;
            try
            {
                var result = await Task.Run(() =>
                {
                    using var file = File.OpenRead(path);
                    if (file.Length < 128 || file.Length > 32L * 1024 * 1024)
                        throw new FormatException("The ICC profile size is unsupported.");
                    byte[] bytes = new byte[(int)file.Length];
                    file.ReadExactly(bytes);
                    if (file.ReadByte() != -1) throw new IOException("The ICC profile changed while it was read.");
                    var entry = library.Import(Path.GetFileNameWithoutExtension(path), bytes);
                    return (Entry: entry, Entries: library.GetProfiles().ToList());
                });
                if (closed) return;
                entries = result.Entries;
                if (settings.PrinterProfileSha256 is { } current && !entries.Any(entry => entry.ContentSha256 == current))
                    entries.Insert(0, new ProofProfileEntry(L("Str_CM_UnavailableProfile"), current));
                profile.ItemsSource = new[] { L("Str_CM_NoProfile") }.Concat(entries.Select(entry => entry.Name)).ToArray();
                profile.SelectedIndex = entries.FindIndex(entry => entry.ContentSha256 == result.Entry.ContentSha256) + 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                if (closed) return;
                importStatus.Text = L("Str_CM_ImportFailed");
                importStatus.Visibility = Visibility.Visible;
            }
            finally
            {
                importBusy = false;
                if (!closed)
                {
                    import.IsEnabled = profile.IsEnabled = apply.IsEnabled = libraryReadable;
                    UpdateProfileAvailability();
                }
            }
        };
        profile.SelectionChanged += (_, _) => UpdateProfileAvailability();
        enabled.Checked += (_, _) => UpdateProfileAvailability();
        enabled.Unchecked += (_, _) => UpdateProfileAvailability();
        UpdateProfileAvailability();

        void UpdateProfileAvailability()
        {
            if (importBusy || profile.SelectedIndex < 0) return;
            if (!libraryReadable)
            {
                profileStatus.Visibility = Visibility.Visible;
                return;
            }
            var preset = JsonNode.Parse(PdfProofPreset.Capture("Document", settings).ToJson())!;
            preset["proof"]!["printerProfileSha256"] = profile.SelectedIndex == 0 ? null : entries[profile.SelectedIndex - 1].ContentSha256;
            profileStatus.Visibility = enabled.IsChecked == true
                && library.Resolve(PdfProofPreset.FromJson(preset.ToJsonString())).Kind != ProofProfileResolutionKind.Matched
                ? Visibility.Visible : Visibility.Collapsed;
        }
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
            proof["printerProfileSha256"] = profile.SelectedIndex == 0 ? null : entries[profile.SelectedIndex - 1].ContentSha256;
            proof["renderingIntent"] = new[] { "perceptual", "relativeColorimetric", "saturation", "absoluteColorimetric" }[intent.SelectedIndex];
            proof["blackPointCompensation"] = bpc.IsChecked == true;
            proof["simulatePaper"] = paper.IsChecked == true;
            proof["simulateBlack"] = black.IsChecked == true;
            proof["gamutWarnings"] = gamut.IsChecked == true;
            AppliedSettings = library.Resolve(PdfProofPreset.FromJson(preset.ToJsonString())).Settings;
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
