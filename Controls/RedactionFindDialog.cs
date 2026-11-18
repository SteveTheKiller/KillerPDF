using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

// Find text to redact: words, phrases, a regular expression or a common pattern such as email
// addresses is searched across the whole document, every result is listed for review, and only
// the checked results come back to be marked.
internal sealed class RedactionFindDialog : Window
{
    private readonly Func<(IReadOnlyList<PdfPageContent?> Pages, IReadOnlyList<int> Unreadable)> _loadPages;
    private readonly ComboBox _kind;
    private readonly TextBlock _queryLabel;
    private readonly TextBox _query;
    private readonly CheckBox _matchCase;
    private readonly TextBlock _summary;
    private readonly TextBlock _skippedPages;
    private readonly StackPanel _list;
    private readonly StackPanel _checkLinks;
    private readonly Button _mark;
    private readonly List<(PdfRedactionMatch Match, CheckBox Include)> _rows = [];
    private IReadOnlyList<PdfPageContent?>? _pages;
    private IReadOnlyList<int> _unreadablePages = [];
    // Form field values and annotation text are shown from their own appearances, not the page content,
    // so the search also reads them from the document.
    private readonly Func<PdfDocument>? _loadDocument;
    private readonly Func<PdfRedactionSearchOptions, (IReadOnlyList<PdfRedactionMatch> Matches, IReadOnlyList<int> Unreadable)>? _searchCurrent;
    private PdfDocument? _document;

    /// <summary>The results the user left checked, in page order.</summary>
    internal IReadOnlyList<PdfRedactionMatch> Checked { get; private set; } = [];

    internal RedactionFindDialog(Window owner,
        Func<(IReadOnlyList<PdfPageContent?> Pages, IReadOnlyList<int> Unreadable)> loadPages,
        Func<PdfDocument>? loadDocument = null,
        Func<PdfRedactionSearchOptions, (IReadOnlyList<PdfRedactionMatch> Matches, IReadOnlyList<int> Unreadable)>? searchCurrent = null)
    {
        _loadPages = loadPages;
        _loadDocument = loadDocument;
        _searchCurrent = searchCurrent;
        Title = "KillerPDF - " + L("Str_RedactFind_Title");
        // Sized to its content: the kind list, a few lines of terms and a results list of page
        // numbers and short snippets, which never needed half a screen across (Steve, 2026-11-17).
        Width = 400;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.85);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(UiKit.GroupLabel(L("Str_RedactFind_Kind")));
        _kind = new ComboBox
        {
            ItemsSource = new[]
            {
                new KindChoice(L("Str_RedactFind_KindText"), PdfRedactionSearchKind.ExactText),
                new KindChoice(L("Str_RedactFind_KindRegex"), PdfRedactionSearchKind.RegularExpression),
                new KindChoice(L("Str_RedactFind_KindEmail"), PdfRedactionSearchKind.EmailAddress),
                new KindChoice(L("Str_RedactFind_KindPhone"), PdfRedactionSearchKind.PhoneNumber),
                new KindChoice(L("Str_RedactFind_KindSsn"), PdfRedactionSearchKind.SocialSecurityNumber),
                new KindChoice(L("Str_RedactFind_KindCard"), PdfRedactionSearchKind.PaymentCardNumber),
                new KindChoice(L("Str_RedactFind_KindIban"), PdfRedactionSearchKind.InternationalBankAccountNumber)
            },
            SelectedIndex = 0,
            Margin = new Thickness(0, 3, 0, 12)
        };
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _kind.Style = comboStyle;
        _kind.SelectionChanged += (_, _) => UpdateQueryState();
        body.Children.Add(_kind);

        _queryLabel = UiKit.GroupLabel(L("Str_RedactFind_Query"));
        body.Children.Add(_queryLabel);
        _query = UiKit.Field();
        _query.AcceptsReturn = true;
        _query.TextWrapping = TextWrapping.Wrap;
        _query.VerticalContentAlignment = VerticalAlignment.Top;
        _query.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _query.MinHeight = 58;
        _query.MaxHeight = 120;
        _query.Margin = new Thickness(0, 3, 0, 8);
        body.Children.Add(_query);

        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var search = UiKit.Make(L("Str_RedactFind_Search"), false);
        search.Click += (_, _) => RunSearch();
        DockPanel.SetDock(search, Dock.Right);
        searchRow.Children.Add(search);
        _matchCase = UiKit.CheckBox(L("Str_RedactFind_MatchCase"));
        _matchCase.VerticalAlignment = VerticalAlignment.Center;
        searchRow.Children.Add(_matchCase);
        body.Children.Add(searchRow);

        var resultsHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _checkLinks = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        _checkLinks.Children.Add(UiKit.LinkLabel(L("Str_RedactFind_CheckAll"), () => SetAll(true)));
        var uncheck = UiKit.LinkLabel(L("Str_RedactFind_UncheckAll"), () => SetAll(false));
        uncheck.Margin = new Thickness(12, 0, 0, 0);
        _checkLinks.Children.Add(uncheck);
        DockPanel.SetDock(_checkLinks, Dock.Right);
        resultsHeader.Children.Add(_checkLinks);
        _summary = UiKit.GroupLabel(string.Empty);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        resultsHeader.Children.Add(_summary);
        body.Children.Add(resultsHeader);

