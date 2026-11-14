using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF;

/// <summary>What Extract Pages makes.</summary>
internal enum SplitChoice
{
    /// <summary>Pages from one number to another, in one PDF (the default).</summary>
    Range,
    /// <summary>A typed page list such as 1-3,7, in one PDF.</summary>
    List,
    /// <summary>Every page in its own PDF.</summary>
    EveryPage,
    /// <summary>A PDF for every few pages.</summary>
    EveryN
}

/// <summary>
/// Extract Pages (#484): one compact window with the four ways out, each a labeled choice with its own numbers in
/// the sentence, so "pages 2 to 5" and "every page" read as what they do. One PDF is the default and works as the
/// range prompt did; the choices that write several files also take the start of the file names, and the line under
/// them says how many files come out and what the first and last are called. A choice that cannot work is refused
/// in place, with the Extract button off, as Delete Pages does.
/// </summary>
internal sealed class SplitDialog : Window
{
    private readonly int _pageCount;
    private readonly RadioButton _range, _list, _everyPage, _everyN;
    private readonly TextBox _from, _to, _pages, _every, _baseName;
    private readonly TextBlock _summary, _problem;
    private readonly Button _extract;
    private readonly bool _ready;

    /// <summary>The choice, once the dialog returns true.</summary>
    internal SplitChoice Choice { get; private set; }
    /// <summary>The zero-based pages of a one-PDF choice, in page order.</summary>
    internal IReadOnlyList<int> Pages { get; private set; } = [];
    /// <summary>The pages in each file of the every-few-pages choice.</summary>
    internal int PagesPerFile { get; private set; } = 1;
    /// <summary>The start of the file names for a choice that writes several files.</summary>
    internal string BaseName { get; private set; } = "";

