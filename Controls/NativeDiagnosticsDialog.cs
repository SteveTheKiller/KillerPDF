using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF;

// Lists, per chapter, what a native book left out, with Copy and Close like the document reports. A book still being
// paginated says so above the list, which covers the chapters counted so far, and the list fills in when counting ends.
internal sealed class NativeDiagnosticsDialog : Window
{
    private readonly TextBlock _intro;
    private readonly StackPanel _list;
    private readonly Button _copy;
    private readonly NativeNoticeWording _wording;
    private string _report = "";

    internal NativeDiagnosticsDialog(Window owner, IReadOnlyList<NativeDiagnosticRow> rows)
        : this(owner, rows, null, null) { }

    /// <param name="rebuild">Reads the rows again, once the book has finished counting its pages.</param>
    /// <param name="counting">The still-counting note, or null when every page is known.</param>
    /// <param name="wording">The title, intro and empty note for the document's format; a book's by default.</param>
    internal NativeDiagnosticsDialog(Window owner, IReadOnlyList<NativeDiagnosticRow> rows,
        Func<IReadOnlyList<NativeDiagnosticRow>>? rebuild, Func<string?>? counting, NativeNoticeWording? wording = null)
    {
        _wording = wording ?? NativeDocumentDiagnostics.WordingFor(KillerPdf.Engine.Documents.PdfDocumentFormat.Epub);
        Title = "KillerPDF - " + L(_wording.Title);
        Width = 640;
        Height = 520;
        MinWidth = 420;
        MinHeight = 320;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new Grid { Margin = new Thickness(20, 6, 20, 16) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _intro = Text("", muted: true);
        _intro.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetLiveSetting(_intro, AutomationLiveSetting.Polite);
        body.Children.Add(_intro);

        _list = new StackPanel { Name = "NativeDiagnosticsList" };
        AutomationProperties.SetName(_list, L(_wording.Title));
        var scroller = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true
        };
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);

        // The copied report keeps the engine's English diagnosis after each localized reason.
        _copy = UiKit.Make(L("Str_DocumentTools_Copy"), accent: false);
        _copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_report);
                _copy.Content = L("Str_DocumentTools_Copied");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = UiKit.ButtonRow(_copy, close);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(buttons, 2);
        body.Children.Add(buttons);

        Show(rows, counting?.Invoke());
        if (counting?.Invoke() is not null && rebuild is not null)
        {
            // While the book counts its pages the note follows its progress; when counting ends the list is read again.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                if (counting() is { } note)
                {
                    _intro.Text = note;
                    return;
                }
                timer.Stop();
                Show(rebuild(), null);
            };
            timer.Start();
            Closed += (_, _) => timer.Stop();
        }

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => scroller.Focus();
    }

    private void Show(IReadOnlyList<NativeDiagnosticRow> rows, string? countingNote)
    {
        _intro.Text = countingNote ?? L(rows.Count > 0 ? _wording.Intro : _wording.None);
        _list.Children.Clear();
        foreach (var row in rows)
        {
            var heading = Text(row.Heading, muted: false);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 12, 0, 4);
            _list.Children.Add(heading);
            foreach (string line in row.Lines)
            {
                var item = Text(line, muted: true);
                item.Margin = new Thickness(12, 0, 0, 2);
                _list.Children.Add(item);
            }
        }
        _report = NativeDocumentDiagnostics.Report(rows);
        _copy.IsEnabled = rows.Count > 0;
        _copy.Content = L("Str_DocumentTools_Copy");
    }

    private static TextBlock Text(string text, bool muted)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedTextBrush" : "TextBrush");
        return block;
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
