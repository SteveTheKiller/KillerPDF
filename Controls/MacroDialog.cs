using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class MacroDialog : Window
{
    private readonly PdfMacroStore _store = new();
    private readonly HashSet<PdfMacroOperation> _supported;
    private readonly ListBox _saved = new() { MinWidth = 220 };
    private readonly ListBox _steps = new() { MinWidth = 320 };
    private readonly TextBox _name = UiKit.Field();
    private readonly ComboBox _operation = Combo();
    private readonly TextBlock _availability = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _savedHint = new()
    {
        Text = L("Str_Macro_NoneSaved"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(18),
        IsHitTestVisible = false
    };
    private readonly Button _run;
    private readonly RadioButton _currentDocument = new() { GroupName = "MacroSource", IsChecked = true };
    private readonly RadioButton _batchFiles = new() { GroupName = "MacroSource" };
    private string? _originalName;
    private List<PdfMacroStep> _currentSteps = [];

    internal PdfMacro? MacroToRun { get; private set; }
    internal bool RunOnBatch => _batchFiles.IsChecked == true;

    internal MacroDialog(Window owner, IEnumerable<PdfMacroOperation> supportedOperations,
        IEnumerable<PdfMacroOperation> addableOperations)
        : this(owner, supportedOperations, addableOperations, null) { }

    internal MacroDialog(Window owner, IEnumerable<PdfMacroOperation> supportedOperations,
        IEnumerable<PdfMacroOperation> addableOperations, Action<Window>? openWatchedFolders)
    {
        ArgumentNullException.ThrowIfNull(supportedOperations);
        ArgumentNullException.ThrowIfNull(addableOperations);
        _supported = [.. supportedOperations];
        Title = "KillerPDF - " + L("Str_DocumentTools_Macros");
        Width = 820;
        Height = 560;
        MinWidth = 680;
        MinHeight = 460;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        _operation.ItemsSource = addableOperations.Distinct()
            .Where(_supported.Contains).OrderBy(operation => Display(operation))
            .Select(operation => new OperationChoice(Display(operation), operation));
        _operation.SelectedIndex = _operation.Items.Count == 0 ? -1 : 0;

        _saved.SelectionChanged += (_, _) => LoadSelected();
        _steps.SelectionChanged += (_, _) => UpdateButtons();
        foreach (ListBox list in new[] { _saved, _steps })
        {
            list.Background = Brushes.Transparent;
            list.BorderThickness = new Thickness(0);
            list.Padding = new Thickness(3);
            list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        }
        _savedHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        _name.TextChanged += (_, _) => UpdateButtons();

        // List actions are icons beside the list they act on, named in their tooltips (and to screen readers).
        Button create = IconAction("Str_Lbl_New", 0xE710, (sender, _) => ShowStarterMenu((Button)sender));
        Button duplicate = IconAction("Str_Macro_Duplicate", 0xE8C8, (_, _) => Duplicate());
        Button import = IconAction("Str_Exchange_Import", 0xE896, (_, _) => Import());
        Button export = IconAction("Str_ExportImg_Export", 0xE898, (_, _) => Export());
        Button delete = IconAction("Str_Ctx_BmDelete", 0xE74D, (_, _) => Delete());
        Button save = IconAction("Str_DocInfo_Save", 0xE74E, (_, _) => SaveCurrent());
        Button add = IconAction("Str_AutoBookmarks_Include", 0xE710, (_, _) => AddStep());
        Button settings = IconAction("Str_Redact_Properties", 0xE713, (_, _) => EditStep());
        Button up = IconAction("Str_Ctx_BmMoveUp", 0xE74A, (_, _) => MoveStep(-1));
        Button down = IconAction("Str_Ctx_BmMoveDown", 0xE74B, (_, _) => MoveStep(1));
        Button remove = IconAction("Str_Portfolio_Remove", 0xE711, (_, _) => RemoveStep());
        Button cancel = Button("Str_Btn_Cancel", (_, _) => Close());
        cancel.IsCancel = true;
        _run = Button("Str_Btn_Run", (_, _) => Run(), accent: true);
        _run.IsDefault = true;

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        _availability.Margin = new Thickness(0, 8, 0, 8);
        _availability.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        bottom.Children.Add(_availability);
        // One footer row: where the macro runs and Automatic folders on the left, Cancel and Run on the right.
        var footer = new DockPanel();
        StackPanel confirm = UiKit.ButtonRow(cancel, _run);
        confirm.VerticalAlignment = VerticalAlignment.Center;
        confirm.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(confirm, Dock.Right);
        footer.Children.Add(confirm);
        var sourceChoices = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        _currentDocument.Content = L("Str_Macro_Current");
        _batchFiles.Content = L("Str_Macro_Batch");
        foreach (RadioButton choice in new[] { _currentDocument, _batchFiles })
        {
            choice.Margin = new Thickness(0, 0, 18, 0);
            choice.VerticalAlignment = VerticalAlignment.Center;
            choice.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            sourceChoices.Children.Add(choice);
        }
        if (openWatchedFolders is not null)
        {
            Button folders = Labeled("Str_Watch_Title", 0xE838, (_, _) => openWatchedFolders(this));
            System.Windows.Automation.AutomationProperties.SetAutomationId(folders, "OpenWatchedFolders");
            folders.VerticalAlignment = VerticalAlignment.Center;
            sourceChoices.Children.Add(folders);
        }
        footer.Children.Add(sourceChoices);
        bottom.Children.Add(footer);
        body.Children.Add(bottom);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement saved = BuildSavedPanel(create, duplicate, import, export, delete);
        Grid.SetColumn(saved, 0);
        columns.Children.Add(saved);
        UIElement editor = BuildEditorPanel(save, add, settings, up, down, remove);
        Grid.SetColumn(editor, 2);
        columns.Children.Add(editor);
        body.Children.Add(columns);

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Reload();
    }

    private DockPanel BuildSavedPanel(params Button[] icons)
    {
        var panel = new DockPanel();
        UIElement header = Header(L("Str_Macro_Saved"), icons);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(Well(_saved, _savedHint));
        return panel;
    }

    private DockPanel BuildEditorPanel(Button save, Button add, params Button[] stepIcons)
    {
        var panel = new DockPanel();

        // The new step's kind and its add icon, under the list it adds to.
        var addRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(6, 0, 0, 0);
        addRow.Children.Add(add);
        addRow.Children.Add(_operation);
        DockPanel.SetDock(addRow, Dock.Bottom);
        panel.Children.Add(addRow);

        var top = new StackPanel();
        top.Children.Add(UiKit.GroupLabel(L("Str_Attachment_Name")));
        var nameRow = new DockPanel { Margin = new Thickness(0, 2, 0, 12) };
        DockPanel.SetDock(save, Dock.Right);
        save.Margin = new Thickness(6, 0, 0, 0);
        nameRow.Children.Add(save);
        nameRow.Children.Add(_name);
        top.Children.Add(nameRow);
        top.Children.Add(Header(L("Str_Macro_Steps"), stepIcons));
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(Well(_steps, null));
        return panel;
    }

    // A list sits in a well: the pane fill at half strength with the pane's edge, so the area reads on every
    // theme (Light's card edge alone is invisible) while the window's texture still shows through it.
    private static Grid Well(ListBox list, TextBlock? emptyHint)
    {
        var well = new Grid();
        var fill = new Border { Opacity = 0.5, IsHitTestVisible = false };
        fill.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
        fill.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
        var edge = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        edge.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
        well.Children.Add(fill);
        well.Children.Add(list);
        if (emptyHint is not null) well.Children.Add(emptyHint);
        well.Children.Add(edge);
        return well;
    }

    // A list's heading on the left and its action icons on the right, on one line above the list.
    private static UIElement Header(string title, Button[] icons)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (Button icon in icons) strip.Children.Add(icon);
        DockPanel.SetDock(strip, Dock.Right);
        row.Children.Add(strip);
        TextBlock label = UiKit.GroupLabel(title);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.Margin = new Thickness(0, 0, 8, 4);
        row.Children.Add(label);
        return row;
    }

    // New offers the starter macros as a menu at the button, replacing the starter list that sat under
    // the saved macros and read like a second list of macros.
    private void ShowStarterMenu(Button anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        foreach (PdfMacroStarterKind kind in Enum.GetValues<PdfMacroStarterKind>())
        {
            var item = new MenuItem { Header = L(StarterKey(kind)) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "Starter" + kind);
            item.Click += (_, _) => CreateStarter(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal static string StarterKey(PdfMacroStarterKind kind) => kind switch
    {
        PdfMacroStarterKind.Archival => "Str_Macro_StarterArchival",
        PdfMacroStarterKind.Scanning => "Str_Macro_StarterScanning",
        PdfMacroStarterKind.Privacy => "Str_Macro_StarterPrivacy",
        _ => "Str_Macro_StarterSharing",
    };

    private void Reload(string? selectName = null)
    {
        try
        {
            IReadOnlyList<PdfMacro> macros = _store.LoadAll();
            _saved.ItemsSource = macros;
            _saved.DisplayMemberPath = nameof(PdfMacro.Name);
            _savedHint.Visibility = macros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _saved.SelectedItem = macros.FirstOrDefault(macro => string.Equals(
                macro.Name, selectName, StringComparison.OrdinalIgnoreCase))
                ?? (macros.Count > 0 ? macros[0] : null);
            if (_saved.SelectedItem is null) CreateStarter();
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            KillerDialog.Show(this, L("Str_Macro_LoadFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSelected()
    {
        if (_saved.SelectedItem is not PdfMacro macro) return;
        _originalName = macro.Name;
        _name.Text = macro.Name;
        _currentSteps = [.. macro.Steps.Select(Copy)];
        RefreshSteps();
    }

    private void CreateStarter(PdfMacroStarterKind kind = PdfMacroStarterKind.Sharing)
    {
        PdfMacro macro = PdfMacro.CreateStarter(kind);
        _originalName = null;
        // Named in the window's language; the engine's starter names are English.
        _name.Text = UniqueName(L(StarterKey(kind)));
        _currentSteps = [.. macro.Steps.Select(Copy)];
        _saved.SelectedItem = null;
        RefreshSteps();
    }

    private void Duplicate()
    {
        if (!TryBuild(out PdfMacro macro)) return;
        string name = UniqueName(macro.Name + " " + L("Str_Macro_Copy"));
        PdfMacro copy = macro.Duplicate(name);
        _store.Save(copy);
        Reload(copy.Name);
    }

    private void AddStep()
    {
        if (_operation.SelectedItem is not OperationChoice choice) return;
        int index = _steps.SelectedIndex < 0 ? _currentSteps.Count : _steps.SelectedIndex + 1;
        var step = new PdfMacroStep(choice.Operation);
        if (choice.Operation is PdfMacroOperation.NumberPages or PdfMacroOperation.GenerateBookmarks
            or PdfMacroOperation.GenerateTableOfContents or PdfMacroOperation.ImposeNUp
            or PdfMacroOperation.ImposeBooklet or PdfMacroOperation.ImposeStepAndRepeat
            or PdfMacroOperation.ImposeCutStack or PdfMacroOperation.ImposeManualSequence or PdfMacroOperation.ImposePoster
            or PdfMacroOperation.EditLayers or PdfMacroOperation.EditPortfolio
            or PdfMacroOperation.SetPageLabels or PdfMacroOperation.EditAttachments or PdfMacroOperation.Watermark
            or PdfMacroOperation.Rename or PdfMacroOperation.DataMerge or PdfMacroOperation.Resize or PdfMacroOperation.ConvertColor
            or PdfMacroOperation.Export)
        {
            step = MacroStepEditor.Edit(this, step);
            if (step is null) return;
        }
        _currentSteps.Insert(index, step);
        RefreshSteps(index);
    }

    private void RemoveStep()
    {
        int index = _steps.SelectedIndex;
        if (index < 0 || _currentSteps.Count == 1) return;
        _currentSteps.RemoveAt(index);
        RefreshSteps(Math.Min(index, _currentSteps.Count - 1));
    }

    private void EditStep()
    {
        int index = _steps.SelectedIndex;
        if (index < 0) return;
        PdfMacroStep? edited = MacroStepEditor.Edit(this, _currentSteps[index]);
        if (edited is null) return;
        _currentSteps[index] = edited;
        RefreshSteps(index);
    }

    private void MoveStep(int offset)
    {
        int from = _steps.SelectedIndex;
        int to = from + offset;
        if (from < 0 || to < 0 || to >= _currentSteps.Count) return;
        (_currentSteps[from], _currentSteps[to]) = (_currentSteps[to], _currentSteps[from]);
        RefreshSteps(to);
    }

    private bool SaveCurrent()
    {
        if (!TryBuild(out PdfMacro macro)) return false;
        try
        {
            _store.Save(macro);
            if (_originalName is not null && !string.Equals(_originalName, macro.Name,
                    StringComparison.OrdinalIgnoreCase))
                _store.Remove(_originalName);
            Reload(macro.Name);
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            KillerDialog.Show(this, L("Str_Macro_SaveFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Delete()
    {
        if (_saved.SelectedItem is not PdfMacro macro) return;
        if (KillerDialog.Show(this,
                string.Format(L("Str_Macro_DeleteConfirm"), macro.Name), "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _store.Remove(macro.Name);
        _originalName = null;
        Reload();
    }

    private void Import()
    {
        var dialog = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = L("Str_Exchange_Import"),
            Filter = L("Str_Macro_Files") + "|*.json|" + L("Str_Dlg_AllFiles") + "|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PdfMacro macro = PdfMacro.FromJson(File.ReadAllText(dialog.FileName));
            _store.Save(macro);
            Reload(macro.Name);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            KillerDialog.Show(this, L("Str_Macro_ImportFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export()
    {
        if (!TryBuild(out PdfMacro macro)) return;
        var dialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = L("Str_ExportImg_Export"),
            Filter = L("Str_Macro_Files") + "|*.json",
            DefaultExt = "json",
            FileName = SafeFileName(macro.Name) + ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, macro.ToJson(indented: true));
            File.Move(temporary, dialog.FileName, overwrite: true);
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            KillerDialog.Show(this, L("Str_Macro_ExportFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Run()
    {
        if (!SaveCurrent() || _saved.SelectedItem is not PdfMacro macro) return;
        if (macro.Steps.Any(step => !_supported.Contains(step.Operation))) return;
        MacroToRun = macro;
        DialogResult = true;
        Close();
    }

    private bool TryBuild(out PdfMacro macro)
    {
        string name = _name.Text.Trim();
        if (name.Length == 0 || _currentSteps.Count == 0)
        {
            KillerDialog.Show(this, L("Str_Macro_Invalid"), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            macro = null!;
            return false;
        }
        macro = new PdfMacro(name, _currentSteps.Select(Copy));
        return true;
    }

    private void RefreshSteps(int selectedIndex = 0)
    {
        _steps.ItemsSource = _currentSteps.Select((step, index) => new StepChoice(
            index + 1, Display(step.Operation), _supported.Contains(step.Operation))).ToArray();
        _steps.SelectedIndex = _currentSteps.Count == 0 ? -1
            : Math.Clamp(selectedIndex, 0, _currentSteps.Count - 1);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        PdfMacroOperation[] unavailable = [.. _currentSteps
            .Select(step => step.Operation).Distinct()
            .Where(operation => !_supported.Contains(operation))];
        _availability.Text = unavailable.Length == 0
            ? L("Str_Macro_Ready")
            : L("Str_Macro_Unavailable") + " "
                + string.Join(", ", unavailable.Select(Display));
        _run.IsEnabled = _name.Text.Trim().Length > 0
            && _currentSteps.Count > 0 && unavailable.Length == 0;
    }

    private string UniqueName(string requested)
    {
        HashSet<string> names = _store.LoadAll().Select(macro => macro.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested)) return requested;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = requested + " " + suffix;
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. value.Select(character => invalid.Contains(character) ? '_' : character)]);
        return safe.Trim().Length == 0 ? "macro" : safe.Trim();
    }

    private static string Display<T>(T value) where T : Enum
    {
        if (value is PdfMacroOperation named) return L(MacroStepEditor.TitleKey(named));
        string text = value.ToString();
        var result = new StringBuilder(text.Length + 8);
        for (int index = 0; index < text.Length; index++)
        {
            if (index > 0 && char.IsUpper(text[index]) && !char.IsUpper(text[index - 1]))
                result.Append(' ');
            result.Append(text[index]);
        }
        return result.ToString();
    }

    private static PdfMacroStep Copy(PdfMacroStep step) => new(step.Operation,
        step.Settings is null ? null
            : new Dictionary<string, string>(step.Settings, StringComparer.Ordinal));

    private static ComboBox Combo() => new()
    {
        Height = 30,
        Style = UiKit.DarkComboStyle
    };

    private static Button Button(string key, RoutedEventHandler click, bool accent = false)
    {
        Button button = UiKit.Make(L(key), accent);
        button.Click += click;
        return button;
    }

    // An icon action: bare until hover (the family's BareButton), named by its tooltip, which the style also
    // gives screen readers. The automation id is the label key, so tests find it in any language.
    private static Button IconAction(string key, int glyph, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = Glyph(glyph),
            ToolTip = L(key),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.SetResourceReference(StyleProperty, "BareButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, key);
        button.Click += click;
        return button;
    }

    // An icon with its words beside it, for an action that opens another window rather than acting on a list.
    private static Button Labeled(string key, int glyph, RoutedEventHandler click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock icon = Glyph(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = L(key), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, ToolTip = L(key), Padding = new Thickness(8, 5, 10, 5) };
        button.SetResourceReference(StyleProperty, "BareButton");
        button.Click += click;
        return button;
    }

    private static TextBlock Glyph(int codePoint) => new()
    {
        Text = char.ConvertFromUtf32(codePoint),
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static bool Recoverable(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException;

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    private sealed record OperationChoice(string Label, PdfMacroOperation Operation)
    {
        public override string ToString() => Label;
    }

    private sealed record StepChoice(int Index, string Label, bool Supported)
    {
        public override string ToString() => Supported
            ? $"{Index}. {Label}" : $"{Index}. {Label} ({L("Str_Macro_NotAvailable")})";
    }
}