    internal SplitDialog(Window owner, string title, int pageCount, IReadOnlyCollection<int> selectedPages,
        string initialList, string baseName)
    {
        _pageCount = pageCount;
        Title = "KillerPDF - " + title;
        Width = 470;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        Style? radioStyle = owner.TryFindResource("ThemeRadio") as Style;
        var (from, to, contiguous) = PageSplit.Start(selectedPages, pageCount);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        _from = Number(from.ToString(CultureInfo.CurrentCulture), "SplitFrom");
        _to = Number(to.ToString(CultureInfo.CurrentCulture), "SplitTo");
        _pages = Box(initialList, 150, "SplitPageList");
        _every = Number("2", "SplitEvery");
        _range = Choose("Str_Split_Range", radioStyle, _from, _to);
        _list = Choose("Str_Split_List", radioStyle, _pages);
        _everyPage = Choose("Str_Split_EveryPage", radioStyle);
        _everyN = Choose("Str_Split_EveryN", radioStyle, _every);
        (contiguous ? _range : _list).IsChecked = true;
        foreach (RadioButton choice in new[] { _range, _list, _everyPage, _everyN }) body.Children.Add(choice);

        // A typed number or page list picks its own choice, so typing is enough.
        Follow(_from, _range);
        Follow(_to, _range);
        Follow(_pages, _list);
        Follow(_every, _everyN);

        var nameRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        TextBlock nameLabel = UiKit.GroupLabel(L("Str_Split_BaseName"));
        nameLabel.VerticalAlignment = VerticalAlignment.Center;
        nameLabel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        _baseName = Box(baseName, double.NaN, "SplitBaseName");
        System.Windows.Automation.AutomationProperties.SetName(_baseName, L("Str_Split_BaseName"));
        nameRow.Children.Add(_baseName);
        body.Children.Add(nameRow);
        _baseName.TextChanged += (_, _) => Replan();

        _summary = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_summary, "SplitSummary");
        body.Children.Add(_summary);
        _problem = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");
        body.Children.Add(_problem);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _extract = UiKit.Make(title, accent: true);
        _extract.IsDefault = true;
        _extract.Click += (_, _) => { if (Replan()) DialogResult = true; };
        body.Children.Add(DialogParts.Footer([cancel, _extract]));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        _ready = true;
        Replan();
        Loaded += (_, _) =>
        {
            TextBox first = contiguous ? _from : _pages;
            first.Focus();
            first.SelectAll();
        };
    }

    private void Follow(TextBox box, RadioButton choice)
    {
        box.TextChanged += (_, _) => { if (_ready) choice.IsChecked = true; Replan(); };
        box.GotKeyboardFocus += (_, _) => choice.IsChecked = true;
    }

    private RadioButton Choose(string formatKey, Style? style, params TextBox[] boxes)
    {
        string format = L(formatKey);
        // The sentence is split around its {0} and {1}, so each language places the numbers where it reads right.
        var line = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (string piece in Regex.Split(format, @"(\{\d\})"))
        {
            if (Regex.Match(piece, @"^\{(\d)\}$") is { Success: true } slot)
            {
                int index = int.Parse(slot.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index < boxes.Length) line.Children.Add(boxes[index]);
            }
            else if (piece.Length > 0)
                line.Children.Add(new TextBlock { Text = piece, VerticalAlignment = VerticalAlignment.Center });
        }
        var radio = new RadioButton
        {
            Content = line,
            GroupName = "split" + GetHashCode().ToString(CultureInfo.InvariantCulture),
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
        TextBox box = Box(value, 52, automationId);
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        return box;
    }

    private static TextBox Box(string value, double width, string automationId)
    {
        TextBox box = UiKit.Field(width);
        box.Text = value;
        box.Margin = new Thickness(5, 0, 5, 0);
        box.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    // Reads the choice, shows what it makes or why it cannot, and says whether Extract may go ahead.
    private bool Replan()
    {
        if (!_ready) return false;
        SplitChoice choice = _list.IsChecked == true ? SplitChoice.List
            : _everyPage.IsChecked == true ? SplitChoice.EveryPage
            : _everyN.IsChecked == true ? SplitChoice.EveryN : SplitChoice.Range;
        bool severalFiles = choice is SplitChoice.EveryPage or SplitChoice.EveryN;
        _baseName.IsEnabled = severalFiles;
        string? problem = null, summary = null;
        IReadOnlyList<int>? pages = null;
        int every = 1;
        string? name = null;
        switch (choice)
        {
            case SplitChoice.Range:
                pages = Whole(_from) is int first && Whole(_to) is int last ? PageSplit.FromTo(first, last, _pageCount) : null;
                break;
            case SplitChoice.List:
                pages = string.IsNullOrWhiteSpace(_pages.Text) ? null
                    : Features.CliRunner.CliParsePageRange(_pages.Text, _pageCount, out _);
                break;
            case SplitChoice.EveryN:
                every = Whole(_every) is int count && count > 0 ? count : 0;
                if (every == 0) problem = L("Str_Split_BadEvery");
                break;
        }
        if (!severalFiles)
        {
            if (pages is null) problem = L("Str_InvalidRange");
            else summary = string.Format(CultureInfo.CurrentCulture, L("Str_Split_OneSummary"), pages.Count);
        }
        else if (problem is null)
        {
            name = PageSplit.BaseName(_baseName.Text);
            if (name is null) problem = L("Str_Split_BadName");
            else
            {
                IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(
                    choice == SplitChoice.EveryPage ? PageSplitMode.EveryPage : PageSplitMode.EveryN, _pageCount, every, name);
                summary = parts.Count == 1
                    ? string.Format(CultureInfo.CurrentCulture, L("Str_Split_OneFileSummary"), parts[0].FileName)
                    : string.Format(CultureInfo.CurrentCulture, L("Str_Split_FilesSummary"), parts.Count, parts[0].FileName, parts[^1].FileName);
            }
        }
        _summary.Text = summary ?? "";
        _summary.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        _extract.IsEnabled = problem is null;
        if (problem is not null) return false;
        Choice = choice;
        Pages = pages ?? [];
        PagesPerFile = every;
        BaseName = name ?? "";
        return true;
    }

    private static int? Whole(TextBox box) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? value : null;

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
