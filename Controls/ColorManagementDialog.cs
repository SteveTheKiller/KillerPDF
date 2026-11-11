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
    /// <summary>Gets whether Apply saved a different working CMYK profile choice.</summary>
    internal bool WorkingCmykChanged { get; private set; }

    internal ColorManagementDialog(Window owner, PdfDocumentProofSettings settings, string? preferenceNoticeKey = null,
        IReadOnlyList<DocumentProofReview>? previousProof = null)
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
        apply.IsDefault = true;
        var footer = UiKit.ButtonRow(close, apply);
        footer.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        var fields = new StackPanel();
        if (preferenceNoticeKey is not null)
        {
            var notice = Label(preferenceNoticeKey, 4);
            AutomationProperties.SetAutomationId(notice, "ProofPreferencesStatus");
            fields.Children.Add(notice);
        }
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
        // Apply stays available with an unreadable library, so proofing can still be turned off.
        apply.IsEnabled = true;
        fields.Children.Add(profile);
        var profileStatus = Label(libraryReadable ? "Str_CM_ProfileUnavailable" : "Str_CM_LibraryUnavailable", 4);
        AutomationProperties.SetAutomationId(profileStatus, "ProofProfileStatus");
        fields.Children.Add(profileStatus);
        bool importBusy = false;
        bool closed = false;
        var resolvedKinds = new Dictionary<string, ProofProfileResolutionKind>(StringComparer.Ordinal);
        // Profiles being resolved off the UI thread. Apply waits for them, so it never reads a half answer.
        int resolving = 0;
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
        // The working CMYK profile is an application setting kept with the profile library.
        fields.Children.Add(Label("Str_CM_WorkingCmyk", 4));
        List<ProofProfileEntry> workingEntries = [];
        string? savedWorking = null;
        var workingCmyk = Choice("Str_CM_WorkingCmyk", "ProofWorkingCmyk", [L("Str_CM_WorkingCmykBuiltIn")], 0);
        fields.Children.Add(workingCmyk);
        fields.Children.Add(Label("Str_CM_WorkingCmykHint", 4));
        RefreshWorkingCmyk();

        void RefreshWorkingCmyk()
        {
            string? chosen = workingCmyk.SelectedIndex > 0 && workingCmyk.SelectedIndex <= workingEntries.Count
                ? workingEntries[workingCmyk.SelectedIndex - 1].ContentSha256 : null;
            try
            {
                workingEntries = libraryReadable ? library.GetCmykProfiles().ToList() : [];
                savedWorking = libraryReadable ? library.GetWorkingCmykProfileSha256() : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or FormatException or NotSupportedException)
            {
                workingEntries = [];
                savedWorking = null;
            }
            chosen ??= savedWorking;
            workingCmyk.ItemsSource = new[] { L("Str_CM_WorkingCmykBuiltIn") }
                .Concat(workingEntries.Select(entry => entry.Name)).ToArray();
            workingCmyk.SelectedIndex = workingEntries.FindIndex(entry => entry.ContentSha256 == chosen) + 1;
            workingCmyk.IsEnabled = libraryReadable && !importBusy;
        }
        var presetLibrary = new ProofPresetLibrary();
        List<PdfProofPreset> presets;
        bool presetsReadable = true;
        try { presets = presetLibrary.GetPresets().ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            presets = [];
            presetsReadable = false;
        }
        var presetChoice = Choice("Str_CM_Preset", "ProofPreset", [L("Str_CM_NoPreset"), .. presets.Select(item => item.Name)], 0);
        var presetName = UiKit.Field();
        presetName.MaxLength = 128;
        presetName.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetAutomationId(presetName, "ProofPresetName");
        AutomationProperties.SetName(presetName, L("Str_CM_PresetName"));
        var loadPreset = UiKit.Make(L("Str_CM_LoadPreset"), accent: false);
        var savePreset = UiKit.Make(L("Str_CM_SavePreset"), accent: false);
        AutomationProperties.SetAutomationId(loadPreset, "ProofLoadPreset");
        AutomationProperties.SetAutomationId(savePreset, "ProofSavePreset");
        var presetStatus = Label("Str_CM_PresetLibraryUnavailable", 4);
        presetStatus.Visibility = presetsReadable ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetAutomationId(presetStatus, "ProofPresetStatus");
        ComboBox? previousChoice = null;
        Button? reviewPrevious = null;
        SetOperationBusy(false);
        presetChoice.SelectionChanged += (_, _) => loadPreset.IsEnabled = !importBusy && presetsReadable && presetChoice.SelectedIndex > 0;
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
            string? priorReference = profile.SelectedIndex > 0 ? entries[profile.SelectedIndex - 1].ContentSha256 : null;
            SetOperationBusy(true);
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
                resolvedKinds.Clear();
                RefreshWorkingCmyk();
                if (priorReference is { } current && !entries.Any(entry => entry.ContentSha256 == current))
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
                if (!closed)
                {
                    SetOperationBusy(false);
                    UpdateProfileAvailability();
                }
            }
        };
        profile.SelectionChanged += (_, _) => UpdateProfileAvailability();
        enabled.Checked += (_, _) => UpdateProfileAvailability();
        enabled.Unchecked += (_, _) => UpdateProfileAvailability();
        UpdateProfileAvailability();

        async void UpdateProfileAvailability()
        {
            if (importBusy || profile.SelectedIndex < 0) return;
            if (!libraryReadable)
            {
                profileStatus.Visibility = Visibility.Visible;
                return;
            }
            if (enabled.IsChecked != true)
            {
                profileStatus.Visibility = Visibility.Collapsed;
                return;
            }
            // Resolve reads and parses the owned ICC copy, so each selection is resolved once.
            string? reference = profile.SelectedIndex == 0 ? null : entries[profile.SelectedIndex - 1].ContentSha256;
            ProofProfileResolutionKind kind = ProofProfileResolutionKind.NotSelected;
            if (reference is not null && !resolvedKinds.TryGetValue(reference, out kind))
            {
                // Reading and parsing the ICC copy can take a while for a large profile, so it runs off
                // the UI thread. A newer selection while it runs wins; this answer is still cached.
                resolving++;
                apply.IsEnabled = false;
                try
                {
                    kind = await Task.Run(() =>
                    {
                        try
                        {
                            var preset = JsonNode.Parse(PdfProofPreset.Capture("Document", settings).ToJson())!;
                            preset["proof"]!["printerProfileSha256"] = reference;
                            return library.Resolve(PdfProofPreset.FromJson(preset.ToJsonString())).Kind;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                            or FormatException or NotSupportedException or ArgumentException)
                        {
                            return ProofProfileResolutionKind.Invalid;
                        }
                    });
                }
                finally
                {
                    resolving--;
                    if (!closed) apply.IsEnabled = !importBusy && resolving == 0;
                }
                if (closed) return;
                resolvedKinds[reference] = kind;
                if (importBusy || profile.SelectedIndex < 0
                    || (profile.SelectedIndex == 0 ? null : entries[profile.SelectedIndex - 1].ContentSha256) != reference) return;
            }
            profileStatus.Visibility = kind != ProofProfileResolutionKind.Matched ? Visibility.Visible : Visibility.Collapsed;
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
        if (previousProof is { Count: > 0 })
        {
            var previousFields = new StackPanel();
            fields.Children.Insert(0, previousFields);
            previousFields.Children.Add(Label("Str_CM_PreviousFileHint", 4));
            previousFields.Children.Add(Label("Str_CM_PreviousFileSettings", 4));
            previousChoice = Choice("Str_CM_PreviousFileSettings", "ProofPreviousSettings",
                Enumerable.Range(1, previousProof.Count).Select(index => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture, L("Str_CM_PreviousFileSettingsItem"), index)).ToArray(), 0);
            previousFields.Children.Add(previousChoice);
            reviewPrevious = UiKit.Make(L("Str_CM_ReviewPrevious"), accent: false);
            reviewPrevious.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(reviewPrevious, "ProofReviewPrevious");
            AutomationProperties.SetName(reviewPrevious, L("Str_CM_ReviewPrevious"));
            previousFields.Children.Add(reviewPrevious);
            reviewPrevious.Click += (_, _) =>
            {
                if (importBusy || previousChoice.SelectedIndex < 0) return;
                var chosen = previousProof[previousChoice.SelectedIndex].Preset;
                if (chosen.PrinterProfileSha256 is { } hash && !entries.Any(entry => entry.ContentSha256 == hash))
                    entries.Add(new ProofProfileEntry(L("Str_CM_UnavailableProfile"), hash));
                profile.ItemsSource = new[] { L("Str_CM_NoProfile") }.Concat(entries.Select(entry => entry.Name)).ToArray();
                profile.SelectedIndex = entries.FindIndex(entry => entry.ContentSha256 == chosen.PrinterProfileSha256) + 1;
                enabled.IsChecked = chosen.Enabled;
                intent.SelectedIndex = chosen.Options.RenderingIntent switch
                {
                    PdfRenderingIntent.Perceptual => 0,
                    PdfRenderingIntent.RelativeColorimetric => 1,
                    PdfRenderingIntent.Saturation => 2,
                    PdfRenderingIntent.AbsoluteColorimetric => 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(chosen))
                };
                bpc.IsChecked = chosen.Options.BlackPointCompensation;
                paper.IsChecked = chosen.Options.SimulatePaper;
                black.IsChecked = chosen.Options.SimulateBlack;
                gamut.IsChecked = chosen.GamutWarnings;
                UpdateProfileAvailability();
            };
        }
        fields.Children.Add(Label("Str_CM_Preset", 4));
        fields.Children.Add(presetChoice);
        fields.Children.Add(Label("Str_CM_PresetName", 4));
        fields.Children.Add(presetName);
        fields.Children.Add(UiKit.ButtonRow(loadPreset, savePreset));
        fields.Children.Add(Label("Str_CM_PresetHint", 4));
        fields.Children.Add(presetStatus);
        loadPreset.Click += (_, _) =>
        {
            if (importBusy || !presetsReadable || presetChoice.SelectedIndex <= 0) return;
            var chosen = presets[presetChoice.SelectedIndex - 1];
            if (chosen.PrinterProfileSha256 is { } hash && !entries.Any(entry => entry.ContentSha256 == hash))
                entries.Add(new ProofProfileEntry(L("Str_CM_UnavailableProfile"), hash));
            profile.ItemsSource = new[] { L("Str_CM_NoProfile") }.Concat(entries.Select(entry => entry.Name)).ToArray();
            profile.SelectedIndex = entries.FindIndex(entry => entry.ContentSha256 == chosen.PrinterProfileSha256) + 1;
            enabled.IsChecked = chosen.Enabled;
            intent.SelectedIndex = chosen.Options.RenderingIntent switch
            {
                PdfRenderingIntent.Perceptual => 0,
                PdfRenderingIntent.RelativeColorimetric => 1,
                PdfRenderingIntent.Saturation => 2,
                PdfRenderingIntent.AbsoluteColorimetric => 3,
                _ => throw new ArgumentOutOfRangeException(nameof(chosen))
            };
            bpc.IsChecked = chosen.Options.BlackPointCompensation;
            paper.IsChecked = chosen.Options.SimulatePaper;
            black.IsChecked = chosen.Options.SimulateBlack;
            gamut.IsChecked = chosen.GamutWarnings;
            presetName.Text = chosen.Name;
            UpdateProfileAvailability();
        };
        savePreset.Click += async (_, _) =>
        {
            if (importBusy || !presetsReadable) return;
            presetStatus.Visibility = Visibility.Collapsed;
            try
            {
                var pending = CapturePreset(presetName.Text.Trim());
                SetOperationBusy(true);
                var saved = await Task.Run(() =>
                {
                    presetLibrary.Save(pending);
                    return presetLibrary.GetPresets().ToList();
                });
                if (closed) return;
                presets = saved;
                presetChoice.ItemsSource = new[] { L("Str_CM_NoPreset") }.Concat(presets.Select(item => item.Name)).ToArray();
                presetChoice.SelectedIndex = presets.FindIndex(item => item.Name == pending.Name) + 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                if (closed) return;
                presetStatus.Text = L("Str_CM_PresetSaveFailed");
                presetStatus.Visibility = Visibility.Visible;
            }
            finally
            {
                if (!closed) SetOperationBusy(false);
            }
        };
        apply.Click += async (_, _) =>
        {
            if (importBusy || resolving > 0 || profile.SelectedIndex < 0) return;
            string? working = workingCmyk.SelectedIndex > 0 ? workingEntries[workingCmyk.SelectedIndex - 1].ContentSha256 : null;
            if (libraryReadable && working != savedWorking)
            {
                try
                {
                    library.SetWorkingCmykProfile(working);
                    WorkingCmykChanged = true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or FormatException or NotSupportedException or ArgumentException)
                {
                    profileStatus.Text = L("Str_CM_LibraryUnavailable");
                    profileStatus.Visibility = Visibility.Visible;
                    return;
                }
            }
            // Resolving reads and parses the chosen ICC copy, so it runs off the UI thread like the import.
            var chosen = CapturePreset("Document");
            SetOperationBusy(true);
            try
            {
                var resolved = await Task.Run(() => library.Resolve(chosen).Settings);
                if (closed) return;
                AppliedSettings = resolved;
            }
            finally
            {
                if (!closed) SetOperationBusy(false);
            }
            DialogResult = true;
        };

        PdfProofPreset CapturePreset(string name)
        {
            var preset = JsonNode.Parse(PdfProofPreset.Capture("Document", settings).ToJson())!;
            preset["name"] = name;
            var proof = preset["proof"]!;
            proof["enabled"] = enabled.IsChecked == true;
            proof["printerProfileSha256"] = profile.SelectedIndex == 0 ? null : entries[profile.SelectedIndex - 1].ContentSha256;
            proof["renderingIntent"] = new[] { "perceptual", "relativeColorimetric", "saturation", "absoluteColorimetric" }[intent.SelectedIndex];
            proof["blackPointCompensation"] = bpc.IsChecked == true;
            proof["simulatePaper"] = paper.IsChecked == true;
            proof["simulateBlack"] = black.IsChecked == true;
            proof["gamutWarnings"] = gamut.IsChecked == true;
            return PdfProofPreset.FromJson(preset.ToJsonString());
        }

        void SetOperationBusy(bool busy)
        {
            importBusy = busy;
            import.IsEnabled = profile.IsEnabled = workingCmyk.IsEnabled = libraryReadable && !busy;
            apply.IsEnabled = !busy && resolving == 0;
            presetChoice.IsEnabled = presetName.IsEnabled = savePreset.IsEnabled = presetsReadable && !busy;
            loadPreset.IsEnabled = presetsReadable && !busy && presetChoice.SelectedIndex > 0;
            if (previousChoice is not null) previousChoice.IsEnabled = !busy;
            if (reviewPrevious is not null) reviewPrevious.IsEnabled = !busy;
        }
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
