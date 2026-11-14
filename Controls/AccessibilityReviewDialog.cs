using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class AccessibilityReviewDialog : Window
{
    private byte[] _source;
    private PdfAccessibilityReport _report;
    private readonly bool _canRepair;
    private readonly Func<byte[], byte[]?> _applyFix;
    private readonly Func<byte[]?> _undoFix;
    private readonly Func<bool> _sourceCurrent;
    private readonly IReadOnlyDictionary<int, int>? _rotations;
    // The tab's protection: a tab that reads its encrypted original is reviewed under its key.
    private readonly OpenedProtection? _protection;
    private readonly ListBox _findings;
    private readonly DockPanel _listPanel;
    private readonly StackPanel _detail;
    private readonly ScrollViewer _detailScroll;
    private readonly Grid _columns;
    private bool? _wide;
    private readonly TextBox _input;
    private readonly TextBlock _count, _explanation, _label, _location, _status;
    private readonly Image _image;
    private readonly Grid _pageGrid;
    private readonly Canvas _overlay;
    private readonly Rectangle _highlight;
    private readonly Viewbox _pageView;
    private readonly Button _apply, _undo, _recheck, _readingOrder, _tableHeader;
    private CancellationTokenSource? _preview;
    private bool _busy, _closed, _stale;
    private int _fixes;
    private readonly HashSet<PdfAccessibilityFinding> _unsupported = [];
    internal string? RequestedTool { get; private set; }

    internal AccessibilityReviewDialog(Window owner, byte[] source, double scale, bool canRepair,
        Func<byte[], byte[]?> apply, Func<byte[]?> undo, Func<bool> sourceCurrent,
        IReadOnlyDictionary<int, int>? rotations = null, OpenedProtection? protection = null)
    {
        _source = source; _canRepair = canRepair; _applyFix = apply; _undoFix = undo;
        _sourceCurrent = sourceCurrent; _rotations = rotations; _protection = protection;
        _report = AccessibilityReviewSource.Inspect(source, protection);
        Title = "KillerPDF - " + L("Str_DocumentTools_Accessibility");
        scale = Math.Clamp(scale, .7, 2.5);
        Width = Math.Min(720 * scale, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(520 * scale, SystemParameters.WorkArea.Height - 40);
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MinWidth = 460; MinHeight = 360; UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        // It opens 720 DIP times the app scale wide and its content scrolls, so 460 x 360 stays its minimum rather than
        // the opening size, which at a large scale is most of the screen.
        DialogFrame.KeepDeclaredMinimum(this);
        Id(this, "AccessibilityReviewDialog");
        var body = new DockPanel { Margin = new Thickness(12, 4, 12, 12),
            LayoutTransform = new ScaleTransform(scale, scale) };

        // Close alone on the footer row; everything else sits beside the problem it acts on.
        Button close = UiKit.Make(L("Str_DocumentTools_Close"), false);
        Id(close, "A11yClose"); close.IsCancel = true; close.Click += (_, _) => Close();
        DockPanel footer = DialogParts.Footer([close]);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);

        // Left: the problems in a well, the remaining count as its heading, and the list-wide actions as icons.
        _undo = IconButton("Str_Lbl_Undo", 0xE7A7, "A11yUndo"); _undo.Click += (_, _) => Undo();
        _recheck = IconButton("Str_A11y_Recheck", 0xE72C, "A11yRecheck"); _recheck.Click += (_, _) => Recheck();
        Button copy = IconButton("Str_DocumentTools_Copy", 0xE8C8, "A11yCopy");
        DockPanel listHeader = DialogParts.Header("", _undo, _recheck, copy);
        _count = listHeader.Children.OfType<TextBlock>().First();
        _count.TextWrapping = TextWrapping.Wrap; Id(_count, "A11yCount");
        _findings = new ListBox();
        ScrollViewer.SetHorizontalScrollBarVisibility(_findings, ScrollBarVisibility.Disabled);
        Id(_findings, "A11yFindings", "Str_DocumentTools_Accessibility");
        _listPanel = new DockPanel();
        DockPanel.SetDock(listHeader, Dock.Top); _listPanel.Children.Add(listHeader);
        _listPanel.Children.Add(DialogParts.Well(_findings));

        // Right: the selected problem, the affected page and its fix.
        _detail = new StackPanel();
        _detail.Children.Add(Note("Str_A11y_Intro", muted: true));
        _explanation = Note(""); _explanation.Margin = new Thickness(0, 6, 0, 2); _detail.Children.Add(_explanation);
        _location = Note("", muted: true); Id(_location, "A11yLocation"); _detail.Children.Add(_location);
        _image = new Image { Stretch = Stretch.Uniform };
        Id(_image, "A11yPreview", "Str_Dlg_Preview");
        _pageGrid = new Grid(); _pageGrid.Children.Add(_image);
        _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true }; Id(_overlay, "A11yOverlay");
        _highlight = new Rectangle { StrokeThickness = 4, Visibility = Visibility.Collapsed };
        _highlight.SetResourceReference(Shape.StrokeProperty, "SelectionAccent"); Id(_highlight, "A11yHighlight");
        _overlay.Children.Add(_highlight); _pageGrid.Children.Add(_overlay);
        _pageView = new Viewbox { Child = _pageGrid, Stretch = Stretch.Uniform, MaxHeight = 230,
            Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
        _detail.Children.Add(_pageView);
        _label = UiKit.GroupLabel(""); _label.TextWrapping = TextWrapping.Wrap;
        _label.Margin = new Thickness(0, 6, 0, 2); _detail.Children.Add(_label);
        _input = UiKit.Field(); _input.FontSize = 13; _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap; _input.Height = 56;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Id(_input, "A11yInput", "Str_Attachment_Description");
        // Apply fix stays a worded button, beside the field it applies.
        _apply = UiKit.Make(L("Str_A11y_Fix"), true); Id(_apply, "A11yApply");
        _apply.VerticalAlignment = VerticalAlignment.Top; _apply.Margin = new Thickness(8, 0, 0, 0);
        _apply.Click += async (_, _) => await Apply();
        var fixRow = new Grid();
        fixRow.ColumnDefinitions.Add(new ColumnDefinition());
        fixRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fixRow.Children.Add(_input); Grid.SetColumn(_apply, 1); fixRow.Children.Add(_apply);
        _detail.Children.Add(fixRow);
        _status = Note(canRepair ? "" : "Str_A11y_ReadOnly"); Id(_status, "A11yStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite); _detail.Children.Add(_status);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(L("Str_A11y_Limit") + "\n\n" + _report.ToText()); _status.Text = L("Str_DocumentTools_Copied"); }
            catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = L("Str_DocumentTools_Failed"); }
        };
        // These two open another window, so their names stay in view.
        _readingOrder = DialogParts.LabeledAction("Str_Repair_ReadingOrder", 0xE70F, (_, _) => MoreTools("Str_Repair_ReadingOrder"));
        _tableHeader = DialogParts.LabeledAction("Str_Repair_TableHeader", 0xE80A, (_, _) => MoreTools("Str_Repair_TableHeader"));
        Id(_readingOrder, "A11yReadingOrder"); Id(_tableHeader, "A11yTableHeader");
        var tools = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        tools.Children.Add(_readingOrder); tools.Children.Add(_tableHeader);
        _detail.Children.Add(tools);
        _detail.Children.Add(Note("Str_A11y_Limit", muted: true));
        _detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _detail.Margin = new Thickness(0, 0, 4, 0);

        // Two columns when there is room; at a large scale in a small window the list joins the scrolling column.
        _columns = new Grid();
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        _columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        Grid.SetColumn(_detailScroll, 2); _columns.Children.Add(_detailScroll);
        _columns.SizeChanged += (_, _) => Arrange(_columns.ActualWidth >= 540);
        _detailScroll.SizeChanged += (_, _) => FitToViewport();
        Arrange(true);
        body.Children.Add(_columns);
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        _findings.SelectionChanged += async (_, _) => await SelectFinding();
        _input.TextChanged += (_, _) => UpdateButtons();
        Closing += (_, _) => { _closed = true; _preview?.Cancel(); };
        Refresh();
    }

    private PdfAccessibilityFinding? Selected => _findings.SelectedIndex is int index
        && index >= 0 && index < _report.Findings.Count ? _report.Findings[index] : null;

    private void Refresh()
    {
        int previous = Math.Max(0, _findings.SelectedIndex);
        _report = AccessibilityReviewSource.Inspect(_source, _protection);
        _findings.Items.Clear();
        foreach (PdfAccessibilityFinding finding in _report.Findings)
        {
            string label = Explain(finding);
            if (finding.PageIndex is int page) label = L("Str_AutoBookmarks_Page") + " " + (page + 1) + ": " + label;
            if (!string.IsNullOrWhiteSpace(finding.FieldName)) label += " (" + finding.FieldName + ")";
            var item = new ListBoxItem { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Tag = finding.Code.ToString(), Padding = new Thickness(6, 4, 6, 4) };
            AutomationProperties.SetName(item, label); _findings.Items.Add(item);
        }
        _count.Text = _report.Findings.Count == 0 ? L("Str_A11y_Ready")
            : string.Format(CultureInfo.CurrentCulture, L("Str_A11y_Remaining"), _report.Findings.Count);
        _findings.SelectedIndex = _report.Findings.Count == 0 ? -1 : Math.Min(previous, _report.Findings.Count - 1);
        if (_report.Findings.Count == 0) _ = SelectFinding();
        UpdateButtons();
    }

    private bool Supported(PdfAccessibilityFinding? finding) => finding is not null && !_unsupported.Contains(finding)
        && (finding.Code switch
        {
            PdfAccessibilityFindingCode.MissingDocumentLanguage or PdfAccessibilityFindingCode.InvalidDocumentLanguage => true,
            PdfAccessibilityFindingCode.MissingFigureAlternateDescription => finding.ObjectNumber.HasValue && finding.ObjectGeneration == 0,
            PdfAccessibilityFindingCode.MissingLinkDescription => finding.ObjectNumber.HasValue && finding.PageIndex.HasValue && finding.AnnotationIndex.HasValue,
            PdfAccessibilityFindingCode.MissingFormFieldDescription => finding.ObjectNumber.HasValue && !string.IsNullOrWhiteSpace(finding.FieldName),
            _ => false
        });

    private async Task SelectFinding()
    {
        _preview?.Cancel(); _preview?.Dispose(); var cancellation = _preview = new CancellationTokenSource();
        PdfAccessibilityFinding? finding = Selected;
        _input.Text = ""; _image.Source = null; _pageView.Visibility = Visibility.Collapsed;
        _highlight.Visibility = Visibility.Collapsed;
        bool language = finding?.Code is PdfAccessibilityFindingCode.MissingDocumentLanguage or PdfAccessibilityFindingCode.InvalidDocumentLanguage;
        _label.Text = L(language ? "Str_A11y_Language" : "Str_Attachment_Description");
        AutomationProperties.SetName(_input, _label.Text);
        _input.AcceptsReturn = !language;
        _explanation.Text = finding is null ? "" : Explain(finding);
        if (finding?.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription)
            _explanation.Text += " " + L("Str_A11y_FieldScope");
        if (finding is not null && !Supported(finding)) _explanation.Text += " " + L("Str_A11y_Manual");
        _location.Text = L("Str_A11y_NoLocation"); UpdateButtons();
        if (finding is null) return;
        int? locatedPage = finding.PageIndex;
        if (locatedPage is null && finding.ObjectNumber is int figure && finding.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription)
        {
            try { locatedPage = PdfAccessibilityReadingOrder.Read(AccessibilityReviewSource.Open(_source, _protection)).Items
                    .Where(item => item.StructureObjectNumber == figure).Select(item => (int?)item.PageIndex).FirstOrDefault(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        if (locatedPage is not int pageIndex) return;
        _location.Text = L("Str_AutoBookmarks_Page") + " " + (pageIndex + 1);
        byte[] source = _source;
        try
        {
            PagePreview preview = await Task.Run(() => Render(source, pageIndex, finding, cancellation.Token));
            if (_closed || cancellation.IsCancellationRequested) return;
            BitmapSource bitmap = preview.Bitmap;
            _image.Source = bitmap; _image.Width = bitmap.PixelWidth; _image.Height = bitmap.PixelHeight;
            _pageGrid.Width = _overlay.Width = bitmap.PixelWidth;
            _pageGrid.Height = _overlay.Height = bitmap.PixelHeight;
            if (preview.Highlight is Rect bounds && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                Canvas.SetLeft(_highlight, bounds.Left); Canvas.SetTop(_highlight, bounds.Top);
                _highlight.Width = bounds.Width; _highlight.Height = bounds.Height;
                _highlight.Visibility = Visibility.Visible;
            }
            _pageView.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_closed && !cancellation.IsCancellationRequested) _location.Text += ": " + L("Str_A11y_NoPreview"); }
    }

    private sealed record PagePreview(BitmapSource Bitmap, Rect? Highlight);

    private PagePreview Render(byte[] source, int index, PdfAccessibilityFinding finding, CancellationToken cancellation)
    {
        PdfDocument document = AccessibilityReviewSource.Open(source, _protection);
        PdfPageInformation page = PdfPageInformation.Read(document)[index];
        bool quarter = page.Rotation is 90 or 270;
        double width = quarter ? page.Height : page.Width, height = quarter ? page.Width : page.Height;
        double factor = Math.Min(900 / width, 900 / height);
        var rendered = new PdfPageRenderer(document, InstalledPdfFontResolver.Instance).Render(index,
            KillerPDF.Services.WorkingCmykProfiles.Apply(new PdfRenderOptions(Math.Max(1, (int)Math.Ceiling(width * factor)),
                Math.Max(1, (int)Math.Ceiling(height * factor)))), cancellation);
        PdfRenderCompleteness.RequireComplete(index, PdfRenderCompleteness.Classify(rendered.Diagnostics));
        BitmapSource bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32,
            null, rendered.Pixels.ToArray(), rendered.Width * 4);
        int rotation = _rotations is not null && _rotations.TryGetValue(index, out int turn) ? turn : page.Rotation;
        if (rotation != page.Rotation)
            bitmap = new TransformedBitmap(bitmap, new RotateTransform(rotation - page.Rotation));
        bitmap.Freeze();
        Rect? mark = null;
        if (finding.Code == PdfAccessibilityFindingCode.MissingLinkDescription)
        {
            PdfLinkInfo? link = PdfLinkReader.ReadPage(document, index).SingleOrDefault(item => item.AnnotationIndex == finding.AnnotationIndex);
            if (link is not null) mark = Bounds(link.Left, link.Bottom, link.Right, link.Top);
        }
        else if (finding.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription)
        {
            PdfFormWidgetInfo? widget = PdfFormWidgetReader.ReadPage(document, index).SingleOrDefault(item => item.AnnotationIndex == finding.AnnotationIndex);
            if (widget is not null) mark = Bounds(widget.Left, widget.Bottom, widget.Right, widget.Top);
        }
        return new PagePreview(bitmap, mark);

        Rect Bounds(double left, double bottom, double right, double top)
        {
            Rect bounds = RedactionGeometry.PageToCanvas(left - page.Left, bottom - page.Bottom,
                right - page.Left, top - page.Bottom, page.Width, page.Height, bitmap.PixelWidth, bitmap.PixelHeight, rotation);
            bounds.Intersect(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)); return bounds;
        }
    }

    private async Task Apply()
    {
        if (_busy || !Supported(Selected) || string.IsNullOrWhiteSpace(_input.Text) || !_canRepair || !Current()) return;
        PdfAccessibilityFinding finding = Selected!;
        string value = _input.Text.Trim(); byte[] source = _source;
        bool committing = false;
        _busy = true; UpdateButtons();
        try
        {
            byte[] edited = await Task.Run(() =>
            {
                PdfDocument document = PdfDocument.Open(source);
                return (finding.Code switch
                {
                    PdfAccessibilityFindingCode.MissingDocumentLanguage or PdfAccessibilityFindingCode.InvalidDocumentLanguage =>
                        PdfAccessibilityRepair.ApplyDocumentLanguage(document, PdfAccessibilityRepair.PreviewDocumentLanguage(document, value)),
                    PdfAccessibilityFindingCode.MissingFigureAlternateDescription =>
                        PdfAccessibilityRepair.ApplyFigureAlternateDescription(document,
                            PdfAccessibilityRepair.PreviewFigureAlternateDescription(document, finding.ObjectNumber!.Value, value)),
                    PdfAccessibilityFindingCode.MissingLinkDescription =>
                        PdfAccessibilityRepair.ApplyLinkDescription(document,
                            PdfAccessibilityRepair.PreviewLinkDescription(document, finding.PageIndex!.Value, finding.AnnotationIndex!.Value, value)),
                    PdfAccessibilityFindingCode.MissingFormFieldDescription =>
                        PdfAccessibilityRepair.ApplyFormFieldDescription(document,
                            PdfAccessibilityRepair.PreviewFormFieldDescription(document, finding.FieldName!, value)),
                    _ => throw new NotSupportedException(L("Str_A11y_Manual"))
                }).Document.ToArray();
            });
            if (_closed || !Current()) return;
            committing = true;
            byte[]? applied = _applyFix(edited);
            if (applied is null) { Changed(); return; }
            _source = applied; _fixes++; Refresh(); _status.Text = L("Str_A11y_Applied");
        }
        catch (NotSupportedException) when (!committing)
        {
            if (!_closed) { _unsupported.Add(finding); _explanation.Text += " " + L("Str_A11y_Manual"); _status.Text = L("Str_A11y_Manual"); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_closed)
            {
                // A failed host refresh may follow a completed document mutation.
                if (committing) Changed();
                else _status.Text = L("Str_A11y_Failed") + " " + error.Message;
            }
        }
        finally { _busy = false; if (!_closed) UpdateButtons(); }
    }

    private void Undo()
    {
        if (_busy || _fixes == 0 || !Current()) return;
        try
        {
            byte[]? restored = _undoFix();
            if (restored is null) { Changed(); return; }
            _source = restored; _fixes--; Refresh(); _status.Text = "";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Changed(); }
    }
    private void Recheck() { if (!_busy && Current()) { Refresh(); _status.Text = _canRepair ? "" : L("Str_A11y_ReadOnly"); } }
    private void MoreTools(string key) { if (_canRepair && !_busy && Current()) { RequestedTool = key; Close(); } }
    private bool Current()
    {
        if (_closed) return false;
        try { if (!_stale && _sourceCurrent()) return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        Changed(); return false;
    }
    private void Changed() { _stale = true; _status.Text = L("Str_A11y_Changed"); UpdateButtons(); }
    private void UpdateButtons()
    {
        bool ready = !_busy && !_stale;
        _findings.IsEnabled = ready; _recheck.IsEnabled = ready;
        _input.IsEnabled = ready && _canRepair && Supported(Selected);
        _apply.IsEnabled = _input.IsEnabled && !string.IsNullOrWhiteSpace(_input.Text);
        _undo.IsEnabled = ready && _canRepair && _fixes > 0;
        _readingOrder.IsEnabled = _tableHeader.IsEnabled = ready && _canRepair;
    }
    private static string Explain(PdfAccessibilityFinding finding) => L(finding.Code switch
    {
        PdfAccessibilityFindingCode.MissingDocumentLanguage or PdfAccessibilityFindingCode.InvalidDocumentLanguage => "Str_A11y_LanguageIssue",
        PdfAccessibilityFindingCode.MissingFigureAlternateDescription => "Str_A11y_FigureIssue",
        PdfAccessibilityFindingCode.MissingLinkDescription => "Str_A11y_LinkIssue",
        PdfAccessibilityFindingCode.MissingFormFieldDescription => "Str_A11y_FieldIssue",
        PdfAccessibilityFindingCode.MissingTableHeader => "Str_A11y_TableIssue",
        PdfAccessibilityFindingCode.DocumentNotMarked => "Str_A11y_MarkedIssue",
        _ => "Str_A11y_StructureIssue"
    });
    // Wide: the list in its own column. Narrow (a large scale in a small window): the list sits under the intro in
    // the scrolling column, no taller than the viewport, so every control can still be scrolled fully into view.
    private void Arrange(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        if (_listPanel.Parent is Panel parent) parent.Children.Remove(_listPanel);
        if (wide)
        {
            Grid.SetColumn(_listPanel, 0); _columns.Children.Add(_listPanel);
            _columns.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            _columns.ColumnDefinitions[1].Width = new GridLength(18);
            _listPanel.Margin = new Thickness(0);
        }
        else
        {
            _columns.ColumnDefinitions[0].Width = new GridLength(0);
            _columns.ColumnDefinitions[1].Width = new GridLength(0);
            _listPanel.Margin = new Thickness(0, 6, 0, 4);
            _detail.Children.Insert(1, _listPanel);
        }
        FitToViewport();
    }

    private void FitToViewport()
    {
        double height = _detailScroll.ActualHeight;
        if (height <= 0) return;
        _pageView.MaxHeight = Math.Clamp(height - 8, 40, 230);
        _findings.MaxHeight = _wide == false ? Math.Clamp(height - 8, 40, 220) : double.PositiveInfinity;
    }

    private static Button IconButton(string key, int glyph, string id)
    {
        Button button = DialogParts.IconAction(key, glyph, (_, _) => { });
        Id(button, id); return button;
    }
    private static TextBlock Note(string key, bool muted = false)
    {
        var text = new TextBlock { Text = key.Length == 0 ? "" : L(key), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2) };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedTextBrush" : "TextBrush"); return text;
    }
    private static void Id(DependencyObject element, string id, string? label = null)
    { AutomationProperties.SetAutomationId(element, id); if (label is not null) AutomationProperties.SetName(element, L(label)); }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
