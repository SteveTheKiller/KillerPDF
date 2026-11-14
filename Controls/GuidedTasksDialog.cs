using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal enum GuidedTaskKind { Email, Scan, Privacy, Form }
internal sealed record GuidedTaskRequest(GuidedTaskKind Kind, string Output, bool Flatten, bool Ocr, bool Search);
internal sealed record GuidedTaskResult(string Path, long Before, long After, int SkippedPictures = 0);

internal sealed class GuidedTasksDialog : Window
{
    private static readonly string[] Names = ["Str_Guided_Email", "Str_Guided_Scan", "Str_Guided_Privacy", "Str_Guided_Form"];
    private static readonly string[] Hints = ["Str_Guided_EmailHint", "Str_Guided_ScanHint", "Str_Guided_PrivacyHint", "Str_Guided_FormHint"];
    private readonly ComboBox _choice;
    private readonly TextBox _output;
    private readonly TextBlock _hint, _status, _marks;
    private readonly CheckBox _flatten, _ocr, _search;
    private readonly Button _browse, _run, _cancel, _macro, _open;
    private readonly ProgressBar _progress;
    private readonly Func<GuidedTasksDialog, GuidedTaskRequest, IProgress<string>, CancellationToken, Task<GuidedTaskResult>> _execute;
    private CancellationTokenSource? _cancellation;
    private string? _completed;
    private bool _closePending;
    internal string? OpenPath { get; private set; }

