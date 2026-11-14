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
        Width = 820;
        Height = 620;
        MinWidth = 560;
        MinHeight = 460;
        DialogFrame.Configure(this, owner, resizable: true);
        Closed += (_, _) => _session.Dispose();

        // The page on the left with its page strip under it, like Transform and Stamp; on the right, what the
        // plates mean over the ink list, and the footer with Report on its left and Close on its right.
        var hint = new TextBlock
        {
            Text = L("Str_Sep_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var report = DialogParts.LabeledAction("Str_Sep_Report", 0xE9F9, (_, _) => showReport());
        var close = UiKit.Make(L("Str_Lbl_Close"), accent: true);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var footer = DialogParts.Footer([close], report);

        _status = new TextBlock { FontFamily = UiKit.UiFont, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        _previous = PageNavButtons.Previous(() => ShowPage(_page - 1));
        _next = PageNavButtons.Next(() => ShowPage(_page + 1));
        _pageLabel = new TextBlock
        {
            FontFamily = UiKit.MonoFont,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pageLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        // The page strip under the frame, as Stamp's: 36 high, 32 x 26 arrows at the frame's edges.
        var pager = new Grid { Height = 36, Margin = new Thickness(8, 2, 8, 0) };
        pager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pager.ColumnDefinitions.Add(new ColumnDefinition());
        pager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_pageLabel, 1);
        Grid.SetColumn(_next, 2);
        pager.Children.Add(_previous);
        pager.Children.Add(_pageLabel);
        pager.Children.Add(_next);

        // The page: a framed pane with the window's grain, the plate drawn on white in its middle.
        var pane = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            Margin = new Thickness(8, 8, 8, _pageCount > 1 ? 0 : 8),
            ClipToBounds = true
        };
        pane.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        pane.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        var pageHost = new Grid();
        if ((owner as KillerPDF.Shell.MainWindow)?.GrainTextureSource is { } grain)
        {
            double opacity = Application.Current.Resources["GrainOpacity"] is double value ? value : 0.05;
            pageHost.Children.Add(new Border
            {
                IsHitTestVisible = false, Opacity = opacity,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        pageHost.Children.Add(new Border
        {
            Background = Brushes.White,
            Child = _image,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KillerPDF.Controls.PageWindowLayout.PageMargin)
        });
        pane.Child = pageHost;
        var pageColumn = new DockPanel();
        if (_pageCount > 1)
        {
            DockPanel.SetDock(pager, Dock.Bottom);
            pageColumn.Children.Add(pager);
        }
        pageColumn.Children.Add(UiKit.PaneWithShadow(pane));

        _list.SelectionChanged += (_, _) => RenderSelection();
        var side = new DockPanel { Width = 264, Margin = new Thickness(16, 8, 0, 0) };
        DockPanel.SetDock(hint, Dock.Top);
        side.Children.Add(hint);
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(footer);
        DockPanel.SetDock(_status, Dock.Bottom);
        side.Children.Add(_status);
        side.Children.Add(DialogParts.Well(_list));

        var body = new Grid { Margin = new Thickness(12, 0, 20, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(pageColumn);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);

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
