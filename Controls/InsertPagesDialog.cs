using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF;

/// <summary>An open tab Insert Pages can take pages from, as its list shows it. A null page count is a tab not read yet.</summary>
internal sealed record InsertPagesSource(string Title, string? Path, int? PageCount);

/// <summary>
/// Insert Pages (issue 529): which open tab the pages come from, which of its pages (all, or pages X to Y), and
/// whether they go in before or after a page of the open document, starting on the page the card was opened on.
/// The last row of the list is files on disk, which hands over to the merge dialog at the chosen place. A choice that
/// cannot work is refused in place with the button off, as Extract Pages and Delete Pages do.
/// </summary>
internal sealed class InsertPagesDialog : Window
{
    private readonly IReadOnlyList<InsertPagesSource> _sources;
    private readonly InsertPagesCounts _counts;
    private readonly int _targetPageCount;
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, MaxHeight = 190 };
    private readonly RadioButton _all, _range, _before, _after;
    private readonly TextBox _from, _to, _beforePage, _afterPage;
    private readonly StackPanel _pages;
    private readonly TextBlock _summary, _problem;
    private readonly Button _insert;
    private readonly bool _ready;
    private bool _spanning, _closed;

    /// <summary>The chosen tab's place in the list it was given, or -1 for files on disk.</summary>
    internal int SourceIndex { get; private set; } = -1;
    internal bool FromDisk => SourceIndex < 0;
    /// <summary>Every page of the source, rather than <see cref="FirstPage"/> to <see cref="LastPage"/>.</summary>
    internal bool AllPages { get; private set; } = true;
    /// <summary>The first and last source page, one-based, when <see cref="AllPages"/> is false.</summary>
    internal int FirstPage { get; private set; }
    internal int LastPage { get; private set; }
    /// <summary>The zero-based position in the open document the pages go in at.</summary>
    internal int InsertAt { get; private set; }

    internal InsertPagesDialog(Window owner, IReadOnlyList<InsertPagesSource> sources, int targetPageCount, int clickedPage)
    {
        _sources = sources;
        _counts = new InsertPagesCounts(sources.Select(source => source.PageCount));
        Closed += (_, _) => _closed = true;
        _targetPageCount = targetPageCount;
        Title = "KillerPDF - " + L("Str_Ctx_InsertPages");
        // Fits "Pages [1] to [6]" and "After page [2]" side by side in English; longer languages widen it on load.
        Width = 520;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        Style? radioStyle = owner.TryFindResource("ThemeRadio") as Style;
        string clicked = Math.Clamp(clickedPage + 1, 1, Math.Max(1, targetPageCount)).ToString(CultureInfo.CurrentCulture);

        // 8 px from the window's sides to the boxes and the buttons.
        var body = new StackPanel { Margin = new Thickness(8, 6, 8, 16) };

        body.Children.Add(Label("Str_InsertPages_From", 0));
        foreach (InsertPagesSource source in sources)
            _list.Items.Add(Row(Document(), source.Title, PageCountText(source.PageCount), source.Path ?? source.Title));
        _list.Items.Add(Row(DialogParts.Glyph(0xE8B7, 16), L("Str_InsertPages_FromDisk"), L("Str_InsertPages_FromDiskHint"), null));
        System.Windows.Automation.AutomationProperties.SetName(_list, L("Str_InsertPages_From"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(_list, "InsertPagesSource");
        body.Children.Add(DialogParts.Well(_list));

        _from = Number("1", "InsertPagesFrom");
        _to = Number("1", "InsertPagesTo");
        _all = Choose("Str_InsertPages_All", "pages", radioStyle);
        _range = Choose("Str_InsertPages_Range", "pages", radioStyle, _from, _to);
        _all.IsChecked = true;
        // All pages has no number box, so it takes a box's height: every row in both boxes is then the same height.
        ((FrameworkElement)_all.Content).SetBinding(MinHeightProperty,
            new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = _from });
        _pages = Group(_all, _range);

        _beforePage = Number(clicked, "InsertPagesBeforePage");
        _afterPage = Number(clicked, "InsertPagesAfterPage");
        _before = Choose("Str_InsertPages_Before", "where", radioStyle, _beforePage);
        _after = Choose("Str_InsertPages_After", "where", radioStyle, _afterPage);
        _after.IsChecked = true;

        // Pages and Where sit side by side, each its own box under its own heading, as Insert from is. Both boxes
        // take the row's height, and the Pages box is as wide as its one-line range needs.
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DockPanel where = Section("Str_InsertPages_Where", Group(_before, _after));
        Grid.SetColumn(where, 2);
        columns.Children.Add(Section("Str_InsertPages_Pages", _pages));
        columns.Children.Add(where);
        body.Children.Add(columns);
        // A language whose choices run longer than the default width widens the window rather than wrapping them.
        Loaded += (_, _) =>
        {
            columns.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double extra = columns.DesiredSize.Width - columns.ActualWidth;
            if (extra > 0) Width += Math.Ceiling(extra);
        };

        // A typed number picks its own choice, and the two page boxes show one page, so switching keeps it.
        Follow(_from, _range);
        Follow(_to, _range);
        _ = new TwinPageBoxes(_beforePage, _before, _afterPage, _after, () => _ready && !_spanning, () => Replan());
        _list.SelectionChanged += (_, _) => { SpanWholeSource(); Replan(); };

        _summary = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0) };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_summary, "InsertPagesSummary");
        body.Children.Add(_summary);
        _problem = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");
        body.Children.Add(_problem);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _insert = UiKit.Make(L("Str_InsertPages_Insert"), accent: true);
        _insert.IsDefault = true;
        _insert.Click += (_, _) => { if (Replan()) DialogResult = true; };
        body.Children.Add(DialogParts.Footer([cancel, _insert]));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        _list.SelectedIndex = 0;
        _ready = true;
        Replan();
        Loaded += (_, _) =>
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) is ListBoxItem first) first.Focus();
            else _list.Focus();
        };
    }

    /// <summary>
    /// A background read of a tab's page count has finished: its row shows the count, the span shown while no range is
    /// picked widens to it, and the plan is checked again. A range the user has picked or is typing is left alone.
    /// Does nothing once the dialog has closed or for a source that already had a count.
    /// </summary>
    internal void SetPageCount(int index, int count)
    {
        if (_closed) return;
        if (!_counts.TryFill(index, count)) return;
        if (_list.Items[index] is ListBoxItem { Tag: TextBlock line }) line.Text = PageCountText(count);
        if (index == _list.SelectedIndex) SpanWholeSource();
        Replan();
    }

    private static TextBlock Label(string key, double top)
    {
        TextBlock label = UiKit.GroupLabel(L(key));
        label.Margin = new Thickness(0, top, 0, 5);
        return label;
    }

    private static StackPanel Group(params RadioButton[] choices)
    {
        // 8 all round inside the box: each choice adds its own 6 on top, so the top inset is 2.
        var group = new StackPanel { Margin = new Thickness(8, 2, 8, 8) };
        foreach (RadioButton choice in choices) group.Children.Add(choice);
        return group;
    }

    // A heading over its box, the box filling the rest of the row's height.
    private static DockPanel Section(string headingKey, FrameworkElement content)
    {
        var section = new DockPanel { LastChildFill = true };
        TextBlock heading = Label(headingKey, 12);
        DockPanel.SetDock(heading, Dock.Top);
        section.Children.Add(heading);
        section.Children.Add(DialogParts.Well(content));
        return section;
    }

    // The app's own document icon on every open PDF, as the tab and compare lists show it.
    private static FrameworkElement Document() =>
        ShellIcons.KillerPdfDocument() is { } icon
            ? new Image { Source = icon, Width = 18, Height = 18, Stretch = Stretch.Uniform }
            : DialogParts.Glyph(0xE8A5, 16);

    private static ListBoxItem Row(FrameworkElement icon, string name, string detail, string? tip)
    {
        icon.Margin = new Thickness(2, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        var line = new TextBlock { Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };
        // The second line takes the row's own foreground, faded, so it stays readable on the selection fill.
        line.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
        });
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(line);
        var row = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = line };
        if (tip is not null) item.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(item, name);
        return item;
    }

    private static string PageCountText(int? count) => count switch
    {
        null => L("Str_InsertPages_NotRead"),
        1 => L("Str_InsertPages_OnePage"),
        _ => string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_PageCount"), count)
    };

    // Until a range is picked, its boxes show the chosen file's whole span, so picking it starts from every page.
    private void SpanWholeSource()
    {
        int index = _list.SelectedIndex;
        if (_range.IsChecked == true || index < 0 || index >= _sources.Count) return;
        _spanning = true;
        _from.Text = "1";
        _to.Text = Math.Max(1, _counts[index] ?? 1).ToString(CultureInfo.CurrentCulture);
        _spanning = false;
    }

    private void Follow(TextBox box, RadioButton choice)
    {
        box.TextChanged += (_, _) => { if (_ready && !_spanning) choice.IsChecked = true; Replan(); };
        box.GotKeyboardFocus += (_, _) => choice.IsChecked = true;
    }

    // The sentence is split around its {0} and {1}, so each language places the numbers where it reads right.
    private RadioButton Choose(string formatKey, string group, Style? style, params TextBox[] boxes)
    {
        string format = L(formatKey);
        // One line, never wrapped: the window widens instead (see the constructor).
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (string piece in Regex.Split(format, @"(\{\d\})"))
        {
            if (Regex.Match(piece, @"^\{(\d)\}$") is { Success: true } slot)
            {
                int index = int.Parse(slot.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index < boxes.Length) line.Children.Add(boxes[index]);
            }
            else if (piece.Length > 0)
            {
                if (char.IsPunctuation(piece[0]) && line.Children.Count > 0 && line.Children[^1] is TextBox before)
                    before.Margin = new Thickness(before.Margin.Left, 0, 1, 0);
                line.Children.Add(new TextBlock { Text = piece, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        var radio = new RadioButton
        {
            Content = line,
            GroupName = group + GetHashCode().ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 6, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (style is not null) radio.Style = style;
        radio.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        string name = Regex.Replace(format, @"\{\d\}", "#");
        System.Windows.Automation.AutomationProperties.SetName(radio, name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(radio, formatKey);
        foreach (TextBox box in boxes) System.Windows.Automation.AutomationProperties.SetName(box, name);
        radio.Checked += (_, _) => Replan();
        return radio;
    }

    private static TextBox Number(string value, string automationId)
    {
        TextBox box = UiKit.Field(52);
        box.Text = value;
        box.Margin = new Thickness(5, 0, 5, 0);
        box.VerticalAlignment = VerticalAlignment.Center;
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    // Reads the choices, shows what the insert does or why it cannot, and says whether Insert may go ahead.
    private bool Replan()
    {
        if (!_ready) return false;
        int index = _list.SelectedIndex;
        bool disk = index < 0 || index >= _sources.Count;
        _pages.IsEnabled = !disk;
        _insert.Content = L(disk ? "Str_InsertPages_ChooseFiles" : "Str_InsertPages_Insert");

        bool before = _before.IsChecked == true;
        int? at = Whole(before ? _beforePage : _afterPage) is int page ? InsertPagesPlan.Position(page, before, _targetPageCount) : null;
        string? problem = at is null
            ? string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_BadPage"), _targetPageCount) : null;
        string? summary = null;
        bool all = _all.IsChecked == true;
        (int First, int Last)? span = null;
        InsertPagesSource? source = disk ? null : _sources[index];
        if (source is not null && !all)
        {
            span = Whole(_from) is int from && Whole(_to) is int to ? InsertPagesPlan.Span(from, to, _counts[index]) : null;
            if (span is null) problem ??= L("Str_InvalidRange");
        }
        if (problem is null && at is int position)
        {
            int? count = source is null ? null : all ? _counts[index] : span!.Value.Last - span.Value.First + 1;
            summary = source is null
                ? string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_SummaryDisk"), position + 1)
                : count is not int n ? string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_SummaryAll"), source.Title, position + 1)
                : n == 1 ? string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_Summary1"), source.Title, position + 1)
                : string.Format(CultureInfo.CurrentCulture, L("Str_InsertPages_SummaryN"), n, source.Title, position + 1, position + n);
        }
        _summary.Text = summary ?? "";
        _summary.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        _insert.IsEnabled = problem is null;
        if (problem is not null || at is not int place) return false;
        SourceIndex = disk ? -1 : index;
        AllPages = all;
        (FirstPage, LastPage) = span ?? (0, 0);
        InsertAt = place;
        return true;
    }

    private static int? Whole(TextBox box) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? value : null;

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