    internal GuidedTasksDialog(Window owner, string source, double scale, int marks,
        Func<GuidedTasksDialog, GuidedTaskRequest, IProgress<string>, CancellationToken, Task<GuidedTaskResult>> execute)
    {
        _execute = execute;
        Title = "KillerPDF - " + L("Str_Guided_Title");
        scale = Math.Clamp(scale, .7, 2.5);
        Width = Math.Min(620 * scale, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(430 * scale, SystemParameters.WorkArea.Height - 40);
        MinWidth = 400; MinHeight = 300; UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        // It opens 620 x 430 DIP times the app scale and its content scrolls, so 400 x 300 stays its minimum rather than
        // the opening size, which at a large scale is most of the screen.
        DialogFrame.KeepDeclaredMinimum(this);
        Id(this, "GuidedTasksDialog", "Str_Guided_Title");
        var body = new DockPanel { Margin = new Thickness(14, 4, 14, 12), LayoutTransform = new ScaleTransform(scale, scale) };
        _run = Button("Str_Btn_Run", "GuidedRun", true); _run.IsDefault = true;
        _cancel = Button("Str_Btn_Cancel", "GuidedCancel");
        _open = Button("Str_Btn_Open", "GuidedOpen"); _open.Visibility = Visibility.Collapsed;
        // One footer row: Save as macro on the left (what it saves is named in its tooltip), Open, Cancel and Run on the right.
        _macro = Button("Str_Guided_SaveMacro", "GuidedSaveMacro"); _macro.ToolTip = L("Str_Guided_MacroHint");
        ToolTipService.SetShowOnDisabled(_macro, true); // it explains why the review tasks cannot be saved
        foreach (Button button in new[] { _macro, _open, _cancel, _run }) button.Margin = new Thickness(0, 4, 0, 4);
        DockPanel footer = DialogParts.Footer([_open, _cancel, _run], _macro);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(Note(Path.GetFileName(source)));
        _choice = new ComboBox { Style = UiKit.DarkComboStyle, Margin = new Thickness(0, 4, 0, 4) };
        Id(_choice, "GuidedTaskChoice", "Str_Guided_Title");
        foreach (string key in Names) _choice.Items.Add(new ComboBoxItem { Content = L(key) });
        content.Children.Add(_choice);
        _hint = Note(""); content.Children.Add(_hint);
        _flatten = Check("Str_Guided_Flatten", "GuidedFlatten"); content.Children.Add(_flatten);
        _ocr = Check("Str_Guided_Ocr", "GuidedOcr"); _ocr.IsChecked = true; content.Children.Add(_ocr);
        _search = Check("Str_Guided_Search", "GuidedSearch"); _search.IsChecked = marks == 0; content.Children.Add(_search);
        _marks = Note(string.Format(CultureInfo.CurrentCulture, L("Str_Guided_Marks"), marks)); content.Children.Add(_marks);
        TextBlock outputLabel = UiKit.GroupLabel(L("Str_Guided_Output")); outputLabel.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(outputLabel);
        var destination = new DockPanel();
        _browse = DialogParts.IconAction("Str_Sign_Browse", 0xE838, (_, _) => { }); Id(_browse, "GuidedBrowse", "Str_Sign_Browse");
        _browse.Margin = new Thickness(4, 0, 0, 0); DockPanel.SetDock(_browse, Dock.Right); destination.Children.Add(_browse);
        _output = UiKit.Field(); _output.IsReadOnly = true; _output.VerticalContentAlignment = VerticalAlignment.Center;
        _output.Margin = new Thickness(0, 2, 0, 2); Id(_output, "GuidedOutput", "Str_Guided_Output"); destination.Children.Add(_output);
        content.Children.Add(destination);
        TextBlock copyHint = Note(L("Str_Guided_CopyHint")); copyHint.FontSize = 11; copyHint.Margin = new Thickness(0, 0, 0, 2);
        copyHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(copyHint);
        _progress = new ProgressBar { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 6) };
        content.Children.Add(_progress);
        _status = Note(""); Id(_status, "GuidedStatus"); AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite); content.Children.Add(_status);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 4) };
        body.Children.Add(scroll);
        // A window too narrow for Save as macro beside the confirm buttons (a large app scale) moves it to the end of the
        // content, still in reach; it returns to the footer when the row has room again.
        var confirmButtons = (FrameworkElement)footer.Children[0];
        void FitMacro()
        {
            _macro.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool fits = footer.ActualWidth <= 0
                || footer.ActualWidth >= confirmButtons.ActualWidth + 12 + _macro.DesiredSize.Width + 18;
            Panel? home = _macro.Parent as Panel;
            Panel target = fits ? (Panel)footer.Children[1] : content;
            if (ReferenceEquals(home, target)) return;
            home?.Children.Remove(_macro);
            _macro.HorizontalAlignment = HorizontalAlignment.Left;
            target.Children.Add(_macro);
        }
        footer.SizeChanged += (_, _) => FitMacro();
        confirmButtons.SizeChanged += (_, _) => FitMacro();
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        _choice.SelectionChanged += (_, _) => { _hint.Text = L(Hints[_choice.SelectedIndex]); ResetResult(); Update(); };
        _choice.SelectedIndex = 0;
        foreach (CheckBox option in new[] { _flatten, _ocr, _search })
        { option.Checked += (_, _) => ResetResult(); option.Unchecked += (_, _) => ResetResult(); }
        _browse.Click += (_, _) =>
        {
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Title = L("Str_Guided_Output"), Filter = L("Str_Filter_Pdf") + "|*.pdf", DefaultExt = "pdf", RequireFilterExtension = true,
                OverwritePrompt = false,
                FileName = Path.GetFileNameWithoutExtension(source) + "-" + Kind.ToString().ToLowerInvariant() + ".pdf"
            };
            if (picker.ShowDialog(this) == true) { _output.Text = picker.FileName; ResetResult(); Update(); }
        };
        _run.Click += async (_, _) => await Run();
        _cancel.Click += (_, _) => { if (_cancellation is not null) _cancellation.Cancel(); else Close(); };
        _open.Click += (_, _) => { OpenPath = _completed; Close(); };
        _macro.Click += (_, _) => SaveMacro();
        Closing += (_, args) => { if (_cancellation is not null) { args.Cancel = true; _closePending = true; _cancellation.Cancel(); } };
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; if (_cancellation is not null) _cancellation.Cancel(); else Close(); } };
        Update();
    }

    private GuidedTaskKind Kind => (GuidedTaskKind)_choice.SelectedIndex;
    private GuidedTaskRequest Request => new(Kind, _output.Text, _flatten.IsChecked == true, _ocr.IsChecked == true, _search.IsChecked == true);

    internal static PdfMacro Macro(GuidedTaskRequest request, string name)
    {
        var steps = new List<PdfMacroStep>();
        if (request.Kind == GuidedTaskKind.Scan && request.Ocr) steps.Add(new(PdfMacroOperation.Ocr));
        steps.Add(new(PdfMacroOperation.Optimize));
        if (request.Kind == GuidedTaskKind.Email && request.Flatten) steps.Add(new(PdfMacroOperation.Flatten));
        steps.Add(new(PdfMacroOperation.Save));
        return new PdfMacro(name, steps);
    }

    private async Task Run()
    {
        if (_cancellation is not null) return;
        ResetResult(); var cancellation = _cancellation = new CancellationTokenSource(); Update();
        _status.Text = L(Names[_choice.SelectedIndex]) + "...";
        try
        {
            GuidedTaskResult result = await _execute(this, Request, new Progress<string>(value =>
            { if (ReferenceEquals(_cancellation, cancellation) && !cancellation.IsCancellationRequested) _status.Text = value; }), cancellation.Token);
            _completed = result.Path;
            _status.Text = KillerPDF.Services.PicturesSkippedNote.Append(
                string.Format(CultureInfo.CurrentCulture, L("Str_Guided_Complete"), result.Path, Size(result.Before), Size(result.After)),
                result.SkippedPictures, L("Str_Save_PicturesSkipped"));
            _open.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { _status.Text = L("Str_St_Canceled"); _run.Content = L("Str_Guided_Retry"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _status.Text = error.Message; _run.Content = L("Str_Guided_Retry"); }
        finally { _cancellation.Dispose(); _cancellation = null; Update(); if (_closePending) Close(); }
    }

    private void SaveMacro()
    {
        if (Kind is not (GuidedTaskKind.Email or GuidedTaskKind.Scan)) return;
        try
        {
            var store = new PdfMacroStore(); var names = store.LoadAll().Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string basis = L(Names[_choice.SelectedIndex]), name = basis;
            for (int number = 2; names.Contains(name); number++) name = basis + " (" + number + ")";
            store.Save(Macro(Request, name));
            _status.Text = string.Format(CultureInfo.CurrentCulture, L("Str_Guided_SavedMacro"), name);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = L("Str_Macro_SaveFailed") + " " + error.Message; }
    }

    private void ResetResult() { _completed = null; _open.Visibility = Visibility.Collapsed; _status.Text = ""; _run.Content = L("Str_Btn_Run"); }
    private void Update()
    {
        bool ready = _cancellation is null;
        _choice.IsEnabled = _browse.IsEnabled = _flatten.IsEnabled = _ocr.IsEnabled = _search.IsEnabled = ready;
        _run.IsEnabled = ready && !string.IsNullOrWhiteSpace(_output.Text);
        _macro.IsEnabled = ready && Kind is GuidedTaskKind.Email or GuidedTaskKind.Scan;
        _progress.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _flatten.Visibility = Kind == GuidedTaskKind.Email ? Visibility.Visible : Visibility.Collapsed;
        _ocr.Visibility = Kind == GuidedTaskKind.Scan ? Visibility.Visible : Visibility.Collapsed;
        _search.Visibility = _marks.Visibility = Kind == GuidedTaskKind.Privacy ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string Size(long bytes) => (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB";
    private static Button Button(string key, string id, bool accent = false)
    {
        Button button = UiKit.Make(L(key), accent); button.MinHeight = 30;
        button.Margin = new Thickness(0, 4, 6, 4); button.Padding = new Thickness(8, 3, 8, 3); Id(button, id, key); return button;
    }
    private static CheckBox Check(string key, string id)
    { CheckBox box = UiKit.CheckBox(L(key)); box.Margin = new Thickness(0, 5, 0, 5); Id(box, id, key); return box; }
    private static TextBlock Note(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont"); block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return block;
    }
    private static void Id(DependencyObject item, string id, string? key = null)
    { AutomationProperties.SetAutomationId(item, id); if (key is not null) AutomationProperties.SetName(item, L(key)); }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
