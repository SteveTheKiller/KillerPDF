using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ComboBox _starter = Combo();
    private readonly ComboBox _operation = Combo();
    private readonly TextBlock _availability = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _run;
    private string? _originalName;
    private List<PdfMacroStep> _currentSteps = [];

    internal PdfMacro? MacroToRun { get; private set; }

    internal MacroDialog(Window owner, IEnumerable<PdfMacroOperation> supportedOperations,
        IEnumerable<PdfMacroOperation> addableOperations)
    {
        ArgumentNullException.ThrowIfNull(supportedOperations);
        ArgumentNullException.ThrowIfNull(addableOperations);
        _supported = [.. supportedOperations];
        Title = "KillerPDF - " + L("Str_DocumentTools_Macros");
        Width = 900;
        Height = 610;
        MinWidth = 760;
        MinHeight = 480;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        _starter.ItemsSource = Enum.GetValues<PdfMacroStarterKind>()
            .Select(kind => new StarterChoice(Display(kind), kind));
        _starter.SelectedIndex = 0;
        _operation.ItemsSource = addableOperations.Distinct()
            .Where(_supported.Contains).OrderBy(operation => Display(operation))
            .Select(operation => new OperationChoice(Display(operation), operation));
        _operation.SelectedIndex = _operation.Items.Count == 0 ? -1 : 0;

        _saved.SelectionChanged += (_, _) => LoadSelected();
        _steps.SelectionChanged += (_, _) => UpdateButtons();
        _name.TextChanged += (_, _) => UpdateButtons();

        Button create = Button("Str_Lbl_New", (_, _) => CreateStarter());
        Button duplicate = Button("Str_Macro_Duplicate", (_, _) => Duplicate());
        Button import = Button("Str_Menu_Import", (_, _) => Import());
        Button export = Button("Str_ExportImg_Export", (_, _) => Export());
        Button delete = Button("Str_Ctx_BmDelete", (_, _) => Delete());
        Button save = Button("Str_DocInfo_Save", (_, _) => SaveCurrent(), accent: true);
        Button add = Button("Str_AutoBookmarks_Include", (_, _) => AddStep());
        Button remove = Button("Str_Portfolio_Remove", (_, _) => RemoveStep());
        Button up = Button("Str_Ctx_BmMoveUp", (_, _) => MoveStep(-1));
        Button down = Button("Str_Ctx_BmMoveDown", (_, _) => MoveStep(1));
        Button cancel = Button("Str_Btn_Cancel", (_, _) => Close());
        cancel.IsCancel = true;
        _run = Button("Str_Btn_Run", (_, _) => Run(), accent: true);
        _run.IsDefault = true;

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        _availability.Margin = new Thickness(0, 8, 0, 4);
        bottom.Children.Add(_availability);
        bottom.Children.Add(UiKit.ButtonRow(cancel, _run));
        body.Children.Add(bottom);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement saved = BuildSavedPanel(create, duplicate, import, export, delete);
        Grid.SetColumn(saved, 0);
        columns.Children.Add(saved);
        UIElement editor = BuildEditorPanel(save, add, remove, up, down);
        Grid.SetColumn(editor, 2);
        columns.Children.Add(editor);
        body.Children.Add(columns);

        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Reload();
    }

    private UIElement BuildSavedPanel(params Button[] buttons)
    {
        var panel = new DockPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (Button button in buttons)
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        DockPanel.SetDock(_starter, Dock.Bottom);
        _starter.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(_starter);
        TextBlock label = UiKit.GroupLabel(L("Str_Macro_Saved"));
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(_saved);
        return panel;
    }

    private UIElement BuildEditorPanel(Button save, Button add, Button remove,
        Button up, Button down)
    {
        var panel = new DockPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (Button button in new[] { save, remove, up, down })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);

        var addRow = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(8, 0, 0, 0);
        addRow.Children.Add(add);
        addRow.Children.Add(_operation);
        DockPanel.SetDock(addRow, Dock.Bottom);
        panel.Children.Add(addRow);

        var heading = new StackPanel();
        heading.Children.Add(UiKit.GroupLabel(L("Str_Attachment_Name")));
        _name.Margin = new Thickness(0, 2, 0, 8);
        heading.Children.Add(_name);
        heading.Children.Add(UiKit.GroupLabel(L("Str_Macro_Steps")));
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        panel.Children.Add(_steps);
        return panel;
    }

    private void Reload(string? selectName = null)
    {
        try
        {
            IReadOnlyList<PdfMacro> macros = _store.LoadAll();
            _saved.ItemsSource = macros;
            _saved.DisplayMemberPath = nameof(PdfMacro.Name);
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

    private void CreateStarter()
    {
        PdfMacroStarterKind kind = (_starter.SelectedItem as StarterChoice)?.Kind
            ?? PdfMacroStarterKind.Sharing;
        PdfMacro macro = PdfMacro.CreateStarter(kind);
        _originalName = null;
        _name.Text = UniqueName(macro.Name);
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
        _currentSteps.Insert(index, new PdfMacroStep(choice.Operation));
        RefreshSteps(index);
    }

    private void RemoveStep()
    {
        int index = _steps.SelectedIndex;
        if (index < 0 || _currentSteps.Count == 1) return;
        _currentSteps.RemoveAt(index);
        RefreshSteps(Math.Min(index, _currentSteps.Count - 1));
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
            Title = L("Str_Menu_Import"),
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
        Style = Application.Current?.TryFindResource("DarkComboBox") as Style
    };

    private static Button Button(string key, RoutedEventHandler click, bool accent = false)
    {
        Button button = UiKit.Make(L(key), accent);
        button.Click += click;
        return button;
    }

    private static bool Recoverable(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException;

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    private sealed record StarterChoice(string Label, PdfMacroStarterKind Kind)
    {
        public override string ToString() => Label;
    }

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
