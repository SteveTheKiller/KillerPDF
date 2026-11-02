using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPDF.Controls;
using KillerPDF.Services;
using KillerPDF.Shell;

namespace KillerPDF;

internal sealed class ParagraphEditDialog : Window
{
    private readonly PdfDocument _document;
    private readonly int _pageIndex;
    private readonly PdfPageInformation _page;
    private readonly IReadOnlyList<PdfParagraphCandidate> _candidates;
    private readonly IReadOnlyList<PdfParagraphInspection> _refusals;
    private readonly ComboBox _candidateBox;
    private readonly TextBox _text;
    private readonly TextBox[] _numbers = new TextBox[6];
    private readonly ComboBox _alignment;
    private readonly TextBlock _status;
    private readonly TextBlock _fontLabel;
    private readonly Image _image = new();
    private readonly Canvas _overlay;
    private readonly Rectangle _box = new() { StrokeThickness = 3, IsHitTestVisible = false };
    private readonly Thumb _resize;
    private readonly CheckBox _original;
    private readonly Button _preview;
    private readonly Button _apply;
    private readonly double _pixelsPerPoint;
    private BitmapSource? _originalImage;
    private BitmapSource? _editedImage;
    private PdfParagraphPlan? _plan;
    private CancellationTokenSource? _operation;
    private bool _setting;
    // The box follows the text and the font until someone resizes it.
    private bool _boxEdited;
    private bool _closed;

    internal PdfParagraphPlan? Result { get; private set; }

    internal ParagraphEditDialog(Window owner, PdfDocument document, int pageIndex, double scale, Point? pagePoint = null)
        : this(owner, document, pageIndex, scale, pagePoint, PdfParagraphEditor.InspectCandidates(document, pageIndex)) { }

