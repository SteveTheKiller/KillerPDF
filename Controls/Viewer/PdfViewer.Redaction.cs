using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Redact tool: drag areas on the page (or mark the current search results), then remove
    // everything under them for good. Marks are kept in unrotated, crop-relative page points so they
    // survive zoom and view changes. They belong to the document they were drawn on, stay visible
    // after the tool closes, and are saved into the file as /Redact annotations until applied. Each
    // mark carries the overlay text and fill color drawn into its area when it is applied.
    public partial class PdfViewer
    {
        private const string RedactMarkTag = "RedactMark";
        private const string RedactFillSetting = "RedactFillColor";
        private const string RedactOverlaySetting = "RedactOverlayText";
        private static Color? _redactFillDefault;
        private static string? _redactOverlayDefault;
        private static bool _redactDefaultsLoaded;
        private List<RedactionMark> _redactMarks = [];
        private string? _redactionSourceFingerprint;
        // The words searches marked, checked for in the finished file once hidden information is dealt with.
        private readonly HashSet<string> _redactFoundText = new(StringComparer.OrdinalIgnoreCase);

        internal RedactionMarkSnapshot CaptureRedactionMarksExt() =>
            new(_redactMarks, _redactionSourceFingerprint, _redactFoundText);

        internal void RestoreRedactionMarksExt(RedactionMarkSnapshot snapshot)
        {
            _redactMarks = [.. snapshot.Marks];
            _redactionSourceFingerprint = snapshot.SourceFingerprint;
            _redactFoundText.Clear();
            _redactFoundText.UnionWith(snapshot.FoundText);
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }
        private Border? _redactBar;
        private TextBlock? _redactCountText;
        private Rectangle? _redactDrawRect;
        private Point _redactDrawStart;
        private bool _redactDrawing;

        /// <summary>Shows the Redact bar for the active document, keeping marks already drawn on it.</summary>
        internal void ShowRedactionBar()
        {
            if (_doc is null) return;
            if (_redactBar is not null)
            {
                UpdateRedactionCount();
                return;
            }

            // The Redact tool's options dock in the pane's options row: the mark count, the marking
            // and property buttons, and Apply, which never leaves the row.
            var bar = new ToolOptionsBar(EditTool.Redact, Loc("Str_Lbl_Redact"), Loc("Str_Bar_More"));

            _redactCountText = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            _redactCountText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            bar.Add(ToolOptionGroup.Count, _redactCountText);

            Button BarButton(string id, string key, string tipKey, bool accent, RoutedEventHandler click)
            {
                var button = UiKit.Make(Loc(key), accent);
                button.SetResourceReference(ContentControl.ContentProperty, key);
                button.SetResourceReference(FrameworkElement.ToolTipProperty, tipKey);
                button.Padding = new Thickness(10, 0, 10, 0);
                button.Height = ToolOptionsControls.ControlHeight;
                button.VerticalAlignment = VerticalAlignment.Center;
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Redact" + id);
                button.Click += click;
                bar.Add(id, button);
                return button;
            }

            BarButton(ToolOptionGroup.Find, "Str_Redact_Find", "Str_Redact_FindTip", false, (_, _) => FindTextForRedaction());
            BarButton(ToolOptionGroup.MarkSearch, "Str_Redact_MarkSearch", "Str_Redact_MarkSearchTip", false, (_, _) => MarkSearchResultsForRedaction());
            Button? markPagesButton = null;
            markPagesButton = BarButton(ToolOptionGroup.MarkPages, "Str_Redact_MarkPages", "Str_Redact_MarkPagesTip", false,
                (_, _) => { if (markPagesButton is not null) OpenMarkPagesFlyout(markPagesButton); });
            BarButton(ToolOptionGroup.Properties, "Str_Redact_Properties", "Str_Redact_PropertiesTip", false, (_, _) => EditRedactionProperties());
            BarButton(ToolOptionGroup.Clear, "Str_Redact_Clear", "Str_Redact_ClearTip", false, (_, _) => ClearRedactionMarks());
            BarButton(ToolOptionGroup.Apply, "Str_Redact_Apply", "Str_Redact_ApplyTip", true, (_, _) => ApplyRedaction());

            _redactBar = bar;
            ToolOptionsDock.Bar = bar;
            _annotBarTool = EditTool.Redact;
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }

        /// <summary>Closes the Redact bar. The marks stay on the document until applied or cleared.</summary>
        internal void HideRedactionBar()
        {
            if (_redactBar is not null)
            {
                if (ReferenceEquals(ToolOptionsDock.Bar, _redactBar)) ToolOptionsDock.Bar = null;
                _redactBar = null;
            }
            _redactCountText = null;
            if (_markPagesFlyout is not null) _markPagesFlyout.IsOpen = false;
            CancelRedactionDrag();
            if (_annotBarTool == EditTool.Redact) _annotBarTool = null;
        }

        /// <summary>Loads the marks saved in the newly opened file, replacing any from before.</summary>
        internal void LoadRedactionMarksFromDocument()
        {
            _redactMarks.Clear();
            if (_currentFile is null) return;
            try
            {
                foreach (var mark in PdfEngineIntegration.ReadRedactionMarks(EnsureEngineDocumentSession().Document))
                    _redactMarks.Add(new RedactionMark(mark.PageIndex, mark.Left, mark.Bottom, mark.Right, mark.Top,
                        mark.OverlayText, mark.FillColor is { } fill
                            ? Color.FromRgb(ToByte(fill.Red), ToByte(fill.Green), ToByte(fill.Blue)) : null,
                        mark.AnnotationFlags));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"LoadRedactionMarksFromDocument: {ex}");
            }
            UpdateRedactionCount();
        }

        /// <summary>Saves the document's unapplied marks into the file being written.</summary>
        internal void WriteRedactionMarksToDocument(string path) =>
            PdfEngineIntegration.WriteRedactionMarks(path, [.. _redactMarks.Select(mark =>
                new PdfEngineIntegration.RedactionMarkData(mark.PageIndex, mark.Left, mark.Bottom, mark.Right, mark.Top,
                    mark.OverlayText, PdfFill(mark.Fill), mark.AnnotationFlags))]);

        private static byte ToByte(double component) => (byte)Math.Round(Math.Clamp(component, 0, 1) * 255);

        private static KillerPdf.Engine.Authoring.PdfRgbColor? PdfFill(Color? fill) =>
            fill is Color color ? new KillerPdf.Engine.Authoring.PdfRgbColor(color.R / 255.0, color.G / 255.0, color.B / 255.0) : null;

        private static PdfRedactionRegion ToRegion(RedactionMark mark) =>
            new(mark.PageIndex, new PdfContentBounds(mark.Left, mark.Bottom, mark.Right, mark.Top), null, mark.OverlayText)
            {
                FillColor = PdfFill(mark.Fill)
            };

        // The fill color and overlay text new marks get, remembered between sessions.
        private static void LoadRedactionDefaults()
        {
            if (_redactDefaultsLoaded) return;
            _redactDefaultsLoaded = true;
            try
            {
                if (App.GetSetting(RedactFillSetting) is string fill && fill.Length > 0
                    && ColorConverter.ConvertFromString(fill) is Color color)
                    _redactFillDefault = Color.FromRgb(color.R, color.G, color.B);
            }
            catch (FormatException) { _redactFillDefault = null; }
            string? overlay = App.GetSetting(RedactOverlaySetting)?.Trim();
            _redactOverlayDefault = string.IsNullOrEmpty(overlay) || !PdfContentRedaction.SupportsOverlayText(overlay)
                ? null : overlay;
        }

        private static RedactionMark NewMark(int pageIndex, double left, double bottom, double right, double top)
        {
            LoadRedactionDefaults();
            return new RedactionMark(pageIndex, left, bottom, right, top, _redactOverlayDefault, _redactFillDefault);
        }

        /// <summary>True when the event came from the Redact bar, which the page canvas must not swallow.</summary>
        internal bool IsInRedactionBar(object? source) =>
            _redactBar is not null && source is DependencyObject element && IsDescendantOf(element, _redactBar);

        private void UpdateRedactionCount()
        {
            if (_redactCountText is null) return;
            int pages = _redactMarks.Select(mark => mark.PageIndex).Distinct().Count();
            _redactCountText.Text = _redactMarks.Count == 0
                ? Loc("Str_Redact_NoMarks")
                : string.Format(Loc("Str_Redact_Count"), _redactMarks.Count, pages);
            ToolOptionsBar.RemeasureOwner(_redactCountText);   // a longer count must not push Apply off the row
        }

        private bool TryPageGeometry(int pageIndex, out double pageWidth, out double pageHeight,
            out double canvasWidth, out double canvasHeight, out int rotation)
        {
            pageWidth = pageHeight = canvasWidth = canvasHeight = 0;
            rotation = 0;
            if (_doc is null || !_renderDims.TryGetValue(pageIndex, out var dims) || dims.w <= 0 || dims.h <= 0)
                return false;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return false;
            pageWidth = session.Pages[pageIndex].Width;
            pageHeight = session.Pages[pageIndex].Height;
            canvasWidth = dims.w;
            canvasHeight = dims.h;
            rotation = EffectiveRotation(pageIndex);
            return pageWidth > 0 && pageHeight > 0;
        }

        /// <summary>The page's on-screen rotation, falling back to the file's /Rotate before rotations are captured.</summary>
        private int EffectiveRotation(int pageIndex)
        {
            if (_pageRotations.TryGetValue(pageIndex, out int stored)) return stored;
            if (_doc is null) return 0;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            return pageIndex >= 0 && pageIndex < session.PageCount ? session.Pages[pageIndex].Rotation : 0;
        }

        private Rect? MarkCanvasRect(RedactionMark mark)
        {
            if (!TryPageGeometry(mark.PageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
                return null;
            double left = Math.Max(0, mark.Left), bottom = Math.Max(0, mark.Bottom);
            double right = Math.Min(pw, mark.Right), top = Math.Min(ph, mark.Top);
            if (right <= left || top <= bottom) return null;
            return RedactionGeometry.PageToCanvas(left, bottom, right, top, pw, ph, cw, ch, rot);
        }

        /// <summary>Repaints this page's marks on its overlay. Called at the end of every annotation re-render.</summary>
        internal void ApplyRedactionMarks(int pageIndex, Canvas? canvas)
        {
            if (canvas is null) return;
            foreach (var old in canvas.Children.OfType<Rectangle>().Where(r => r.Tag is RedactionMark).ToList())
                canvas.Children.Remove(old);
            foreach (RedactionMark mark in _redactMarks.Where(mark => mark.PageIndex == pageIndex && mark.IsVisibleOnScreen))
            {
                if (MarkCanvasRect(mark) is not Rect rect) continue;
                var visual = new Rectangle
                {
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    Fill = new SolidColorBrush(Color.FromArgb(120, (mark.Fill ?? Colors.Black).R,
                        (mark.Fill ?? Colors.Black).G, (mark.Fill ?? Colors.Black).B)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x20)),
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                    Tag = mark
                };
                Canvas.SetLeft(visual, rect.X);
                Canvas.SetTop(visual, rect.Y);
                Panel.SetZIndex(visual, 5);
                canvas.Children.Add(visual);
            }
        }

        private void RepaintRedactionMarks()
        {
            if (_doc is null) return;
            int count = EnsureEngineDocumentSession().PageCount;
            for (int page = 0; page < count; page++)
            {
                var canvas = VisibleCanvasForPage(page);
                if (canvas is not null) ApplyRedactionMarks(page, canvas);
            }
        }

        internal void RedactionMouseDown(Point position)
        {
            if (_activeCanvas is null) return;
            ClearSelection();
            _redactDrawing = true;
            _redactDrawStart = position;
            _redactDrawRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x20)),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 2],
                Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                Tag = RedactMarkTag
            };
            Canvas.SetLeft(_redactDrawRect, position.X);
            Canvas.SetTop(_redactDrawRect, position.Y);
            Panel.SetZIndex(_redactDrawRect, 6);
            _activeCanvas.Children.Add(_redactDrawRect);
            _activeCanvas.CaptureMouse();
        }

        internal bool RedactionMouseMove(MouseEventArgs e)
        {
            if (!_redactDrawing || _redactDrawRect is null || _gestureCanvas is null) return false;
            Point position = e.GetPosition(_gestureCanvas);
            Canvas.SetLeft(_redactDrawRect, Math.Min(position.X, _redactDrawStart.X));
            Canvas.SetTop(_redactDrawRect, Math.Min(position.Y, _redactDrawStart.Y));
            _redactDrawRect.Width = Math.Abs(position.X - _redactDrawStart.X);
            _redactDrawRect.Height = Math.Abs(position.Y - _redactDrawStart.Y);
            return true;
        }

        internal bool RedactionMouseUp(int pageIndex, MouseButtonEventArgs e)
        {
            if (!_redactDrawing) return false;
            Canvas? canvas = _gestureCanvas ?? _activeCanvas;
            Point end = canvas is null ? _redactDrawStart : e.GetPosition(canvas);
            canvas?.ReleaseMouseCapture();
            var drawn = new Rect(_redactDrawStart, end);
            CancelRedactionDrag();

            if (drawn.Width < 4 || drawn.Height < 4)
            {
                // A click removes the mark under it.
                RedactionMark? hit = _redactMarks.LastOrDefault(mark => mark.PageIndex == pageIndex && mark.IsVisibleOnScreen
                    && MarkCanvasRect(mark) is Rect rect && rect.Contains(end));
                if (hit is not null && _redactMarks.Remove(hit)) MarkDirty();
            }
            else if (TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
            {
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(drawn, pw, ph, cw, ch, rot);
                if (right - left > 0.5 && top - bottom > 0.5)
                {
                    _redactMarks.Add(NewMark(pageIndex, left, bottom, right, top));
                    MarkDirty();
                }
            }
            UpdateRedactionCount();
            ApplyRedactionMarks(pageIndex, canvas);
            return true;
        }

        private void CancelRedactionDrag()
        {
            _redactDrawing = false;
            if (_redactDrawRect is not null)
                (_redactDrawRect.Parent as Panel)?.Children.Remove(_redactDrawRect);
            _redactDrawRect = null;
        }

        /// <summary>
        /// The page menu's Redact on a flowing text selection: each selected line becomes a mark
        /// and the Redact tool opens on them, so the bar's Apply removes exactly that text.
        /// </summary>
        internal void MarkTextSelectionForRedaction()
        {
            if (!_txtSelHasRange || _doc is null) return;
            var (start, end) = OrderedSelection();
            var marks = new List<RedactionMark>();
            for (int page = start.Page; page <= end.Page; page++)
            {
                if (!TryPageGeometry(page, out double pw, out double ph, out double cw, out double ch, out int rot))
                    continue;
                foreach (Rect line in SelectionLineRectsForPage(page))
                {
                    var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(line, pw, ph, cw, ch, rot);
                    if (right - left > 0.5 && top - bottom > 0.5)
                        marks.Add(NewMark(page, left, bottom, right, top));
                }
            }
            if (marks.Count == 0) return;

            ClearTextSelection();
            if (_currentTool != EditTool.Redact) SetTool(EditTool.Redact);
            // An owner-restricted document stays on Select until it is unlocked.
            if (_currentTool != EditTool.Redact) return;
            _redactMarks.AddRange(marks);
            MarkDirty();
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }

        private void MarkSearchResultsForRedaction()
        {
            if (!Search.HasResults)
            {
                SetStatus(Loc("Str_Redact_NoSearch"));
                return;
            }
            if (!Search.ResultsCurrent)
            {
                Search.Run(Search.LastQuery);
                return;
            }
            // Mark only the matched characters, not the whole words the search highlights.
            // The options are the ones these results were found with, so Match case and Whole word keep
            // the marks to what was highlighted and off the same letters inside other words.
            // A page that cannot be read is not searched, and a redaction that skips it must say so.
            Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> matches;
            List<int> unreadable;
            try
            {
                string query = PdfSearchText.Fold(Search.LastQuery);
                var options = new PdfRedactionSearchOptions
                {
                    Query = Search.LastWholeWord ? @"(?<![\p{L}\p{Nd}_])" + System.Text.RegularExpressions.Regex.Escape(query)
                        + @"(?![\p{L}\p{Nd}_])" : query,
                    Kind = Search.LastWholeWord ? PdfRedactionSearchKind.RegularExpression : PdfRedactionSearchKind.ExactText,
                    MatchCase = Search.LastMatchCase
                };
                var snapshot = CaptureCurrentSearchSnapshot();
                string fingerprint = snapshot.Fingerprint();
                if (_redactionSourceFingerprint is not null && _redactionSourceFingerprint != fingerprint)
                {
                    SetStatus(Loc("Str_Redact_SourceChanged"));
                    return;
                }
                var current = SearchService.FindCurrentRedaction(snapshot, options);
                if (current.Matches.Count > 0) _redactionSourceFingerprint = fingerprint;
                foreach (PdfRedactionMatch match in current.Matches)
                    if (match.Text.Trim() is { Length: > 0 } found) _redactFoundText.Add(found);
                unreadable = [.. current.Unreadable];
                matches = current.Matches.GroupBy(match => match.PageIndex).ToDictionary(group => group.Key,
                    group => group.SelectMany(match => match.Areas.Count > 0 ? match.Areas : [match.Bounds])
                        .Select(area => (area.Left, area.Bottom, area.Right, area.Top)).ToList());
            }
            catch (Exception)
            {
                SetStatus(Loc("Str_Search_Error"));
                return;
            }
            int added = 0;
            foreach (var (page, rects) in matches)
            {
                foreach (var (left, bottom, right, top) in rects)
                {
                    if (_redactMarks.Any(mark => mark.PageIndex == page && mark.Left == left
                        && mark.Bottom == bottom && mark.Right == right && mark.Top == top)) continue;
                    _redactMarks.Add(NewMark(page, left, bottom, right, top));
                    added++;
                }
            }
            if (added > 0) MarkDirty();
            UpdateRedactionCount();
            RepaintRedactionMarks();
            string marked = string.Format(Loc("Str_Redact_MarkedSearch"), added);
            if (unreadable.Count == 0)
            {
                SetStatus(marked);
                return;
            }
            string skipped = Search.SkippedPagesMessage(unreadable);
            SetStatus(marked + " " + skipped);
            KillerDialog.Show(Window.GetWindow(this), skipped, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The Redact bar's Find: words, phrases or patterns across the whole document, reviewed in a
        // list, and the checked results marked letter by letter on each line they cover.
        private void FindTextForRedaction()
        {
            if (_doc is null || _currentFile is null || Window.GetWindow(this) is not Window owner) return;
            CurrentSearchSnapshot snapshot = CaptureCurrentSearchSnapshot();
            string fingerprint = snapshot.Fingerprint();
            if (_redactionSourceFingerprint is not null && _redactionSourceFingerprint != fingerprint)
            {
                SetStatus(Loc("Str_Redact_SourceChanged"));
                return;
            }
            var dialog = new RedactionFindDialog(owner, () => SearchService.LoadPagesForReview(snapshot.Bytes, snapshot.Protection),
                () => PdfEngineIntegration.OpenForReading(snapshot.Bytes, snapshot.Protection),
                options => SearchService.FindCurrentRedaction(snapshot, options));
            if (dialog.ShowDialog() != true) return;
            if (!snapshot.SameContent(CaptureCurrentSearchSnapshot()))
            {
                SetStatus(Loc("Str_Redact_SourceChanged"));
                return;
            }
            _redactionSourceFingerprint = fingerprint;
            int added = 0;
            foreach (PdfRedactionMatch match in dialog.Checked)
            {
                if (match.Text.Trim() is { Length: > 0 } found) _redactFoundText.Add(found);
                IEnumerable<PdfContentBounds> areas = match.Areas.Count > 0 ? match.Areas : [match.Bounds];
                foreach (PdfContentBounds area in areas)
                {
                    if (_redactMarks.Any(mark => mark.PageIndex == match.PageIndex && mark.Left == area.Left
                        && mark.Bottom == area.Bottom && mark.Right == area.Right && mark.Top == area.Top)) continue;
                    _redactMarks.Add(NewMark(match.PageIndex, area.Left, area.Bottom, area.Right, area.Top));
                    added++;
                }
            }
            if (added > 0) MarkDirty();
            UpdateRedactionCount();
            RepaintRedactionMarks();
            SetStatus(string.Format(Loc("Str_Redact_MarkedSearch"), added));
        }

        // The Redact bar's Mark pages: each listed page gets one mark over its whole visible area,
        // so everything drawn on it goes when the redaction is applied. The page list is asked for in a
        // small flyout under the bar's own Mark pages button (a bar dropdown stays on its trigger),
        // not a dialog of its own.
        private System.Windows.Controls.Primitives.Popup? _markPagesFlyout;

        private void OpenMarkPagesFlyout(Button anchor)
        {
            if (_doc is null) return;
            if (_markPagesFlyout is { IsOpen: true }) { _markPagesFlyout.IsOpen = false; return; }

            var prompt = new TextBlock
            {
                Text = Loc("Str_Redact_MarkPagesPrompt"),
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            TextBox box = UiKit.Field();
            box.Text = (CurrentPageIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
            System.Windows.Automation.AutomationProperties.SetName(box, Loc("Str_Redact_MarkPages"));
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "RedactMarkPagesRange");
            var mark = UiKit.Make(Loc("Str_Redact_MarkPagesConfirm"), accent: true);
            var cancel = UiKit.Make(Loc("Str_Btn_Cancel"), accent: false);
            var buttons = UiKit.ButtonRow(mark, cancel);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            var body = new StackPanel { Width = 260, Margin = new Thickness(12, 10, 12, 12) };
            body.Children.Add(prompt);
            body.Children.Add(box);
            body.Children.Add(buttons);

            var flyout = ToolOptionsBar.MakePopup(anchor, body, rightAligned: false);
            void Commit()
            {
                string range = box.Text;
                flyout.IsOpen = false;
                MarkPagesForRedaction(range);
            }
            mark.Click += (_, _) => Commit();
            cancel.Click += (_, _) => flyout.IsOpen = false;
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                Commit();
            };
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
            // While the flyout is open its button takes no clicks, so the click that closes a
            // StaysOpen=false popup does not reopen it at once (the bar menus do the same).
            flyout.Opened += (_, _) => anchor.IsHitTestVisible = false;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_markPagesFlyout, flyout)) _markPagesFlyout = null;
                anchor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => anchor.IsHitTestVisible = true));
            };
            _markPagesFlyout = flyout;
            flyout.IsOpen = true;
        }

        private void MarkPagesForRedaction(string range)
        {
            if (_doc is null) return;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            Window? owner = Window.GetWindow(this);
            List<int>? pages = string.IsNullOrWhiteSpace(range) ? null
                : Features.CliRunner.CliParsePageRange(range, session.PageCount, out _);
            if (pages is null || pages.Count == 0)
            {
                KillerDialog.Show(owner, Loc("Str_InvalidRange"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int added = 0;
            foreach (int page in pages)
            {
                double width = session.Pages[page].Width, height = session.Pages[page].Height;
                if (_redactMarks.Any(mark => mark.PageIndex == page && mark.Left <= 0 && mark.Bottom <= 0
                    && mark.Right >= width && mark.Top >= height)) continue;
                _redactMarks.Add(NewMark(page, 0, 0, width, height));
                added++;
            }
            if (added > 0) MarkDirty();
            UpdateRedactionCount();
            RepaintRedactionMarks();
            SetStatus(string.Format(Loc("Str_Redact_MarkedPages"), added));
        }

        // The Redact bar's Properties: the fill color and overlay text for new marks, and optionally
        // for the marks already drawn.
        private void EditRedactionProperties()
        {
            if (_doc is null || Window.GetWindow(this) is not Window owner) return;
            LoadRedactionDefaults();
            var dialog = new RedactionPropertiesDialog(owner, _redactFillDefault ?? Colors.Black,
                _redactOverlayDefault, _redactMarks.Count);
            if (dialog.ShowDialog() != true) return;
            _redactFillDefault = dialog.Fill;
            _redactOverlayDefault = dialog.OverlayText;
            App.SetSetting(RedactFillSetting, $"#{dialog.Fill.R:X2}{dialog.Fill.G:X2}{dialog.Fill.B:X2}");
            App.SetSetting(RedactOverlaySetting, dialog.OverlayText ?? string.Empty);
            if (!dialog.ApplyToExisting || _redactMarks.Count == 0) return;
            for (int index = 0; index < _redactMarks.Count; index++)
                _redactMarks[index] = _redactMarks[index] with { OverlayText = dialog.OverlayText, Fill = dialog.Fill };
            MarkDirty();
            RepaintRedactionMarks();
        }

        private void ClearRedactionMarks()
        {
            if (_redactMarks.Count > 0) MarkDirty();
            _redactMarks.Clear();
            _redactFoundText.Clear();
            _redactionSourceFingerprint = null;
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }

        private void ApplyRedaction()
        {
            if (_doc is null || _currentFile is null) return;
            if (_redactionSourceFingerprint is not null && _redactionSourceFingerprint != CaptureCurrentSearchSnapshot().Fingerprint())
            {
                SetStatus(Loc("Str_Redact_SourceChanged"));
                return;
            }
            if (_redactMarks.Count == 0)
            {
                SetStatus(Loc("Str_Redact_NoMarks"));
                return;
            }
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            List<PdfRedactionRegion> regions = [.. _redactMarks
                .Where(mark => mark.PageIndex >= 0 && mark.PageIndex < session.PageCount)
                .Select(ToRegion)];

            PdfContentRedactionPlan plan;
            try { plan = PdfEngineIntegration.AnalyzeRedaction(session.Document, regions); }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_Redact_Failed"), ex.Message));
                return;
            }

            var approved = new List<int>();
            var skipped = new HashSet<int>();
            if (!ApproveRasterPages(plan.RasterPages, approved, skipped)) return;
            if (skipped.Count > 0) regions.RemoveAll(region => skipped.Contains(region.PageIndex));
            if (regions.Count == 0)
            {
                SetStatus(Loc("Str_Redact_NothingApplied"));
                return;
            }

            List<(int Page, PageAnnotation Annotation)> covered = CoveredAnnotations(regions);
            var retiredFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (int page in regions.Select(region => region.PageIndex).Distinct())
                foreach (var field in PdfEngineIntegration.ReadPageFormWidgets(session.Document, page))
                {
                    var bounds = new PdfContentBounds(field.Left - field.PageBoxLeft, field.Bottom - field.PageBoxBottom,
                        field.Right - field.PageBoxLeft, field.Top - field.PageBoxBottom);
                    if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right)
                        || !double.IsFinite(bounds.Top) || bounds.Width < 0 || bounds.Height < 0
                        || regions.Any(region => region.PageIndex == page && region.Bounds.Left <= bounds.Right
                            && region.Bounds.Right >= bounds.Left && region.Bounds.Bottom <= bounds.Top && region.Bounds.Top >= bounds.Bottom))
                        retiredFields.Add(field.FieldName);
                }

            Exception? failure = null;
            PdfContentRedactionResult? result = null;
            HideRedactionBar();
            SetTool(EditTool.Select);
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true, finalizeSavedFile: path =>
                {
                    // The save can leave a page needing an image this plan did not ask about (#499): ask now.
                    result = PdfEngineIntegration.ApplyRedaction(path, regions, approved,
                        pages => Dispatcher.Invoke(() => ApproveEveryRasterPage(pages)));
                }, onPublished: () =>
                {
                    foreach (var (page, annotation) in covered)
                        if (_annotations.TryGetValue(page, out var list))
                            list.RemoveAll(item => item.HistoryId == annotation.HistoryId);
                    foreach (string field in retiredFields)
                    {
                        _formTextValues.Remove(field);
                        _formChoiceValues.Remove(field);
                        _formMultiChoiceValues.Remove(field);
                        _formCheckValues.Remove(field);
                        _formRadioValues.Remove(field);
                        _formFontSizes.Remove(field);
                        _formTextStyles.Remove(field);
                    }
                    _redactMarks.RemoveAll(mark => !skipped.Contains(mark.PageIndex));
                });
            }
            catch (Exception ex) { failure ??= ex; }
            finally { Mouse.OverrideCursor = null; }

            if (failure is OperationCanceledException)
            {
                ReportResult(Loc("Str_Redact_NothingApplied"));
                return;
            }
            if (failure is not null || result is null)
            {
                string reason = failure is PdfRedactionVerificationException
                    ? Loc("Str_Redact_VerifyFailed") : failure?.Message ?? string.Empty;
                ReportResult(string.Format(Loc("Str_Redact_Failed"), reason));
                return;
            }
            UpdateRedactionCount();
            RepaintRedactionMarks();
            int pages = regions.Select(region => region.PageIndex).Distinct().Count();
            if (_redactMarks.Count == 0) _redactionSourceFingerprint = null;
            else
            {
                try { _redactionSourceFingerprint = CaptureCurrentSearchSnapshot().Fingerprint(); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            string status = approved.Count == 0
                ? string.Format(Loc("Str_Redacted"), regions.Count, pages)
                : string.Format(Loc("Str_RedactedRaster"), regions.Count, pages, approved.Count);
            if (skipped.Count > 0)
                status += " " + string.Format(Loc("Str_Redact_SkippedPages"), string.Join(", ", skipped.Order().Select(page => page + 1)));
            ReportResult(status);
            FinishRedaction();
        }

        // After a redaction is applied: offer to remove the hidden information the document can
        // still carry, then save the result as a new _Redacted copy so the original stays as it was.
        private void FinishRedaction()
        {
            if (_doc is null || Window.GetWindow(this) is not Window owner) return;
            string source = _originalFile ?? _currentFile ?? "document.pdf";
            string stem = System.IO.Path.GetFileNameWithoutExtension(source);
            string suggested = (stem.EndsWith("_Redacted", StringComparison.OrdinalIgnoreCase) ? stem : stem + "_Redacted") + ".pdf";
            var dialog = new RedactionFinishDialog(owner, suggested);
            bool finished = dialog.ShowDialog() == true;
            // Skipped, cancelled or with boxes unticked, the words may still sit in a bookmark, a
            // destination name, a form value or a script, so the finished file is checked either way.
            if (!finished || dialog.Choices is null)
            {
                WarnAboutSurvivingText(owner);
                if (!finished) return;
            }
            if (dialog.Choices is PdfEngineIntegration.SanitizeChoices choices)
            {
                Exception? failure = null;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    SaveTempAndReload(keepAnnotations: true, preserveZoom: true, finalizeSavedFile: path =>
                    {
                        try { PdfEngineIntegration.SanitizeFile(path, choices); }
                        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                            or ArgumentException or FormatException or System.IO.IOException) { failure = ex; }
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { failure ??= ex; }
                finally { Mouse.OverrideCursor = null; }
                if (failure is not null)
                {
                    ReportResult(string.Format(Loc("Str_RedactFinish_Failed"), failure.Message));
                    return;
                }
                if (choices.FormFields)
                {
                    // The fields are gone, so values typed into them have nothing left to fill.
                    _formTextValues.Clear();
                    _formChoiceValues.Clear();
                    _formMultiChoiceValues.Clear();
                    _formCheckValues.Clear();
                    _formRadioValues.Clear();
                    _formFontSizes.Clear();
                    _formTextStyles.Clear();
                }
                ReportResult(Loc("Str_RedactFinish_Done"));
                WarnAboutSurvivingText(owner);
            }
            if (dialog.SaveCopy)
            {
                Host!.FocusViewer(this);
                Host.SaveAsWithName(suggested);
            }
        }

        // A warning, not a refusal: a page skipped from image replacement or a mark taken off by hand
        // leaves copies the user chose to keep, so only they can say whether a remaining one is wrong.
        private void WarnAboutSurvivingText(Window owner)
        {
            if (_redactFoundText.Count == 0 || _currentFile is null) return;
            string[] terms = [.. _redactFoundText];
            if (_redactMarks.Count == 0) _redactFoundText.Clear();
            PdfRedactionVerificationReport report;
            bool checkFailed = false;
            try { report = PdfEngineIntegration.FindSurvivingText(_currentFile, terms, matchCase: false); }
            catch (Exception error) when (error is System.IO.IOException or FormatException or InvalidOperationException
                or NotSupportedException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                // A check that could not run proves nothing, so every word is named as possibly left.
                report = new PdfRedactionVerificationReport([]);
                checkFailed = true;
            }
            if (report.Succeeded && !checkFailed) return;
            string[] left = [.. terms.Where(term => report.Findings.Any(finding =>
                finding.Message.Contains("'" + term + "'", StringComparison.OrdinalIgnoreCase)))];
            string words = string.Join(", ", (left.Length > 0 ? left : terms).Select(term => "“" + term + "”"));
            KillerDialog.Show(owner, string.Format(Loc("Str_Redact_TextSurvives"), words), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Pages whose marked content can't be removed by itself are only turned into an image
        // with the user's say-so, one page at a time. False means the user canceled.
        /// <summary>
        /// Asks about pages a redaction found it needs images for only once the file was saved. Those pages can carry
        /// no region of their own (they share covered content), so they cannot be skipped: every one is approved or
        /// the redaction is cancelled.
        /// </summary>
        internal bool ApproveEveryRasterPage(IReadOnlyList<int> rasterPages, Window? reviewOwner = null)
        {
            var approved = new List<int>();
            var skipped = new HashSet<int>();
            return ApproveRasterPages(rasterPages, approved, skipped, reviewOwner) && skipped.Count == 0;
        }

        internal bool ApproveRasterPages(IReadOnlyList<int> rasterPages, List<int> approved, HashSet<int> skipped,
            Window? reviewOwner = null)
        {
            bool approveRest = false;
            Window? owner = reviewOwner ?? Window.GetWindow(this);
            for (int index = 0; index < rasterPages.Count; index++)
            {
                int page = rasterPages[index];
                if (approveRest) { approved.Add(page); continue; }
                bool more = index < rasterPages.Count - 1;
                string[] labels = more
                    ? [Loc("Str_Redact_ConvertPage"), Loc("Str_Redact_ConvertAll"), Loc("Str_Redact_SkipPage"), Loc("Str_Btn_Cancel")]
                    : [Loc("Str_Redact_ConvertPage"), Loc("Str_Redact_SkipPage"), Loc("Str_Btn_Cancel")];
                int choice = KillerDialog.ShowChoices(owner,
                    string.Format(Loc("Str_Redact_RasterPrompt"), page + 1), labels, 0, Loc("Str_Lbl_Redact"));
                if (!more && choice >= 1) choice++;   // align the two-button layout with the four-button one
                switch (choice)
                {
                    case 0: approved.Add(page); break;
                    case 1: approved.Add(page); approveRest = true; break;
                    case 2: skipped.Add(page); break;
                    default: return false;
                }
            }
            return true;
        }

        /// <summary>The areas marked with the Redact tool on the active document, for a macro's Redact step.</summary>
        internal List<PdfRedactionRegion> PendingRedactionRegions() => [.. _redactMarks.Select(ToRegion)];

        // Unsaved overlay annotations drawn over a redacted area would be burned back in on save,
        // so they go with the content underneath once the redaction succeeds.
        private List<(int Page, PageAnnotation Annotation)> CoveredAnnotations(IReadOnlyList<PdfRedactionRegion> regions)
        {
            var covered = new List<(int, PageAnnotation)>();
            foreach (var group in regions.GroupBy(region => region.PageIndex))
            {
                if (!_annotations.TryGetValue(group.Key, out var annotations) || annotations.Count == 0) continue;
                if (!TryPageGeometry(group.Key, out double pw, out double ph, out double cw, out double ch, out int rot))
                    continue;
                Rect[] areas = [.. group.Select(region => RedactionGeometry.PageToCanvas(region.Bounds.Left,
                    region.Bounds.Bottom, region.Bounds.Right, region.Bounds.Top, pw, ph, cw, ch, rot))];
                foreach (PageAnnotation annotation in annotations)
                    if (areas.Any(rect => RedactionGeometry.Overlaps(AnnotBounds(annotation), rect)))
                        covered.Add((group.Key, annotation));
            }
            return covered;
        }
    }
}