        _list = new StackPanel();
        var results = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = UiKit.Brush("CardBorderBrush"),
            Padding = new Thickness(6, 4, 6, 4),
            MinHeight = 120,
            Child = new ScrollViewer
            {
                Content = _list,
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        body.Children.Add(results);

        // A page left out of the search must never read as a page without the term.
        _skippedPages = new TextBlock
        {
            Foreground = UiKit.Brush("TextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        body.Children.Add(_skippedPages);

        body.Children.Add(new TextBlock
        {
            Text = L("Str_RedactFind_Hint"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10)
        });

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _mark = UiKit.Make(L("Str_RedactFind_Mark"), true);
        _mark.IsEnabled = false;
        _mark.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, _mark));

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        UpdateQueryState();
        Loaded += (_, _) => _query.Focus();
    }

    private PdfRedactionSearchKind SelectedKind => ((KindChoice)_kind.SelectedItem).Kind;

    // Common patterns need no text, and their expressions are written case-insensitive.
    private void UpdateQueryState()
    {
        PdfRedactionSearchKind kind = SelectedKind;
        bool typed = kind is PdfRedactionSearchKind.ExactText or PdfRedactionSearchKind.RegularExpression;
        _query.IsEnabled = typed;
        _queryLabel.Opacity = typed ? 1 : 0.5;
        _queryLabel.Text = L(kind == PdfRedactionSearchKind.RegularExpression
            ? "Str_RedactFind_KindRegex" : "Str_RedactFind_Query");
        _matchCase.IsEnabled = typed;
        if (!typed) _matchCase.IsChecked = false;
    }

    private void RunSearch()
    {
        PdfRedactionSearchKind kind = SelectedKind;
        // Each line is its own word or phrase; a regular expression is one expression.
        string[] queries = kind switch
        {
            PdfRedactionSearchKind.ExactText => [.. _query.Text.Split('\n')
                .Select(line => line.Trim()).Where(line => line.Length > 0).Distinct()],
            PdfRedactionSearchKind.RegularExpression => _query.Text.Trim().Length > 0 ? [_query.Text.Trim()] : [],
            _ => [string.Empty]
        };
        if (queries.Length == 0)
        {
            _query.Focus();
            return;
        }

        var found = new Dictionary<string, PdfRedactionMatch>();
        var skipped = new SortedSet<int>();
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (_pages is null) (_pages, _unreadablePages) = _loadPages();
            skipped.UnionWith(_unreadablePages);
            if (_loadDocument is not null) _document ??= _loadDocument();
            foreach (string query in queries)
            {
                var options = new PdfRedactionSearchOptions
                {
                    Kind = kind,
                    Query = query,
                    MatchCase = _matchCase.IsChecked == true,
                    Timeout = TimeSpan.FromSeconds(2)
                };
                if (_searchCurrent is not null)
                {
                    var current = _searchCurrent(options);
                    foreach (var match in current.Matches) found.TryAdd(match.Id, match);
                    skipped.UnionWith(current.Unreadable);
                    continue;
                }
                foreach (PdfRedactionMatch match in PdfRedactionSearch.Find(_pages, options).Matches)
                    found.TryAdd(match.Id, match);
                if (_document is null) continue;
                PdfRedactionReview annotations = PdfRedactionSearch.FindInAnnotations(_document, options);
                foreach (PdfRedactionMatch match in annotations.Matches)
                    found.TryAdd(match.Id, match);
                skipped.UnionWith(annotations.UnreadableAnnotationPages);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowResults([]);
            ShowSkippedPages([]);
            _summary.Text = string.Format(L("Str_RedactFind_Error"), ex.Message);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ShowResults([.. found.Values
            .OrderBy(match => match.PageIndex)
            .ThenByDescending(match => match.Bounds.Top)
            .ThenBy(match => match.Bounds.Left)]);
        ShowSkippedPages(skipped);
    }

    private void ShowSkippedPages(IReadOnlyCollection<int> pages)
    {
        _skippedPages.Text = pages.Count == 0 ? string.Empty
            : string.Format(L("Str_Search_PagesSkipped"), Services.SearchService.SkippedPagesText(pages));
        _skippedPages.Visibility = pages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowResults(IReadOnlyList<PdfRedactionMatch> matches)
    {
        _rows.Clear();
        _list.Children.Clear();
        foreach (PdfRedactionMatch match in matches)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            CheckBox include = UiKit.CheckBox(string.Format(L("Str_RedactFind_Page"), match.PageIndex + 1));
            include.IsChecked = true;
            include.Checked += (_, _) => UpdateSummary();
            include.Unchecked += (_, _) => UpdateSummary();
            var text = new TextBlock
            {
                Text = match.Text,
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                Foreground = UiKit.Brush("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = match.Text
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(include);
            row.Children.Add(text);
            _list.Children.Add(row);
            _rows.Add((match, include));
        }
        _checkLinks.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int pages = _rows.Select(row => row.Match.PageIndex).Distinct().Count();
        _summary.Text = _rows.Count == 0
            ? L("Str_RedactFind_None")
            : string.Format(L("Str_RedactFind_Results"), _rows.Count, pages);
        _mark.IsEnabled = _rows.Any(row => row.Include.IsChecked == true);
    }

    private void SetAll(bool value)
    {
        foreach (var (_, include) in _rows) include.IsChecked = value;
    }

    private void Commit()
    {
        Checked = [.. _rows.Where(row => row.Include.IsChecked == true).Select(row => row.Match)];
        if (Checked.Count == 0) return;
        DialogResult = true;
        Close();
    }

    private sealed record KindChoice(string Label, PdfRedactionSearchKind Kind)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
