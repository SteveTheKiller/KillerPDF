using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class FormRecognitionReviewDialog : Window
{
    private readonly PdfDocument _document;
    private readonly PdfFormRecognitionReview _review;
    private readonly List<(PdfFormFieldProposal Proposal, CheckBox Include, TextBox Name)> _rows = [];
    private readonly Canvas _preview = new();
    private readonly Dictionary<int, BitmapSource> _pages = [];
    private string? _shownProposalId;
    internal PdfFormRecognitionReview? Result { get; private set; }

    internal FormRecognitionReviewDialog(Window owner, PdfDocument document, PdfFormRecognitionReview review)
    {
        _document = document;
        _review = review;
        Title = "KillerPDF - " + L("Str_FormTools_Recognize");
        Width = 1000;
        Height = 660;
        MinWidth = 700;
        MinHeight = 420;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_AutoBookmarks_AddSelected"), accent: true);
        apply.IsDefault = true;
        apply.Click += (_, _) => Commit();
        var footer = DialogParts.Footer([cancel, apply]);

        // The page on the left in a framed pane like Transform; the proposals on the right in a well, over the footer.
        var pane = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            Margin = new Thickness(8),
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
        pageHost.Children.Add(new Viewbox
        {
            Child = _preview, Stretch = Stretch.Uniform,
            Margin = new Thickness(KillerPDF.Controls.PageWindowLayout.PageMargin)
        });
        pane.Child = pageHost;
        var columns = new Grid { Margin = new Thickness(12, 0, 20, 16) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        columns.Children.Add(UiKit.PaneWithShadow(pane));
        var list = new StackPanel();
        foreach (PdfFormFieldProposal proposal in review.Proposals)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var include = UiKit.CheckBox("");
            // Boxes the page already prints text in are likely filled in, so they start left out.
            include.IsChecked = !proposal.CoversPrintedText;
            System.Windows.Automation.AutomationProperties.SetName(include,
                L("Str_AutoBookmarks_Include") + " " + proposal.SuggestedName);
            var name = UiKit.Field();
            name.Text = proposal.SuggestedName;
            System.Windows.Automation.AutomationProperties.SetName(name, L("Str_FieldPreset_Name"));
            var page = new TextBlock
            {
                Text = (proposal.PageIndex + 1).ToString(),
                Foreground = UiKit.Brush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            page.ToolTip = L("Str_AutoBookmarks_Page");
            Grid.SetColumn(name, 1);
            Grid.SetColumn(page, 2);
            row.Children.Add(include);
            row.Children.Add(name);
            row.Children.Add(page);
            name.GotKeyboardFocus += (_, _) => ShowProposal(proposal);
            include.GotKeyboardFocus += (_, _) => ShowProposal(proposal);
            row.MouseEnter += (_, _) => ShowProposal(proposal);
            list.Children.Add(row);
            _rows.Add((proposal, include, name));
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var reviewPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        reviewPanel.Children.Add(footer);
        // The column headings line up with the rows inside the well's 3 DIP padding.
        var header = new Grid { Margin = new Thickness(3, 0, 3, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        string[] headingKeys = ["Str_AutoBookmarks_Include", "Str_FieldPreset_Name", "Str_AutoBookmarks_Page"];
        for (int index = 0; index < headingKeys.Length; index++)
        {
            var label = new TextBlock
            {
                Text = L(headingKeys[index]), ToolTip = L(headingKeys[index]),
                FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = index == 2 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Grid.SetColumn(label, index);
            header.Children.Add(label);
        }
        DockPanel.SetDock(header, Dock.Top);
        reviewPanel.Children.Add(header);
        reviewPanel.Children.Add(DialogParts.Well(scroll));
        Grid.SetColumn(reviewPanel, 2);
        columns.Children.Add(reviewPanel);
        Content = DialogFrame.Frame(this, owner, Title, Close, columns);
        Loaded += (_, _) =>
        {
            if (_rows.Count > 0) { _rows[0].Name.Focus(); ShowProposal(_rows[0].Proposal); }
        };
    }

    private void ShowProposal(PdfFormFieldProposal proposal)
    {
        if (_shownProposalId == proposal.Id) return;
        try { RenderProposal(proposal); _shownProposalId = proposal.Id; }
        catch (Exception error)
        {
            _preview.Children.Clear();
            _preview.Children.Add(new TextBlock
            {
                Text = L("Str_DocumentTools_Failed") + "\n" + error.Message,
                TextWrapping = TextWrapping.Wrap, Width = 500,
                Foreground = UiKit.Brush("TextBrush")
            });
        }
    }

    private void RenderProposal(PdfFormFieldProposal proposal)
    {
        PdfPageInformation page = PdfPageInformation.Read(_document)[proposal.PageIndex];
        if (!_pages.TryGetValue(proposal.PageIndex, out BitmapSource? bitmap))
        {
            bool sideways = page.Rotation % 180 != 0;
            double ratio = sideways ? page.Width / page.Height : page.Height / page.Width;
            var rendered = new PdfPageRenderer(_document).Render(proposal.PageIndex,
                KillerPDF.Services.WorkingCmykProfiles.Apply(new PdfRenderOptions(720, Math.Max(1, (int)Math.Round(720 * ratio)))));
            bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32,
                null, rendered.Pixels.ToArray(), rendered.Width * 4);
            bitmap.Freeze();
            if (_pages.Count >= 3) _pages.Clear();
            _pages[proposal.PageIndex] = bitmap;
        }
        _preview.Width = bitmap.PixelWidth;
        _preview.Height = bitmap.PixelHeight;
        _preview.Children.Clear();
        _preview.Children.Add(new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight });
        PdfContentBounds box = proposal.Bounds;
        Rect rect = RedactionGeometry.PageToCanvas(box.Left - page.Left, box.Bottom - page.Bottom,
            box.Right - page.Left, box.Top - page.Bottom, page.Width, page.Height,
            bitmap.PixelWidth, bitmap.PixelHeight, page.Rotation);
        var outline = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(2, rect.Width), Height = Math.Max(2, rect.Height),
            StrokeThickness = 3
        };
        outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SelectionAccent");
        Canvas.SetLeft(outline, rect.Left);
        Canvas.SetTop(outline, rect.Top);
        _preview.Children.Add(outline);
    }

    private void Commit()
    {
        var selected = _rows.Where(row => row.Include.IsChecked == true).ToArray();
        if (selected.Length == 0 || selected.Any(row => string.IsNullOrWhiteSpace(row.Name.Text))
            || selected.Select(row => row.Name.Text.Trim()).Distinct(StringComparer.Ordinal).Count() != selected.Length)
        {
            KillerDialog.Show(this, L("Str_FormTools_InvalidNames"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PdfFormRecognitionReview result = _review;
        foreach (var row in _rows)
            result = row.Include.IsChecked == true
                ? result.Accept(row.Proposal.Id, name: row.Name.Text.Trim()) : result.Reject(row.Proposal.Id);
        try { _ = result.ApplyAccepted(_document); }
        catch (Exception error)
        {
            KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + error.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = result;
        DialogResult = true;
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
