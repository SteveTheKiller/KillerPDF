using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF;

// Separation preview (#264): each ink plate of a page on its own, drawn as the ink it puts down,
// black for full coverage and white for none, next to the page with all inks.
internal sealed class SeparationPreviewDialog : Window
{
    internal sealed record Plate(string Name, IReadOnlyCollection<int> Pages);

    private readonly PdfPageRenderSession _session;
    private readonly IReadOnlyList<Plate> _plates;
    private readonly int _pageCount;
    private readonly ListBox _list = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _pageLabel;
    private readonly TextBlock _status;
    private readonly Button _previous;
    private readonly Button _next;
    private int _page;

    internal SeparationPreviewDialog(Window owner, string file, OpenedProtection? protection, int pageCount, int startPage,
        IReadOnlyList<Plate> plates, Action showReport)
    {
        _session = PdfPageRenderSession.OpenEngineFirst(file, 900, 900, protection);
        _plates = plates;
        _pageCount = pageCount;
        _page = Math.Clamp(startPage, 0, Math.Max(0, pageCount - 1));
        Title = "KillerPDF - " + L("Str_DocumentTools_Separations");
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 460;
        DialogFrame.Configure(this, owner, resizable: true);
        Closed += (_, _) => _session.Dispose();

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16), LastChildFill = true };
        var hint = new TextBlock
        {
            Text = L("Str_Sep_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var report = UiKit.Make(L("Str_Sep_Report"), accent: false);
        report.Click += (_, _) => showReport();
        var close = UiKit.Make(L("Str_Lbl_Close"), accent: true);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = UiKit.ButtonRow(report, close);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);

        _status = new TextBlock { FontFamily = UiKit.UiFont, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);

        _previous = UiKit.Make(new Glyph { Text = "", FontFamily = UiKit.IconFont, FontSize = 12 }, accent: false);
        _previous.ToolTip = L("Str_Kb_PrevPage");
        _previous.Click += (_, _) => ShowPage(_page - 1);
        _next = UiKit.Make(new Glyph { Text = "", FontFamily = UiKit.IconFont, FontSize = 12 }, accent: false);
        _next.ToolTip = L("Str_Kb_NextPage");
        _next.Click += (_, _) => ShowPage(_page + 1);
        _pageLabel = new TextBlock
        {
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        _pageLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 4)
        };
        pager.Children.Add(_previous);
        pager.Children.Add(_pageLabel);
        pager.Children.Add(_next);
        DockPanel.SetDock(pager, Dock.Bottom);
        body.Children.Add(pager);

        _list.Width = 190;
        _list.Margin = new Thickness(0, 0, 12, 0);
        _list.SetResourceReference(Control.BackgroundProperty, "PaneBrush");
        _list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _list.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");
        _list.SelectionChanged += (_, _) => RenderSelection();
        DockPanel.SetDock(_list, Dock.Left);
        body.Children.Add(_list);

        var paper = new Border { Background = Brushes.White, Child = _image, BorderThickness = new Thickness(1) };
        paper.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        body.Children.Add(paper);

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        ShowPage(_page);
    }

    // The list names every plate; one with no ink on this page is shown muted.
    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, Math.Max(0, _pageCount - 1));
        _pageLabel.Text = string.Format(L("Str_PageOf"), _page + 1, _pageCount);
        _previous.IsEnabled = _page > 0;
        _next.IsEnabled = _page < _pageCount - 1;
        int selected = Math.Max(0, _list.SelectedIndex);
        _list.Items.Clear();
        _list.Items.Add(Item(L("Str_Sep_AllInks"), null, present: true));
        foreach (Plate plate in _plates)
            _list.Items.Add(Item(plate.Name, plate.Name, plate.Pages.Contains(_page)));
        _list.SelectedIndex = Math.Min(selected, _list.Items.Count - 1);
        RenderSelection();
    }

    private static ListBoxItem Item(string text, string? plate, bool present)
    {
        var item = new ListBoxItem { Content = text, Tag = plate, Padding = new Thickness(8, 4, 8, 4) };
        item.SetResourceReference(Control.ForegroundProperty, present ? "TextBrush" : "MutedTextBrush");
        return item;
    }

    private void RenderSelection()
    {
        if (_list.SelectedItem is not ListBoxItem item) return;
        try
        {
            PdfRenderedPage rendered = item.Tag is string plate
                ? _session.RenderPlate(_page, plate)
                : _session.RenderPage(_page);
            var bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96,
                PixelFormats.Bgra32, null, rendered.Pixels, rendered.Width * 4);
            bitmap.Freeze();
            _image.Source = bitmap;
            _status.Text = "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _image.Source = null;
            _status.Text = string.Format(L("Str_Sep_Failed"), ex.Message);
        }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
