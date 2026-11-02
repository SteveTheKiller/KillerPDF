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
    private readonly ComboBox _findings;
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
        MaxHeight = SystemParameters.WorkArea.Height - 40; SizeToContent = SizeToContent.Height;
        MinWidth = 460; MinHeight = 360; UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        Id(this, "AccessibilityReviewDialog");
        var body = new DockPanel { Margin = new Thickness(16, 4, 16, 14),
            LayoutTransform = new ScaleTransform(scale, scale) };
        var close = Button("Str_DocumentTools_Close", "A11yClose");
        close.HorizontalAlignment = HorizontalAlignment.Right; close.IsCancel = true;
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); body.Children.Add(close);
        var content = new StackPanel();
        content.Children.Add(Note("Str_A11y_Intro"));
        _count = Note(""); Id(_count, "A11yCount"); content.Children.Add(_count);
        _findings = new ComboBox { Style = UiKit.DarkComboStyle, Margin = new Thickness(0, 6, 0, 6) };
        Id(_findings, "A11yFindings", "Str_DocumentTools_Accessibility"); content.Children.Add(_findings);
        _explanation = Note(""); content.Children.Add(_explanation);
        _location = Note(""); Id(_location, "A11yLocation"); content.Children.Add(_location);
        _image = new Image { Stretch = Stretch.Uniform };
        Id(_image, "A11yPreview", "Str_Dlg_Preview");
        _pageGrid = new Grid(); _pageGrid.Children.Add(_image);
        _overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true }; Id(_overlay, "A11yOverlay");
        _highlight = new Rectangle { StrokeThickness = 4, Visibility = Visibility.Collapsed };
        _highlight.SetResourceReference(Shape.StrokeProperty, "SelectionAccent"); Id(_highlight, "A11yHighlight");
        _overlay.Children.Add(_highlight); _pageGrid.Children.Add(_overlay);
        _pageView = new Viewbox { Child = _pageGrid, Stretch = Stretch.Uniform, MaxHeight = 230,
            Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(_pageView);
        _label = Note(""); content.Children.Add(_label);
        _input = UiKit.Field(); _input.FontSize = 13; _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap; _input.Height = 56;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Id(_input, "A11yInput", "Str_Attachment_Description"); content.Children.Add(_input);
        _apply = Button("Str_A11y_Fix", "A11yApply", true); _apply.Click += async (_, _) => await Apply();
        _undo = Button("Str_Lbl_Undo", "A11yUndo"); _undo.Click += (_, _) => Undo();
        _recheck = Button("Str_A11y_Recheck", "A11yRecheck"); _recheck.Click += (_, _) => Recheck();
        content.Children.Add(Row(_apply, _undo, _recheck));
        _status = Note(canRepair ? "" : "Str_A11y_ReadOnly"); Id(_status, "A11yStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite); content.Children.Add(_status);
        _readingOrder = Button("Str_Repair_ReadingOrder", "A11yReadingOrder");
        _tableHeader = Button("Str_Repair_TableHeader", "A11yTableHeader");
        _readingOrder.Click += (_, _) => MoreTools("Str_Repair_ReadingOrder");
        _tableHeader.Click += (_, _) => MoreTools("Str_Repair_TableHeader");
        var copy = Button("Str_DocumentTools_Copy", "A11yCopy");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(L("Str_A11y_Limit") + "\n\n" + _report.ToText()); _status.Text = L("Str_DocumentTools_Copied"); }
            catch (Exception error) when (error is not OutOfMemoryException) { _status.Text = L("Str_DocumentTools_Failed"); }
        };
        content.Children.Add(Row(_readingOrder, _tableHeader, copy));
        content.Children.Add(Note("Str_A11y_Limit"));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 6) };
        scroll.SizeChanged += (_, _) =>
        {
            content.Width = Math.Max(1, scroll.ActualWidth - SystemParameters.VerticalScrollBarWidth);
            _pageView.MaxHeight = Math.Clamp(scroll.ActualHeight - 8, 40, 230);
        };
        body.Children.Add(scroll);
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
            var item = new ComboBoxItem { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Tag = finding.Code.ToString() };
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
            new PdfRenderOptions(Math.Max(1, (int)Math.Ceiling(width * factor)), Math.Max(1, (int)Math.Ceiling(height * factor))), cancellation);
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
    private static Button Button(string key, string id, bool accent = false)
    {
        Button button = UiKit.Make(L(key), accent); button.MinHeight = 30;
        button.Padding = new Thickness(6, 3, 6, 3); button.Margin = new Thickness(0, 4, 8, 4);
        Id(button, id); return button;
    }
    private static WrapPanel Row(params Button[] buttons)
    { var row = new WrapPanel(); foreach (Button button in buttons) row.Children.Add(button); return row; }
    private static TextBlock Note(string key)
    {
        var text = new TextBlock { Text = key.Length == 0 ? "" : L(key), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2) };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return text;
    }
    private static void Id(DependencyObject element, string id, string? label = null)
    { AutomationProperties.SetAutomationId(element, id); if (label is not null) AutomationProperties.SetName(element, L(label)); }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
