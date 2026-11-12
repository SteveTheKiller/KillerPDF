using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF;

/// <summary>
/// Delete Pages in one step: the range (prefilled from the selection), a strip that marks exactly which pages go, and
/// a button that says what it will do. It replaces the range prompt followed by a separate Yes/No box. A run of
/// neighbouring pages is one stacked tile, so the strip has a tile per group and never draws the whole document; past
/// <see cref="MaxTiles"/> groups the rest are counted on one last tile. The counts always cover every page.
/// </summary>
internal sealed class DeletePagesDialog : Window
{
    internal const int MaxTiles = 40;

    private readonly int _pageCount;
    private readonly Func<int, PageThumbnailVm?> _row;
    private readonly string _undoGesture;
    private readonly TextBox _range;
    private readonly TextBlock _problem;
    private readonly StackPanel _strip;
    private readonly TextBlock _summary;
    private readonly Button _delete;
    private DeletePagesPlan _plan;

    /// <summary>The pages to delete, zero-based and sorted, once the dialog returns true.</summary>
    internal IReadOnlyList<int> Pages => _plan.Pages;

    internal DeletePagesDialog(Window owner, int pageCount, string initialRange, Func<int, PageThumbnailVm?> row, string undoGesture)
    {
        _pageCount = pageCount;
        _undoGesture = undoGesture;
        _row = row;
        _plan = DeletePagesPlan.From([], pageCount);
        Title = "KillerPDF - " + L("Str_Lbl_Delete");
        Width = 580;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        string rangeLabel = string.Format(L("Str_Dlg_PageRange"), pageCount);
        body.Children.Add(UiKit.GroupLabel(rangeLabel));
        _range = UiKit.Field();
        _range.Text = initialRange;
        _range.Margin = new Thickness(0, 5, 0, 4);
        System.Windows.Automation.AutomationProperties.SetName(_range, rangeLabel);
        body.Children.Add(_range);
        _problem = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");
        body.Children.Add(_problem);

        _strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 6) };
        body.Children.Add(new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        });

        _summary = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(_summary);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _delete = UiKit.Make(L("Str_Lbl_Delete"), accent: true);
        _delete.IsDefault = true;
        _delete.Click += (_, _) => { if (_delete.IsEnabled) DialogResult = true; };
        var buttons = UiKit.ButtonRow(cancel, _delete);
        buttons.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(buttons);

        _range.TextChanged += (_, _) => Replan();
        Replan();
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => { _range.Focus(); _range.SelectAll(); };
    }

    private void Replan()
    {
        List<int>? parsed = string.IsNullOrWhiteSpace(_range.Text)
            ? [] : Features.CliRunner.CliParsePageRange(_range.Text, _pageCount, out _);
        _plan = DeletePagesPlan.From(parsed ?? [], _pageCount);
        string? problem = _plan.Problem(parsed is not null) switch
        {
            DeletePagesProblem.InvalidRange => L("Str_InvalidRange"),
            DeletePagesProblem.KeepOnePage => L("Str_Dlg_KeepOnePage"),
            _ => null
        };
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        bool ok = problem is null;
        _delete.IsEnabled = ok;
        _delete.Content = ok ? Text.Button(_plan.Pages.Count) : L("Str_Lbl_Delete");
        _summary.Text = ok ? Text.Summary(_plan.Pages.Count, _plan.Remaining, _undoGesture) : "";
        _summary.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        BuildStrip(ok);
    }

    private void BuildStrip(bool ok)
    {
        _strip.Children.Clear();
        if (!ok) return;
        foreach (var (first, last) in _plan.Groups.Take(MaxTiles))
            _strip.Children.Add(Tile(first, last));
        int more = _plan.PagesAfter(MaxTiles);
        if (more > 0) _strip.Children.Add(MoreTile(more));
    }

    private FrameworkElement Tile(int first, int last)
    {
        bool run = last > first;
        var picture = new Grid { Width = 64, Height = 82 };
        if (run)
        {
            picture.Children.Add(Sheet(8, -6, 0.35));
            picture.Children.Add(Sheet(4, -3, 0.5));
        }
        var face = new Border
        {
            Width = 58, Height = 76, Background = Brushes.White, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.6
        };
        face.SetResourceReference(Border.BorderBrushProperty, "DangerRed");
        var image = new Image { Stretch = Stretch.Uniform };
        // The sidebar row's own picture: reading it asks the page list to draw it if it has not yet.
        if (_row(first) is { } vm) image.SetBinding(Image.SourceProperty, new Binding(nameof(PageThumbnailVm.Thumbnail)) { Source = vm });
        face.Child = image;
        picture.Children.Add(face);

        var label = new TextBlock
        {
            Text = run ? Text.Run(first + 1, last + 1) : string.Format(L("Str_PageLabel"), first + 1),
            FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed");
        var tile = new StackPanel { Width = 76, Margin = new Thickness(0, 0, 10, 0) };
        tile.Children.Add(picture);
        tile.Children.Add(label);
        if (run)
        {
            var count = new TextBlock { Text = Text.RunCount(last - first + 1), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            tile.Children.Add(count);
        }
        System.Windows.Automation.AutomationProperties.SetName(tile, label.Text);
        return tile;
    }

    private static Border Sheet(double left, double top, double opacity)
    {
        var sheet = new Border
        {
            Width = 58, Height = 76, Background = Brushes.White, BorderThickness = new Thickness(1), Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(left, 0, 0, -top)
        };
        sheet.SetResourceReference(Border.BorderBrushProperty, "DangerRed");
        return sheet;
    }

    private static FrameworkElement MoreTile(int more)
    {
        var text = new TextBlock { Text = Text.More(more), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return new Border { Height = 82, VerticalAlignment = VerticalAlignment.Top, Child = text };
    }

    /// <summary>The dialog's wording, kept apart so it can be checked without a window.</summary>
    internal static class Text
    {
        internal static string Button(int pages) => pages == 1 ? L("Str_Del_Btn1") : string.Format(L("Str_Del_BtnN"), pages);
        // The undo key comes from the shortcut table, so a remap (or a keyboard that says Strg) reads right.
        internal static string Summary(int going, int staying, string undo) => string.Format(L("Str_Del_Summary"), going, staying, undo);
        internal static string Run(int first, int last) => string.Format(L("Str_Del_Run"), first, last);
        internal static string RunCount(int pages) => string.Format(L("Str_Del_RunCount"), pages);
        internal static string More(int pages) => string.Format(L("Str_Del_More"), pages);
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
