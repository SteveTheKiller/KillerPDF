using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class WatchedFolderDialog : Window
{
    private readonly WatchedFolderService _service;
    private readonly TextBox _input = UiKit.Field();
    private readonly TextBox _output = UiKit.Field();
    private readonly TextBox _filter = UiKit.Field();
    private readonly ComboBox _macro = new() { Style = UiKit.DarkComboStyle, DisplayMemberPath = nameof(MacroChoice.Label) };
    private readonly CheckBox _pauseOnError = new();
    private readonly TextBlock _preview = Note();
    private readonly TextBlock _status = Note();
    private readonly TextBlock _error = Note();
    private readonly ListBox _results = new() { MinHeight = 100, MaxHeight = 240 };
    private readonly StackPanel _settings = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Button _start, _pause, _resume, _retry;
    private IReadOnlyList<WatchedFolderJob> _lastJobs = [];
    private string _actionError = "";

    internal WatchedFolderDialog(Window owner, WatchedFolderService service,
        IReadOnlyList<PdfMacro> macros, double scale)
    {
        _service = service;
        _filter.Text = "*.pdf";
        Title = "KillerPDF - " + L("Str_Watch_Title");
        Width = Math.Min(900, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(660, SystemParameters.WorkArea.Height - 40);
        MinWidth = 480; MinHeight = 380; UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        AutomationProperties.SetAutomationId(this, "WatchedFolderDialog");
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16),
            LayoutTransform = new ScaleTransform(Math.Clamp(scale, .7, 2.5), Math.Clamp(scale, .7, 2.5)) };
        // One footer row: whether the task runs on the left, Close on the right. Pause, Resume and Retry
        // are icons on the results they act on.
        _status.Margin = new Thickness(0);
        AutomationProperties.SetAutomationId(_status, "WatchStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _pause = IconButton("Str_Watch_Pause", "WatchPause", 0xE769, () => Change(_service.Pause));
        _resume = IconButton("Str_Watch_Resume", "WatchResume", 0xE768, ResumeSavedSetup);
        _retry = IconButton("Str_Watch_Retry", "WatchRetry", 0xE72C, () => Change(_service.RetryFailed));
        var close = Button("Str_Lbl_Close", "WatchClose", Close); close.IsCancel = true;
        close.Margin = new Thickness(0);
        DockPanel footer = DialogParts.Footer([close], _status);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(Note("Str_Watch_Intro"));
        content.Children.Add(Note("Str_Watch_RunningNote"));
        var left = new StackPanel();
        left.Children.Add(UiKit.GroupLabel(L("Str_Watch_Configuration")));
        left.Children.Add(_settings);
        var right = new StackPanel();
        AddPath("Str_Watch_Input", _input, "WatchInput");
        AddPath("Str_Watch_Output", _output, "WatchOutput");
        _settings.Children.Add(UiKit.GroupLabel(L("Str_Watch_Filter")));
        Identify(_filter, "WatchFilter", "Str_Watch_Filter"); _settings.Children.Add(_filter);
        _settings.Children.Add(Note("Str_Watch_FilterHint"));
        _settings.Children.Add(UiKit.GroupLabel(L("Str_Watch_Task")));
        Identify(_macro, "WatchMacro", "Str_Watch_Task");
        _macro.Margin = new Thickness(0, 2, 0, 8); _macro.MinWidth = 140;
        _settings.Children.Add(_macro);
        for (Window? themeOwner = owner; themeOwner is not null && _pauseOnError.Style is null; themeOwner = themeOwner.Owner)
            _pauseOnError.Style = themeOwner.TryFindResource("ThemeCheck") as Style;
        _pauseOnError.Content = new TextBlock { Text = L("Str_Watch_StopOnError"), TextWrapping = TextWrapping.Wrap };
        _pauseOnError.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _pauseOnError.Margin = new Thickness(0, 6, 0, 6);
        Identify(_pauseOnError, "WatchPauseOnError", "Str_Watch_StopOnError");
        _settings.Children.Add(_pauseOnError);
        _settings.Children.Add(Note("Str_Watch_Changed"));
        _start = Button("Str_Watch_Start", "WatchStart", SaveAndStart, accent: true);
        _start.HorizontalAlignment = HorizontalAlignment.Right; _settings.Children.Add(_start);

        // The right column: what the task does and what it has done, both in wells.
        right.Children.Add(UiKit.GroupLabel(L("Str_Watch_Preview")));
        _preview.Margin = new Thickness(8, 6, 8, 6);
        Grid previewWell = DialogParts.Well(_preview);
        previewWell.Margin = new Thickness(0, 2, 0, 4);
        right.Children.Add(previewWell);
        right.Children.Add(Note("Str_Watch_Signatures"));
        right.Children.Add(_error);
        AutomationProperties.SetAutomationId(_error, "WatchError");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        right.Children.Add(DialogParts.Header(L("Str_Watch_Results"), _pause, _resume, _retry));
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new Binding("."));
        row.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        _results.ItemTemplate = new DataTemplate { VisualTree = row };
        Identify(_results, "WatchResults", "Str_Watch_Results");
        right.Children.Add(DialogParts.Well(_results));
        right.Children.Add(Note("Str_Watch_Limits"));
        right.Children.Add(Note("Str_Watch_QueueLimit"));
        content.Children.Add(Columns(left, right));
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);

        WatchedFolderSnapshot snapshot = service.GetSnapshot();
        var available = macros.Select(macro => new MacroChoice(macro.Name, macro)).ToList();
        if (snapshot.Options is { } options)
        {
            _input.Text = options.InputFolder; _output.Text = options.OutputFolder;
            _filter.Text = options.FilePattern; _pauseOnError.IsChecked = options.PauseOnError;
            PdfMacro captured = PdfMacro.FromJson(options.MacroJson);
            available.RemoveAll(item => item.Macro.ToJson() == captured.ToJson());
            available.Insert(0, new MacroChoice(captured.Name + " (" + L("Str_Watch_SavedSetup") + ")", captured));
        }
        _macro.ItemsSource = available;
        _macro.SelectionChanged += (_, _) => { Preview(); Refresh(); };
        _macro.SelectedIndex = available.Count == 0 ? -1 : 0;
        foreach (TextBox field in new[] { _input, _output, _filter }) field.TextChanged += (_, _) => Refresh();
        _pauseOnError.Checked += (_, _) => Refresh();
        _pauseOnError.Unchecked += (_, _) => Refresh();
        Preview(); Refresh();
        _refresh.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _refresh.Start(); _input.Focus(); };
        Closed += (_, _) => _refresh.Stop();
    }

    private void AddPath(string key, TextBox field, string id)
    {
        _settings.Children.Add(UiKit.GroupLabel(L(key))); Identify(field, id, key);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        Button browse = IconButton("Str_Sign_Browse", id + "Browse", 0xE838, () =>
        {
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Folder)
                { Title = L(key), InitialDirectory = field.Text, HideFilesInFolderMode = true, AcceptHighlightedFolder = true };
            if (picker.ShowDialog(this) == true) field.Text = picker.FileName;
        });
        browse.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(field);
        _settings.Children.Add(row);
    }

    // Folder setup on the left, the task preview and file results on the right. A window narrowed below
    // two readable columns stacks them instead, so nothing is squeezed out of reach.
    private static Grid Columns(FrameworkElement left, FrameworkElement right)
    {
        const double gap = 24, stackBelow = 600;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(left);
        grid.Children.Add(right);
        void Arrange(double width)
        {
            bool stacked = width > 0 && width < stackBelow;
            Grid.SetColumnSpan(left, stacked ? 3 : 1);
            Grid.SetColumn(right, stacked ? 0 : 2);
            Grid.SetColumnSpan(right, stacked ? 3 : 1);
            Grid.SetRow(right, stacked ? 1 : 0);
            right.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        }
        Arrange(0);
        grid.SizeChanged += (_, e) => { if (e.WidthChanged) Arrange(e.NewSize.Width); };
        return grid;
    }

    private static Button IconButton(string key, string id, int glyph, Action action)
    {
        Button button = DialogParts.IconAction(key, glyph, (_, _) => action());
        Identify(button, id, key);
        return button;
    }

    private void SaveAndStart() => Change(() =>
    {
        if (_macro.SelectedItem is not MacroChoice choice) throw new InvalidOperationException("InvalidMacro");
        _service.Configure(new(_input.Text.Trim(), _output.Text.Trim(), _filter.Text.Trim(),
            choice.Macro.ToJson(), _pauseOnError.IsChecked == true));
        _service.Resume();
    });

    private void ResumeSavedSetup()
    {
        WatchedFolderSnapshot snapshot = _service.GetSnapshot();
        if (!snapshot.IsPaused || !MatchesSavedSetup(snapshot.Options)) { Refresh(); return; }
        Change(_service.Resume);
    }

    private bool MatchesSavedSetup(WatchedFolderOptions? options)
    {
        if (options is null || _macro.SelectedItem is not MacroChoice choice) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_input.Text.Trim())),
                    options.InputFolder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_output.Text.Trim())),
                    options.OutputFolder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_filter.Text.Trim(), options.FilePattern, StringComparison.OrdinalIgnoreCase)
                && (_pauseOnError.IsChecked == true) == options.PauseOnError
                && choice.Macro.ToJson() == PdfMacro.FromJson(options.MacroJson).ToJson();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException
            or System.Text.Json.JsonException)
        { return false; }
    }

    private void Change(Action action)
    {
        try { action(); _actionError = ""; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _actionError = ErrorText(error.Message);
            if (WatchedFolderService.ConfigurationIssueOf(error) is { } issue)
                _actionError += "\n" + string.Format(L("Str_Watch_FailedStep"),
                    issue.StepIndex + 1, OperationName(issue.Operation));
        }
        Refresh();
    }

    private void Preview()
    {
        _preview.Text = _macro.SelectedItem is MacroChoice choice
            ? string.Join(Environment.NewLine, choice.Macro.Steps.Select((step, index) =>
                $"{index + 1}. {OperationName(step.Operation)}"))
            : L("Str_Watch_NoMacros");
        _start.IsEnabled = _macro.SelectedItem is MacroChoice;
    }

    private void Refresh()
    {
        WatchedFolderSnapshot snapshot = _service.GetSnapshot();
        string readinessError = "";
        if (_macro.SelectedItem is MacroChoice choice)
        {
            try
            {
                if (_service.InspectReadiness(choice.Macro) is { } issue)
                    readinessError = string.Format(L("Str_Watch_MissingOcrModels"), issue.StepIndex + 1,
                        string.Join(", ", issue.MissingOcrLanguages));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { readinessError = L("Str_Watch_Unavailable"); }
        }
        _start.IsEnabled = _macro.SelectedItem is MacroChoice && readinessError.Length == 0;
        _status.Text = L(snapshot.IsPaused ? "Str_Watch_Paused" : "Str_Watch_Running");
        _pause.IsEnabled = !snapshot.IsPaused;
        _resume.IsEnabled = snapshot.IsPaused && readinessError.Length == 0 && MatchesSavedSetup(snapshot.Options);
        _retry.IsEnabled = readinessError.Length == 0 && snapshot.Jobs.Any(job => job.Status == WatchedFolderJobStatus.Failed);
        _settings.IsEnabled = snapshot.IsPaused && !snapshot.Jobs.Any(job => job.Status == WatchedFolderJobStatus.Processing);
        _error.Text = readinessError.Length > 0 ? readinessError
            : snapshot.ErrorCode is null ? _actionError : ErrorText(snapshot.ErrorCode);
        if (_lastJobs.SequenceEqual(snapshot.Jobs) && _results.Items.Count > 0) return;
        _lastJobs = snapshot.Jobs;
        _results.ItemsSource = snapshot.Jobs.Count == 0 ? [L("Str_Watch_Empty")]
            : snapshot.Jobs.Reverse().Select(job => Path.GetFileName(job.InputPath) + "\n"
                + L("Str_Watch_" + (job.Status == WatchedFolderJobStatus.Failed ? "Failed" : job.Status.ToString()))
                + (job.ErrorCode is null ? "" : ": " + ErrorText(job.ErrorCode))
                + (job.FailedStepIndex is int step && job.FailedOperation is PdfMacroOperation operation
                    ? "\n" + string.Format(L("Str_Watch_FailedStep"), step + 1, OperationName(operation)) : "")
                + (string.IsNullOrEmpty(job.OutputPath) ? "" : "\n" + job.OutputPath)).ToArray();
    }

    internal static string ErrorText(string? code) => L(code switch
    {
        "InvalidFolders" or "ReparsePath" or "InvalidPattern" or "InvalidMacro" or "NotConfigured" => "Str_Watch_InvalidSetup",
        "ConfigurationBusy" => "Str_Watch_Busy",
        "UnsupportedOperation" or "InteractiveParameter" or "EncryptedPdf" => "Str_Watch_Unsupported",
        "QueueUnreadable" or "QueueInvalid" or "QueueWriteFailed" or "RecoveryMismatch" => "Str_Watch_QueueError",
        "AlreadyOwned" or "StorageUnavailable" or "InputFolderUnavailable" or "OutputFolderUnavailable" => "Str_Watch_Unavailable",
        "InputBusy" or "InputMissing" or "InputChanged" => "Str_Watch_SourceChanged",
        "InputTooLarge" => "Str_Watch_InputLarge",
        "OutputExists" => "Str_Watch_OutputExists",
        "QueueFull" => "Str_Watch_QueueFull",
        "Canceled" => "Str_Watch_Paused",
        _ => "Str_Watch_Failure"
    });

    private static string OperationName(PdfMacroOperation operation) => L(MacroStepEditor.TitleKey(operation));
    private static Button Button(string key, string id, Action action, bool accent = false)
    {
        Button button = UiKit.Make(new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center }, accent);
        button.Height = double.NaN; button.MinHeight = 30;
        button.Margin = new Thickness(4, 4, 0, 0);
        button.Click += (_, _) => action(); Identify(button, id, key); return button;
    }
    private static void Identify(DependencyObject control, string id, string key)
    { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, L(key)); }
    private static TextBlock Note(string? key = null)
    {
        var text = new TextBlock { Text = key is null ? "" : L(key), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return text;
    }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
    private sealed record MacroChoice(string Label, PdfMacro Macro)
    {
        public override string ToString() => Label;
    }
}