    internal ParagraphEditDialog(Window owner, PdfDocument document, int pageIndex, double scale, Point? pagePoint,
        PdfParagraphCandidateInspection inspection)
    {
        _document = document; _pageIndex = pageIndex;
        _page = PdfPageInformation.Read(document)[pageIndex];
        _pixelsPerPoint = Math.Min(1000 / _page.Width, 1000 / _page.Height);
        _candidates = inspection.Candidates;
        _refusals = inspection.Refusals;
        Title = "KillerPDF - " + L("Str_Paragraph_Title");
        scale = Math.Clamp(scale, .7, 2.5);
        Width = Math.Min(980 * scale, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(720 * scale, SystemParameters.WorkArea.Height - 40);
        MinWidth = ScaledDialogFit.Minimum(640, 320, scale, SystemParameters.WorkArea.Width - 40);
        MinHeight = ScaledDialogFit.Minimum(460, 280, scale, SystemParameters.WorkArea.Height - 40);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        Id(this, "ParagraphEditDialog");
        var root = new DockPanel { LayoutTransform = new ScaleTransform(scale, scale) };

        var sidebar = new Border { Width = 288, Background = Brushes.Transparent, Padding = new Thickness(16, 8, 0, 14) };
        DockPanel.SetDock(sidebar, Dock.Right);
        var side = new DockPanel();
        var footer = new StackPanel { Margin = new Thickness(0, 10, 16, 0) };
        _status = Note(""); _status.FontSize = 12; Id(_status, "ParagraphStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _fontLabel = Note(""); _fontLabel.FontSize = 12;
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0) };
        _preview = Button("Str_Dlg_Preview", "ParagraphPreview", false);
        _preview.Click += async (_, _) => await Preview();
        var cancel = Button("Str_Btn_Cancel", "ParagraphCancel", false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        _apply = Button("Str_Tf_Apply", "ParagraphApply", true);
        _apply.IsEnabled = false;
        _apply.Click += (_, _) => { if (_plan is not null && _operation is null) { Result = _plan; Close(); } };
        actions.Children.Add(_preview); actions.Children.Add(cancel); actions.Children.Add(_apply);
        footer.Children.Add(_status); footer.Children.Add(_fontLabel); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); side.Children.Add(footer);

        var content = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        TextBlock hint = Note("Str_Paragraph_Hint"); hint.FontSize = 12;
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        content.Children.Add(hint);
        _candidateBox = new ComboBox { Style = UiKit.DarkComboStyle, Margin = new Thickness(0, 8, 0, 8),
            ItemsSource = _candidates.Select((candidate, index) => $"{index + 1}. " +
                candidate.Text.Replace('\n', ' ')[..Math.Min(100, candidate.Text.Length)]).ToArray() };
        Id(_candidateBox, "ParagraphCandidates", "Str_Paragraph_Select");
        content.Children.Add(_candidateBox);
        content.Children.Add(UiKit.GroupLabel(L("Str_Lbl_Text")));
        _text = UiKit.Field(); _text.AcceptsReturn = true; _text.TextWrapping = TextWrapping.Wrap;
        _text.Height = 200; _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.MaxLength = 10000; _text.VerticalContentAlignment = VerticalAlignment.Top;
        Id(_text, "ParagraphText", "Str_Lbl_Text"); content.Children.Add(_text);
        var fields = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, -8, 0) };
        string[] labels = ["Str_Pos_Left", "Str_Pos_Top", "Str_Sign_Width", "Str_Sign_Height", "Str_Stamp_FontSize", "Str_Paragraph_Leading"];
        string[] names = ["Left", "Top", "Width", "Height", "FontSize", "Leading"];
        for (int i = 0; i < _numbers.Length; i++)
        {
            var field = new StackPanel { Margin = new Thickness(0, 2, 8, 4) };
            field.Children.Add(UiKit.GroupLabel(L(labels[i])));
            TextBox input = UiKit.Field(); _numbers[i] = input;
            Id(input, "Paragraph" + names[i], labels[i]);
            bool boxField = i < 4;
            input.TextChanged += (_, _) => { if (!_setting) { _boxEdited |= boxField; Invalidate(); } };
            field.Children.Add(input); fields.Children.Add(field);
        }
        var alignmentField = new StackPanel { Margin = new Thickness(0, 2, 8, 4) };
        alignmentField.Children.Add(UiKit.GroupLabel(L("Str_Paragraph_Alignment")));
        _alignment = new ComboBox { Style = UiKit.DarkComboStyle,
            ItemsSource = new[] { L("Str_Paragraph_Left"), L("Str_Paragraph_Center"), L("Str_Paragraph_Right") }, SelectedIndex = 0 };
        Id(_alignment, "ParagraphAlignment", "Str_Paragraph_Alignment");
        alignmentField.Children.Add(_alignment); fields.Children.Add(alignmentField); content.Children.Add(fields);
        var sideScroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        sideScroll.SizeChanged += (_, _) => _text.Height = ScaledDialogFit.FieldHeight(200, 80, sideScroll.ActualHeight);
        side.Children.Add(sideScroll);
        sidebar.Child = side; root.Children.Add(sidebar);

        var pageColumn = new DockPanel();
        _original = UiKit.CheckBox(L("Str_Paragraph_Original"));
        _original.IsChecked = true; _original.Margin = new Thickness(8, 0, 8, 12);
        Id(_original, "ParagraphOriginal");
        _original.Checked += (_, _) => _image.Source = _originalImage;
        _original.Unchecked += (_, _) => _image.Source = _editedImage ?? _originalImage;
        DockPanel.SetDock(_original, Dock.Bottom); pageColumn.Children.Add(_original);
        var pageWrap = new Border { BorderThickness = new Thickness(1), CornerRadius = UiKit.RadCard,
            Margin = new Thickness(8, 4, 8, 8), ClipToBounds = true };
        pageWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        pageWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        var pageGrid = new Grid();
        if ((owner as MainWindow)?.GrainTextureSource is ImageSource grain)
            pageGrid.Children.Add(new Border { IsHitTestVisible = false,
                Opacity = Application.Current.Resources["GrainOpacity"] is double grainOpacity ? grainOpacity : 0.05,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None } });
        double pixelWidth = Math.Ceiling(_page.Width * _pixelsPerPoint);
        double pixelHeight = Math.Ceiling(_page.Height * _pixelsPerPoint);
        var pageView = new Grid { Width = pixelWidth, Height = pixelHeight, Background = Brushes.White };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _image.Stretch = Stretch.Fill; pageView.Children.Add(_image);
        _overlay = new Canvas { Background = Brushes.Transparent };
        Id(_overlay, "ParagraphPage", "Str_Paragraph_Select");
        _box.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
        _overlay.Children.Add(_box);
        _resize = new Thumb { Width = 20, Height = 20, Cursor = Cursors.SizeNWSE,
            Background = UiKit.Brush("PrimaryBrush"), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        Id(_resize, "ParagraphResize", "Str_Paragraph_Hint");
        _overlay.Children.Add(_resize); pageView.Children.Add(_overlay);
        pageGrid.Children.Add(new Viewbox { Child = pageView, Stretch = Stretch.Uniform, Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 } });
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            Point point = e.GetPosition(_overlay);
            SelectAt(new Point(point.X / _pixelsPerPoint, _page.Height - point.Y / _pixelsPerPoint));
            e.Handled = true;
        };
        _resize.DragDelta += (_, e) =>
        {
            if (!TryNumbers(out double[] values)) return;
            double availableWidth = Math.Max(0, _page.Width - values[0]);
            double availableHeight = Math.Max(0, _page.Height - values[1]);
            values[2] = Math.Clamp(values[2] + e.HorizontalChange / _pixelsPerPoint, Math.Min(1, availableWidth), availableWidth);
            values[3] = Math.Clamp(values[3] + e.VerticalChange / _pixelsPerPoint, Math.Min(1, availableHeight), availableHeight);
            _boxEdited = true; SetNumbers(values); Invalidate();
        };
        pageWrap.Child = pageGrid;
        pageColumn.Children.Add(UiKit.PaneWithShadow(pageWrap));
        root.Children.Add(pageColumn);
        Content = DialogFrame.Frame(this, owner, Title, Close, root);
        _candidateBox.SelectionChanged += (_, _) => SelectCandidate();
        _text.TextChanged += (_, _) => { if (!_setting) Invalidate(); };
        _alignment.SelectionChanged += (_, _) => { if (!_setting) Invalidate(); };
        _candidateBox.SelectedIndex = _candidates.Count > 0 ? 0 : -1;
        if (_candidates.Count == 0) _status.Text = RefusalMessage(inspection);
        if (pagePoint is Point pointOnPage) SelectAt(pointOnPage);
        _preview.IsEnabled = _candidates.Count > 0;
        _text.IsEnabled = _candidates.Count > 0;
        Loaded += async (_, _) =>
        {
            try { _originalImage = await Task.Run(() => Render(_document, CancellationToken.None));
                if (!_closed) { _image.Source = _original.IsChecked == true ? _originalImage : _editedImage ?? _originalImage; _text.Focus(); } }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!_closed) { _status.Text = L("Str_Paragraph_Unsupported"); _preview.IsEnabled = false; } }
        };
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
    }

    private void SelectAt(Point point)
    {
        int index = _candidates.ToList().FindIndex(candidate => point.X >= candidate.SourceRegion.Left
            && point.X <= candidate.SourceRegion.Right && point.Y >= candidate.SourceRegion.Bottom
            && point.Y <= candidate.SourceRegion.Top);
        if (index >= 0) { _candidateBox.SelectedIndex = index; return; }
        // A click on a paragraph the page refused would otherwise leave another paragraph selected with no word about it.
        PdfParagraphInspection? refused = _refusals.FirstOrDefault(item => item.Region is { } area
            && point.X >= area.Left && point.X <= area.Right && point.Y >= area.Bottom && point.Y <= area.Top);
        if (refused is not null)
            _status.Text = string.Format(L("Str_Paragraph_Refused"), refused.Reason);
    }

    private void SelectCandidate()
    {
        int index = _candidateBox.SelectedIndex;
        if (index < 0 || index >= _candidates.Count) return;
        PdfParagraphCandidate candidate = _candidates[index];
        _setting = true; _text.Text = candidate.Text; _alignment.SelectedIndex = 0; _setting = false;
        _boxEdited = false;
        ShowBox(PdfParagraphEditor.DefaultBox(candidate, _page.Width, _page.Height), candidate.FontSize, candidate.Leading);
        Invalidate();
    }

    private void ShowBox(PdfContentBounds box, double fontSize, double leading) =>
        SetNumbers([box.Left, _page.Height - box.Top, box.Right - box.Left, box.Top - box.Bottom, fontSize, leading]);

    private void SetNumbers(double[] values)
    {
        _setting = true;
        for (int i = 0; i < values.Length; i++) _numbers[i].Text = values[i].ToString("0.###", CultureInfo.CurrentCulture);
        _setting = false;
    }

    // A field shows three decimals, so a value left alone means the source's exact number.
    private double Unrounded(int field, double source)
    {
        string shown = source.ToString("0.###", CultureInfo.CurrentCulture);
        return _numbers[field].Text == shown ? source : double.Parse(_numbers[field].Text, NumberStyles.Float, CultureInfo.CurrentCulture);
    }

    private bool TryNumbers(out double[] values)
    {
        values = new double[6];
        for (int i = 0; i < values.Length; i++)
            if (!double.TryParse(_numbers[i].Text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i])
                || !double.IsFinite(values[i]) || values[i] < 0 || i >= 2 && values[i] <= 0) return false;
        return values[0] + values[2] <= _page.Width + .001 && values[1] + values[3] <= _page.Height + .001;
    }

    private void Invalidate()
    {
        _operation?.Cancel(); _plan = null; _apply.IsEnabled = false; _editedImage = null;
        _image.Source = _originalImage; _original.IsChecked = true; _fontLabel.Text = "";
        _status.Text = HyphenNote();
        PlaceBox();
    }

    // A word joined on a guess stays listed until someone has reworded it.
    private string HyphenNote()
    {
        if (_candidateBox.SelectedIndex < 0 || _candidateBox.SelectedIndex >= _candidates.Count) return "";
        string[] words = [.. _candidates[_candidateBox.SelectedIndex].HyphenGuesses.Distinct()
            .Where(word => _text.Text.Contains(word, StringComparison.Ordinal))];
        return words.Length == 0 ? "" : string.Format(L("Str_Paragraph_HyphensToCheck"), string.Join(", ", words));
    }

    private void PlaceBox()
    {
        bool valid = TryNumbers(out double[] values);
        _box.Visibility = _resize.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        if (!valid) return;
        double left = values[0] * _pixelsPerPoint, top = values[1] * _pixelsPerPoint;
        _box.Width = values[2] * _pixelsPerPoint; _box.Height = values[3] * _pixelsPerPoint;
        Canvas.SetLeft(_box, left); Canvas.SetTop(_box, top);
        Canvas.SetLeft(_resize, left + _box.Width - _resize.Width / 2); Canvas.SetTop(_resize, top + _box.Height - _resize.Height / 2);
    }

    private async Task Preview()
    {
        if (_operation is not null || _candidateBox.SelectedIndex < 0) return;
        if (!TryNumbers(out double[] values)) { _status.Text = L("Str_Paragraph_Overflow"); return; }
        PdfParagraphCandidate candidate = _candidates[_candidateBox.SelectedIndex];
        values[4] = Unrounded(4, candidate.FontSize); values[5] = Unrounded(5, candidate.Leading);
        var bounds = new PdfContentBounds(values[0], _page.Height - values[1] - values[3],
            values[0] + values[2], _page.Height - values[1]);
        string text = _text.Text;
        PdfParagraphAlignment alignment = (PdfParagraphAlignment)Math.Clamp(_alignment.SelectedIndex, 0, 2);
        var cts = new CancellationTokenSource(); _operation = cts;
        CancellationToken token = cts.Token;
        _preview.IsEnabled = _apply.IsEnabled = false; _status.Text = L("Str_Paragraph_Preparing");
        try
        {
            bool fit = !_boxEdited;
            var result = await Task.Run<(PdfParagraphPlan? Plan, string Family, BitmapSource? Image)>(() =>
            {
                token.ThrowIfCancellationRequested();
                if (PdfEngineIntegration.LoadParagraphFont(candidate, text) is not { } font) return (null, "", null);
                // Fitting works from the exact box, since rounded fields would move the text a little.
                PdfParagraphPlan plan = fit
                    ? PdfParagraphEditor.PlanFitted(_document, _pageIndex, candidate, _page.Width, _page.Height, text, font.Font, values[4], values[5], alignment)
                    : PdfParagraphEditor.Plan(_document, _pageIndex, candidate.SourceRegion, bounds, text, font.Font, values[4], values[5], alignment);
                token.ThrowIfCancellationRequested();
                return (plan, font.Family, plan.HasOverflow ? null : Render(PdfDocument.Open(plan.GetPreviewDocument()), token));
            }, token);
            token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (result.Plan is not { } planned) { _status.Text = L("Str_Paragraph_Unsupported"); return; }
            if (planned.HasOverflow) { _status.Text = L("Str_Paragraph_Overflow"); return; }
            if (fit) { ShowBox(planned.TargetBounds, values[4], values[5]); PlaceBox(); }
            _plan = planned; _editedImage = result.Image;
            _original.IsChecked = false; _image.Source = _editedImage;
            string hyphens = HyphenNote();
            _status.Text = L("Str_Paragraph_Ready") + (candidate.MixedStyles ? "\n" + L("Str_Paragraph_MixedStyles") : "")
                + (planned.HorizontalScale < 1 ? "\n" + string.Format(L("Str_Paragraph_Narrowed"), Math.Round(planned.HorizontalScale * 100)) : "")
                + (hyphens.Length == 0 ? "" : "\n" + hyphens);
            _fontLabel.Text = string.Format(L("Str_Paragraph_Font"), result.Family);
            _apply.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        { if (!_closed) _status.Text = string.Format(L("Str_Paragraph_Refused"), error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_closed) _status.Text = L("Str_Paragraph_Unsupported"); }
        finally { _operation = null; cts.Dispose(); if (!_closed) _preview.IsEnabled = true; }
    }

    private BitmapSource Render(PdfDocument document, CancellationToken cancellation)
    {
        var renderer = new PdfPageRenderer(document, InstalledPdfFontResolver.Instance);
        var page = renderer.Render(_pageIndex, new PdfRenderOptions((int)Math.Ceiling(_page.Width * _pixelsPerPoint),
            (int)Math.Ceiling(_page.Height * _pixelsPerPoint)), cancellation);
        PdfRenderCompleteness.RequireComplete(_pageIndex, PdfRenderCompleteness.Classify(page.Diagnostics));
        BitmapSource bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32,
            null, page.Pixels.ToArray(), page.Width * 4);
        bitmap.Freeze(); return bitmap;
    }

    private static Button Button(string key, string id, bool accent)
    {
        Button button = UiKit.Make(L(key), accent);
        button.MinHeight = 30; button.Padding = new Thickness(6, 3, 6, 3);
        button.Margin = new Thickness(8, 0, 0, 0); Id(button, id); return button;
    }
    private static TextBlock Note(string key)
    {
        var text = new TextBlock { Text = key.Length == 0 ? "" : L(key), FontFamily = UiKit.UiFont,
            FontSize = 13, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return text;
    }
    private static void Id(DependencyObject element, string id, string? label = null)
    { AutomationProperties.SetAutomationId(element, id); if (label is not null) AutomationProperties.SetName(element, L(label)); }
    internal static string RefusalText(PdfParagraphRefusalCode? code) => L(code switch
    {
        PdfParagraphRefusalCode.RotatedPage => "Str_Paragraph_Rotated",
        PdfParagraphRefusalCode.NoText => "Str_Paragraph_NoText",
        _ => "Str_Paragraph_Unsupported"
    });
    // The engine has already said why, so a page with no candidates reports that and not a general note.
    internal static string RefusalMessage(PdfParagraphCandidateInspection inspection) => string.Join("\n", inspection.Refusals.Select(item =>
        item.RefusalCode is PdfParagraphRefusalCode.RotatedPage or PdfParagraphRefusalCode.NoText || item.Reason.Length == 0 ? RefusalText(item.RefusalCode)
            : string.Format(L("Str_Paragraph_Refused"), item.Reason)).Distinct());
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
