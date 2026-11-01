using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        private object? _pendingSignDocument;
        private Controls.PdfViewer? _pendingSignViewer;
        private IInputElement? _signaturePopupReturnFocus;

        private bool HandleSignaturePickerKey(KeyEventArgs e)
        {
            if (_signaturePopup is not { IsKeyboardFocusWithin: true }) return false;
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null) SetTool(EditTool.Select);
                e.Handled = true;
                return true;
            }
            return (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Enter or Key.Space)
                || (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift);
        }

        private void LoadSignatures()
        {
            _signatureStore.Load();
            if (_signatureStore.PreservedDamagedFile is { } kept)
                Dispatcher.BeginInvoke(() => KillerDialog.Show(this,
                    string.Format(Loc("Str_Warn_SignatureLibraryDamaged"), kept), Loc("Str_Dlg_AppTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private bool PersistSignatures() => _signatureStore.Persist();

        // Rebuild the signature popup (if open) so its Loc()-built labels - section headers, pen sizes -
        // switch immediately on a language change rather than only on the next open.
        private void RefreshSignaturePopupLanguage()
        {
            if (_signaturePopup is not null) ShowSignaturePopup();
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = sig.Kind == SignatureKind.Initials ? 0.3 : 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                StrokeWidth = sig.StrokeWidth,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight,
                ImageData = sig.ImageData,
                Vectors = sig.Vectors is null ? null : [.. sig.Vectors]
            };

            // Drawn signature - convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([..stroke.Select(p => new Point(p.X, p.Y))]);
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);

            // Auto-switch to Select and select the placed signature so the user
            // can immediately reposition or resize without an extra click.
            SetTool(EditTool.Select);
            double sigW = sig.CanvasWidth * scale;
            double sigH = sig.CanvasHeight * scale;
            SelectAnnotation(annot, new Rect(pos.X, pos.Y, sigW, sigH));
            SetStatus(Loc("Str_St_SignaturePlaced"));
        }

        // Already signed -> change/remove menu. Otherwise drop the reusable choice, or open the
        // picker the first time and route the pick back to this field.
        private void FillSignField(bool initials, int objNum, int pageIndex, double x, double y, double w, double h)
        {
            if (IsFieldSigned(objNum, pageIndex))
            {
                ShowSignedFieldMenu(initials, objNum, pageIndex, x, y, w, h);
                return;
            }
            var choice = initials ? _activeInitialsChoice : _activeSignatureChoice;
            if (choice is null)
            {
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                _pendingSignDocument = _doc;
                _pendingSignViewer = ActiveViewer;
                ShowSignaturePopup();
                SetStatus(initials ? Loc("Str_Sig_Initials") : Loc("Str_Sig_Signatures"));
                return;
            }
            DropSignatureInField(objNum, choice, pageIndex, x, y, w, h);
        }

        // Re-clicking a signed field: change (re-pick) or remove it.
        private void ShowSignedFieldMenu(bool initials, int objNum, int pageIndex, double x, double y, double w, double h)
        {
            var menu = MakeThemedMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.Items.Add(MakeMenuItem(Loc(initials ? "Str_Sig_ChangeInitials" : "Str_Sig_ChangeSignature"), (_, _) =>
            {
                RemoveSignedField(objNum, pageIndex);
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                _pendingSignDocument = _doc;
                _pendingSignViewer = ActiveViewer;
                ShowSignaturePopup();
            }, glyph: ""));
            menu.Items.Add(MakeMenuItem(Loc(initials ? "Str_Sig_RemoveInitials" : "Str_Sig_RemoveSignature"),
                (_, _) => RemoveSignedField(objNum, pageIndex), glyph: ""));
            menu.IsOpen = true;
        }

        // A field counts as signed only while its signature is still on that page of the
        // document now shown. Object numbers repeat across documents and change after page
        // edits, so an entry whose annotation is gone is stale and is dropped.
        private bool IsFieldSigned(int objNum, int pageIndex)
        {
            if (!_signedFields.TryGetValue(objNum, out var annot)) return false;
            if (_annotations.TryGetValue(pageIndex, out var list) && list.Contains(annot)) return true;
            _signedFields.Remove(objNum);
            return false;
        }

        // Deletes the signature placed in a field and clears its signed state.
        private void RemoveSignedField(int objNum, int pageIndex)
        {
            if (!IsFieldSigned(objNum, pageIndex) || !_signedFields.TryGetValue(objNum, out var annot)) return;
            if (_annotations.TryGetValue(pageIndex, out var list)) list.Remove(annot);
            _signedFields.Remove(objNum);
            RenderAllAnnotations(pageIndex);
            MarkDirty(true);
            SetStatus(Loc("Str_St_FieldCleared"));
        }

        // Places a SignatureAnnotation centered in and scaled to fit the field rectangle.
        private void DropSignatureInField(int objNum, SavedSignature sig, int pageIndex, double x, double y, double w, double h)
        {
            const double pad = 2;
            double sw = sig.CanvasWidth, sh = sig.CanvasHeight;

            // Older or damaged signature-store entries can carry zero or non-finite canvas
            // dimensions. Dividing the field size by those values produces an infinite scale,
            // which later crashes WPF when the annotation is assigned an infinite Height (#181).
            if (double.IsNaN(sw) || double.IsInfinity(sw) || sw <= 0) sw = 400;
            if (double.IsNaN(sh) || double.IsInfinity(sh) || sh <= 0) sh = 150;
            double scale = Math.Min((w - 2 * pad) / sw, (h - 2 * pad) / sh);
            if (scale <= 0) scale = Math.Min(w / sw, h / sh);
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 0.5;
            double drawW = sw * scale, drawH = sh * scale;
            double px = x + (w - drawW) / 2;
            double py = y + (h - drawH) / 2;

            var annot = new SignatureAnnotation
            {
                PageIndex    = pageIndex,
                Position     = new Point(px, py),
                Scale        = scale,
                StrokeWidth  = sig.StrokeWidth,
                SourceWidth  = sw,
                SourceHeight = sh,
                ImageData    = sig.ImageData,
                Vectors      = sig.Vectors is null ? null : [.. sig.Vectors],
            };
            if (sig.ImageData is null)
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([.. stroke.Select(pt => new Point(pt.X, pt.Y))]);

            _signedFields[objNum] = annot;
            AddAnnotation(annot);
            RenderAllAnnotations(pageIndex);
            MarkDirty(true);
            SetStatus(Loc("Str_St_FieldSigned"));
        }

        private void HideSignaturePopup(bool cancelPendingField = true)
        {
            if (cancelPendingField)
            {
                _pendingSignField = null;
                _pendingSignDocument = null;
                _pendingSignViewer = null;
            }
            if (_signaturePopup is not null)
            {
                var popup = _signaturePopup;
                bool restoreFocus = popup.IsKeyboardFocusWithin;
                popup.IsEnabled = false;
                var previewGrid = popup.Parent as Panel;
                _signaturePopup = null;   // detach now so a re-open builds a fresh popup
                if (cancelPendingField)
                {
                    if (restoreFocus)
                    {
                        if (_signaturePopupReturnFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
                            Keyboard.Focus(previous);
                        else Focus();
                    }
                    _signaturePopupReturnFocus = null;
                }
                // Fade out (it's a Border, not a Window, so WindowFx doesn't apply) then remove.
                var fade = new DoubleAnimation(popup.Opacity, 0,
                    new Duration(TimeSpan.FromMilliseconds(WindowFx.FadeMs)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (_, _) => previewGrid?.Children.Remove(popup);
                popup.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

        // ---- Draggable floating panels (signature popup, settings panel) -------------------------

        /// <summary>
        /// Clamps a Left/Top so a panel stays fully inside its container's bounds.
        /// </summary>
        private static void ClampPanelToBounds(FrameworkElement panel, FrameworkElement bounds,
                                               ref double left, ref double top)
        {
            double w = panel.ActualWidth  > 0 ? panel.ActualWidth  : panel.Width;
            double h = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
            if (double.IsNaN(w)) w = 0;
            if (double.IsNaN(h)) h = 0;
            double maxLeft = Math.Max(0, bounds.ActualWidth  - w);
            double maxTop  = Math.Max(0, bounds.ActualHeight - h);
            left = Math.Max(0, Math.Min(maxLeft, left));
            top  = Math.Max(0, Math.Min(maxTop,  top));
        }

        /// <summary>
        /// Positions a Left/Top-aligned floating panel from its saved position, falling back to a
        /// top-right inset when nothing is stored. Always clamped inside <paramref name="bounds"/>.
        /// Must run after layout so the panel's ActualWidth/Height are known.
        /// </summary>
        private static void ApplySavedPanelPosition(FrameworkElement panel, FrameworkElement bounds, string keyPrefix,
                                             double fallbackRightInset, double fallbackTop)
        {
            double w = panel.ActualWidth > 0 ? panel.ActualWidth : (double.IsNaN(panel.Width) ? 0 : panel.Width);
            double left, top;
            if (int.TryParse(App.GetSetting(keyPrefix + "Left"), out int sl) &&
                int.TryParse(App.GetSetting(keyPrefix + "Top"),  out int st))
            {
                left = sl; top = st;
            }
            else
            {
                left = bounds.ActualWidth - w - fallbackRightInset;
                top  = fallbackTop;
            }
            ClampPanelToBounds(panel, bounds, ref left, ref top);
            panel.Margin = new Thickness(left, top, 0, 0);
        }

        /// <summary>
        /// Makes <paramref name="handle"/> drag <paramref name="panel"/> within <paramref name="bounds"/>,
        /// clamped to stay inside, and persists the resulting position under <paramref name="keyPrefix"/>.
        /// </summary>
        private static void EnablePanelDrag(FrameworkElement handle, FrameworkElement panel, FrameworkElement bounds,
                                     string keyPrefix)
        {
            handle.Cursor = DragCursors.Open;
            Point start = default;
            Thickness orig = default;
            bool dragging = false;

            // A capture lost to an alt-tab or a dialog never reaches the button-up handler, which
            // would strand the closed hand on screen for the rest of the session.
            handle.LostMouseCapture += (s, e) => { dragging = false; DragCursors.EndDrag(); };
            handle.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(bounds);
                orig  = panel.Margin;
                handle.CaptureMouse();
                DragCursors.BeginDrag();
                e.Handled = true;
            };
            handle.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(bounds);
                double nl = orig.Left + (p.X - start.X);
                double nt = orig.Top  + (p.Y - start.Y);
                ClampPanelToBounds(panel, bounds, ref nl, ref nt);
                panel.Margin = new Thickness(nl, nt, 0, 0);
            };
            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                handle.ReleaseMouseCapture();
                DragCursors.EndDrag();
                App.SetSetting(keyPrefix + "Left", ((int)panel.Margin.Left).ToString());
                App.SetSetting(keyPrefix + "Top",  ((int)panel.Margin.Top).ToString());
                e.Handled = true;
            };
        }

        private static void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double scaleX = targetW / sig.CanvasWidth;
            double scaleY = targetH / sig.CanvasHeight;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sig.CanvasWidth * scale) / 2;
            double offsetY = (targetH - sig.CanvasHeight * scale) / 2;

            if (sig.Vectors is { Count: > 0 })
            {
                foreach (var shape in SignatureVectorGeometry.Shapes(sig.Vectors, offsetX, offsetY, scale))
                    canvas.Children.Add(shape);
                return;
            }

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = Math.Max(0.8, sig.StrokeWidth * scale),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void ShowSignaturePopup()
        {
            if (_signaturePopup is null) _signaturePopupReturnFocus = Keyboard.FocusedElement;
            // NOTE: this popup is rebuilt on every open. All event handlers here are lambdas
            // on the popup's own child elements - no external source subscriptions, so no leak.
            // If SignatureStore.Signatures ever becomes ObservableCollection and this popup
            // subscribes to CollectionChanged, use CollectionChangedEventManager instead of +=.
            HideSignaturePopup(cancelPendingField: false);

            bool classicCaption = FindResource("UseDialogCaption") is true;
            var stack = new StackPanel { Margin = classicCaption ? new Thickness(0) : new Thickness(8) };

            // Title doubles as a drag handle so the user can move the popup anywhere inside the
            // document area (position is remembered). Wrapped in a transparent Border so the whole
            // title strip is grabbable, not just the text glyphs.
            var sigHeaderGrid = new Grid();
            sigHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sigHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sigTitleText = new TextBlock
            {
                Text = Loc("Str_Sig_Title"),
                Foreground = (Brush)FindResource(classicCaption ? "ChromeTextBrush" : "PrimaryBrush"),
                // Lacquer, the "PDF" wordmark face, on the flat themes. It has no bold cut, and its
                // deep drips hang below the line, so the bottom margin is pulled in to keep the
                // header row the same height.
                FontFamily = classicCaption
                    ? (FontFamily)FindResource("ChromeFontFamily")
                    : UiKit.WordmarkFontPdf,
                FontWeight = classicCaption ? FontWeights.Bold : FontWeights.Normal,
                FontSize = classicCaption ? 11 : 18,
                Margin = classicCaption ? new Thickness(0) : new Thickness(4, 0, 4, -4),
                VerticalAlignment = VerticalAlignment.Center,
                Effect = classicCaption ? null : new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 1, Direction = 270, Opacity = 0.7 }
            };
            Grid.SetColumn(sigTitleText, 0);
            void ClosePicker()
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
            }

            FrameworkElement sigCloseControl;
            if (classicCaption)
            {
                var closeButton = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FocusVisualStyle = null,
                    Style = (Style)FindResource("ChromeCloseButton")
                };
                closeButton.SetResourceReference(FrameworkElement.WidthProperty, "DialogCloseWidth");
                closeButton.SetResourceReference(FrameworkElement.HeightProperty, "DialogCloseHeight");
                closeButton.SetResourceReference(FrameworkElement.MarginProperty, "DialogCaptionButtonsMargin");
                System.Windows.Automation.AutomationProperties.SetName(closeButton, Loc("Str_Lbl_Close"));
                closeButton.Click += (_, _) => ClosePicker();
                sigCloseControl = closeButton;
            }
            else
            {
                var closeText = new Glyph
                {
                    Text = "",
                    FontFamily = UiKit.IconFont,
                    FontSize = 11,
                    Foreground = (SolidColorBrush)FindResource("MutedTextBrush"),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(4)
                };
                closeText.MouseEnter += (_, _) => { closeText.Foreground = (SolidColorBrush)FindResource("DangerRed"); closeText.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5 }; };
                closeText.MouseLeave += (_, _) => { closeText.Foreground = (SolidColorBrush)FindResource("MutedTextBrush"); closeText.Effect = null; };
                var closeButton = new Button { Content = closeText, Style = (Style)FindResource("ToolbarButton"),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
                System.Windows.Automation.AutomationProperties.SetName(closeButton, Loc("Str_Lbl_Close"));
                closeButton.Click += (_, _) => ClosePicker();
                sigCloseControl = closeButton;
            }
            Grid.SetColumn(sigCloseControl, 1);
            sigHeaderGrid.Children.Add(sigTitleText);
            sigHeaderGrid.Children.Add(sigCloseControl);
            var sigHeader = new Border
            {
                Background = classicCaption ? (Brush)FindResource("DialogTitleBarBrush") : Brushes.Transparent,
                Height = classicCaption && FindResource("DialogTitleBarHeight") is double captionHeight
                    ? captionHeight : double.NaN,
                Margin = classicCaption ? new Thickness(0) : new Thickness(0, 0, 0, 4),
                Child = sigHeaderGrid
            };
            if (classicCaption)
                sigHeaderGrid.SetResourceReference(FrameworkElement.MarginProperty, "TitleBarPadding");
            stack.Children.Add(sigHeader);

            // Saved signatures and initials, shown as two labeled sections so the HR-style
            // "initial here, sign there" flow can pick each independently. One tile builder is shared.
            UIElement MakeSigItem(SavedSignature sigCopy)
            {
                void Activate(MouseButtonEventArgs? mouse)
                {
                    var pendingField = _pendingSignField;
                    var pendingDocument = _pendingSignDocument;
                    var pendingViewer = _pendingSignViewer;
                    HideSignaturePopup();
                    if (pendingField is { } tgt
                        && ReferenceEquals(pendingDocument, _doc)
                        && ReferenceEquals(pendingViewer, ActiveViewer))
                    {
                        if (tgt.Initials) _activeInitialsChoice = sigCopy; else _activeSignatureChoice = sigCopy;
                        _pendingSignature = null;
                        DropSignatureInField(tgt.ObjNum, sigCopy, tgt.Page, tgt.X, tgt.Y, tgt.W, tgt.H);
                        return;
                    }
                    _pendingSignature = sigCopy;
                    if (mouse is not null) ArmSignaturePickerDrag(sigCopy, mouse);
                    _annotationCanvas.Cursor = Cursors.Cross;
                    SetStatus(sigCopy.Kind == SignatureKind.Initials
                        ? Loc("Str_St_PlaceInitials") : Loc("Str_St_PlaceSignature"));
                }
                var item = new SavedSignatureButton(
                    Loc(sigCopy.Kind == SignatureKind.Initials ? "Str_Sig_Initials" : "Str_Sig_Signatures")
                        + ": " + sigCopy.Name, Activate)
                {
                    Background = Brushes.White,
                    BorderBrush = _swatchDimBorder,
                    BorderThickness = new Thickness(1),
                    Style = (Style)FindResource("DarkButton"),
                    Margin = new Thickness(4, 2, 4, 2),
                    Padding = new Thickness(4),
                    Cursor = Cursors.Hand,
                    Height = 60,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, "SavedSignature_" + sigCopy.Id);

                if (sigCopy.ImageData is not null)
                {
                    try
                    {
                        var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                        var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                        bmpImg.BeginInit();
                        bmpImg.StreamSource = new System.IO.MemoryStream(imgBytes);
                        bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmpImg.EndInit();
                        item.Content = new System.Windows.Controls.Image
                        {
                            Source = bmpImg,
                            Height = 50,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            IsHitTestVisible = false
                        };
                    }
                    catch { item.Content = new TextBlock { Text = $"({Loc("Str_Annot_Image")})", IsHitTestVisible = false }; }
                }
                else
                {
                    var canvas = new Canvas
                    {
                        Width = 288, Height = 50,
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    RenderSignaturePreview(canvas, sigCopy, 288, 50);
                    item.Content = canvas;
                }

                item.MouseEnter += (s, e) =>
                    ((Button)s!).BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush");
                item.MouseLeave += (s, e) =>
                    ((Button)s!).BorderBrush = _swatchDimBorder;

                var itemGrid = new Grid();
                itemGrid.Children.Add(item);

                var delBtn = new Button
                {
                    Content = "",
                    FontSize = 10,
                    Width = 18, Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 2, 0),
                    Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                    Foreground = (SolidColorBrush)FindResource("DangerRed"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Style = (Style)FindResource("ToolbarButton")
                };
                System.Windows.Automation.AutomationProperties.SetName(delBtn, Loc("Str_Ctx_BmDelete") + ": " + sigCopy.Name);
                System.Windows.Automation.AutomationProperties.SetAutomationId(delBtn, "DeleteSignature_" + sigCopy.Id);
                delBtn.Click += (s, e) =>
                {
                    _signatureStore.Remove(sigCopy);
                    if (!PersistSignatures()) SetStatus(Loc("Str_Err_SignatureLibrarySave"));
                    ShowSignaturePopup(); // refresh
                };
                itemGrid.Children.Add(delBtn);
                return itemGrid;
            }

            // Section = header + saved tiles (or a "none yet" hint) + Create/Import for that Kind.
            void AddSigSection(string sectionTitle, SignatureKind kind)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = sectionTitle,
                    Foreground = (SolidColorBrush)FindResource("MutedTextBrush"),
                    FontFamily = UiKit.UiFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Margin = new Thickness(4, 6, 4, 2)
                });

                var items = _signatureStore.Signatures.Where(x => x.Kind == kind).ToList();
                if (items.Count > 0)
                {
                    var scroll = new ScrollViewer
                    {
                        MaxHeight = 170,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                    };
                    var listPanel = new StackPanel();
                    foreach (var sig in items)
                        listPanel.Children.Add(MakeSigItem(sig));
                    scroll.Content = listPanel;
                    stack.Children.Add(scroll);
                }
                else
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = Loc("Str_Sig_None"),
                        Foreground = (SolidColorBrush)FindResource("MutedTextBrush"),
                        FontFamily = UiKit.UiFont,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(4, 2, 4, 6),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }

                var rowBtns = new Grid { Margin = new Thickness(4, 8, 4, 2) };
                rowBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var createBtn = UiKit.Make(Loc("Str_Sig_Create"), accent: true);
                createBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                createBtn.Margin = new Thickness(0, 0, 3, 0);
                createBtn.Click += (s, e) => AddSignatureFromPicker(() => OpenSignatureCreator(kind));
                var importBtn = UiKit.Make(Loc("Str_Sig_Import"), accent: false);
                importBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                importBtn.Margin = new Thickness(3, 0, 0, 0);
                importBtn.Click += (s, e) => AddSignatureFromPicker(() => ImportImageSignature(kind));
                Grid.SetColumn(createBtn, 0);
                Grid.SetColumn(importBtn, 1);
                rowBtns.Children.Add(createBtn);
                rowBtns.Children.Add(importBtn);
                stack.Children.Add(rowBtns);
            }

            AddSigSection(Loc("Str_Sig_Signatures"), SignatureKind.Signature);

            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("CardBorderBrush"),
                Margin = new Thickness(4, 6, 4, 2)
            });

            AddSigSection(Loc("Str_Sig_Initials"), SignatureKind.Initials);

            // Match the Settings/menu popups: themed modal surface + accent border + film grain.
            var sigContent = new Grid();
            var sigGrain = new Border
            {
                CornerRadius     = new CornerRadius(0),
                IsHitTestVisible = false,
                Opacity          = (double)FindResource("GrainOpacity"),
                Background       = (System.Windows.Media.Brush)FindResource("GrainBrushShared")
            };
            sigGrain.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");
            sigContent.Children.Add(sigGrain);
            sigContent.Children.Add(stack);

            var sigBody = new Border
            {
                Background = (Brush)FindResource("MenuBackgroundBrush"),
                Child = sigContent
            };
            sigBody.SetResourceReference(Border.MarginProperty, "DialogWindowFramePadding");
            sigBody.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");

            Border SignatureFrameRing(string brushKey, string thicknessKey, string? marginKey = null)
            {
                var ring = new Border { IsHitTestVisible = false };
                ring.SetResourceReference(Border.BorderBrushProperty, brushKey);
                ring.SetResourceReference(Border.BorderThicknessProperty, thicknessKey);
                if (marginKey is not null)
                    ring.SetResourceReference(Border.MarginProperty, marginKey);
                return ring;
            }

            var sigFrame = new Grid();
            sigFrame.Children.Add(sigBody);
            sigFrame.Children.Add(SignatureFrameRing("WindowFrameBrush", "DialogWindowFrameThickness"));
            sigFrame.Children.Add(SignatureFrameRing("FrameInnerLightBrush", "FrameInnerLightThickness", "FrameInnerMargin"));
            sigFrame.Children.Add(SignatureFrameRing("FrameInnerDarkBrush", "FrameInnerDarkThickness", "FrameInnerMargin"));
            sigFrame.Children.Add(SignatureFrameRing("FrameOuterLightBrush", "FrameOuterLightThickness"));
            sigFrame.Children.Add(SignatureFrameRing("FrameOuterDarkBrush", "FrameOuterDarkThickness"));

            _signaturePopup = new Border
            {
                Background = (SolidColorBrush)FindResource("MenuBackgroundBrush"),
                BorderBrush = (SolidColorBrush)FindResource("MenuBorderBrush"),
                BorderThickness = classicCaption ? new Thickness(0) : new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                Child = sigFrame,
                // Free-positioned (Left/Top) inside the document grid so it can be dragged; the
                // exact spot is set after layout from the saved position (or a default top-right).
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 16,
                    Opacity = FindResource("FlyoutShadowOpacity") is double shadowOpacity ? shadowOpacity : 0.55,
                    ShadowDepth = 3
                }
            };
            _signaturePopup.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");
            KeyboardNavigation.SetTabNavigation(_signaturePopup, KeyboardNavigationMode.Cycle);

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
                // Freshly-inserted element: defer the fade until it's laid out, otherwise the
                // animation is missed (unlike the always-present Settings/About overlays).
                _signaturePopup.Opacity = 0;
                var popup = _signaturePopup;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    if (popup is null) return;
                    if (!ReferenceEquals(popup, _signaturePopup)) return;
                    // Place it (saved position, or default to the old top-right spot) and wire up
                    // dragging now that ActualWidth/Height are known.
                    ApplySavedPanelPosition(popup, previewGrid, "SigPopup", fallbackRightInset: 80, fallbackTop: 4);
                    EnablePanelDrag(sigHeader, popup, previewGrid, "SigPopup");
                    popup.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    popup.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                }));
            }
        }

        private void OpenSignatureCreator(SignatureKind kind = SignatureKind.Signature)
        {
            var win = new Window
            {
                Title = Loc("Str_Sig_Create"),
                Width = 460,
                SizeToContent = SizeToContent.Height   // size to content so there's no empty padding below
            };
            DialogFrame.Configure(win, this);
            // This separate window can't see MainWindow's ChromeCloseCorner, so the close button's
            // {DynamicResource ChromeCloseCorner} fell back to 0 (square hover). Provide it here so the
            // hover rounds the top-right corner to match the window.
            win.Resources["ChromeCloseCorner"] = new CornerRadius(0, UiKit.RadWindow.TopRight, 0, 0);

            var contentArea = new StackPanel();

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                // Faint outline so the white drawing pane reads as a distinct field on the modal.
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = UiKit.RadControl,
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            // In a Canvas, alignment is ignored, so position the hint with a little padding rather
            // than leaving it jammed in the top-left corner. A script face suits a signature prompt.
            var placeholder = new TextBlock
            {
                Text = Loc("Str_Sig_DrawHere"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0xb0)),
                FontFamily = new FontFamily("Segoe Script, Segoe UI"),
                FontSize = 18,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(placeholder, 18);
            Canvas.SetTop(placeholder, 14);
            drawCanvas.Children.Add(placeholder);

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;
            double penWidth = 2.5;   // medium; set by the pen-width selector below

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = penWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Max(0, Math.Min(drawCanvas.ActualWidth, pos.X));
                pos.Y = Math.Max(0, Math.Min(drawCanvas.ActualHeight, pos.Y));
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Pen-width selector: three preset thicknesses, active one highlighted. On the left so the
            // modal does not read bottom-right-heavy.
            var penRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            penRow.Children.Add(new TextBlock { Text = Loc("Str_Sig_Pen"), Foreground = (SolidColorBrush)FindResource("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var penOptions = new (string Label, double W)[] { (Loc("Str_Sig_Thin"), 2.0), (Loc("Str_Sig_Medium"), 4.5), (Loc("Str_Sig_Thick"), 9.0) };
            var penBtns = new List<Button>();
            void RefreshPen()
            {
                for (int bi = 0; bi < penBtns.Count; bi++)
                {
                    bool active = Math.Abs(penOptions[bi].W - penWidth) < 0.01;
                    penBtns[bi].Background  = active ? (Brush)FindResource("SelectionBg") : (Brush)FindResource("PaneBrush");
                    penBtns[bi].Foreground  = active ? (SolidColorBrush)FindResource("SelectionFg") : (SolidColorBrush)FindResource("TextBrush");
                    penBtns[bi].BorderBrush = active ? (SolidColorBrush)FindResource("PrimaryBrush")      : (SolidColorBrush)FindResource("CardBorderBrush");
                }
            }
            foreach (var (lbl, w) in penOptions)
            {
                double ww = w;
                var pb = new Button
                {
                    Content = lbl,
                    Style = (Style)FindResource("DarkButton"),
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FontFamily = UiKit.UiFont,
                    FontSize = 11
                };
                pb.Click += (s2, e2) => { penWidth = ww; RefreshPen(); };
                penBtns.Add(pb);
                penRow.Children.Add(pb);
            }
            RefreshPen();

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var clearBtn = UiKit.Make(Loc("Str_Sig_Clear"), accent: false);
            clearBtn.Margin = new Thickness(0, 0, 8, 0);
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = UiKit.Make(Loc("Str_Sig_SaveSig"), accent: true);
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    KillerDialog.Show(this, Loc("Str_Dlg_DrawSignatureFirst"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    Kind = kind,
                    StrokeWidth = penWidth,
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"{(kind == SignatureKind.Initials ? "Initials" : "Signature")} {_signatureStore.Signatures.Count(x => x.Kind == kind) + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _signatureStore.Add(saved);
                bool kept = PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus(Loc(kept ? "Str_St_SignatureSaved" : "Str_Err_SignatureLibrarySave"));

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);

            // Pen-size selector on its own row above the buttons. A single shared row doesn't survive
            // longer translated labels (e.g. Bengali Clear/Save) - the last pen option ("Thick") clipped.
            contentArea.Children.Add(penRow);
            btnPanel.Margin = new Thickness(12, 4, 12, 12);
            contentArea.Children.Add(btnPanel);

            win.Content = DialogFrame.Frame(win, this, "KillerPDF - " + Loc("Str_Sig_Create"), () => win.Close(), contentArea);
            win.ShowDialog();
        }

        // A new signature is armed for placing, so the picker stays out of the way of the page. It
        // returns when nothing was added, or when a form field is still waiting for a pick.
        private void AddSignatureFromPicker(Action add)
        {
            SavedSignature? armed = _pendingSignature;
            HideSignaturePopup(cancelPendingField: false);
            add();
            if (_pendingSignField is not null || ReferenceEquals(armed, _pendingSignature)) ShowSignaturePopup();
        }

        private void ImportImageSignature(SignatureKind kind = SignatureKind.Signature)
        {
            var dlg = new Controls.ImageAssetPickerDialog(this, Loc("Str_Sign_ImportImage"), allowVector: true);
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.SelectedPath)) return;

            try
            {
                if (string.Equals(System.IO.Path.GetExtension(dlg.SelectedPath), ".svg", StringComparison.OrdinalIgnoreCase))
                {
                    ImportVectorSignature(dlg.SelectedPath, dlg.SelectedName, kind);
                    return;
                }
                var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.SelectedPath));
                // A scanned or photographed signature carries its paper with it. The sensitivity
                // dialog drops the paper and lets the user lighten or darken the ink before saving.
                var inked = ShowSignatureSensitivityDialog(bmp);
                if (inked is null) return;
                byte[] pngBytes = SignatureInk.EncodePng(inked);

                var saved = new SavedSignature
                {
                    Kind = kind,
                    Name = dlg.SelectedName ?? System.IO.Path.GetFileNameWithoutExtension(dlg.SelectedPath),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _signatureStore.Add(saved);
                bool kept = PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus(Loc(kept ? "Str_St_ImageLoaded" : "Str_Err_SignatureLibrarySave"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ImportImageFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // An SVG signature (#398) keeps its outlines, fitted to the drawing pad's 400 by 150 so it
        // places at the same size a drawn signature does, and is saved and burned as vectors.
        private void ImportVectorSignature(string path, string? name, SignatureKind kind)
        {
            SvgSignatureDocument svg = SvgSignatureImporter.Import(path);
            var (vectors, width, height) = svg.Fit(400, 150);
            var saved = new SavedSignature
            {
                Kind = kind,
                Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path),
                CanvasWidth = width,
                CanvasHeight = height,
                Vectors = vectors
            };
            _signatureStore.Add(saved);
            bool kept = PersistSignatures();

            _pendingSignature = saved;
            _annotationCanvas.Cursor = Cursors.Cross;
            SetStatus(Loc(kept ? "Str_St_ImageLoaded" : "Str_Err_SignatureLibrarySave"));
        }

        // Preview of an imported signature with a Sensitivity slider. Returns the full-size ink on a
        // transparent background, or null when the user cancels.
        private System.Windows.Media.Imaging.BitmapSource? ShowSignatureSensitivityDialog(
            System.Windows.Media.Imaging.BitmapSource source)
        {
            var win = new Window
            {
                Title = Loc("Str_Sig_AdjustImage"),
                Width = 460,
                SizeToContent = SizeToContent.Height
            };
            DialogFrame.Configure(win, this);
            win.Resources["ChromeCloseCorner"] = new CornerRadius(0, UiKit.RadWindow.TopRight, 0, 0);

            var contentArea = new StackPanel();

            // White like the page the signature lands on, so the preview shows the real result.
            var previewBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = UiKit.RadControl,
                Height = 170,
                Padding = new Thickness(8)
            };
            var preview = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
            previewBorder.Child = preview;
            contentArea.Children.Add(previewBorder);

            // The slider re-inks a reduced copy so a large phone photo still follows the thumb.
            var previewSource = SignatureInk.PreviewSource(source, 900);
            byte[] previewPixels = SignatureInk.ReadPixels(previewSource, out int pw, out int ph);
            void RefreshPreview(double sensitivity) =>
                preview.Source = SignatureInk.ToBitmap(SignatureInk.Apply(previewPixels, sensitivity), pw, ph);

            var sensitivityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock MutedLabel(string text, Thickness margin)
            {
                var label = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
                label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                return label;
            }
            sensitivityRow.Children.Add(MutedLabel(Loc("Str_Sig_Sensitivity"), new Thickness(0, 0, 10, 0)));
            sensitivityRow.Children.Add(MutedLabel(Loc("Str_Sig_Lighter"), new Thickness(0, 0, 6, 0)));
            var sensitivitySlider = new Slider
            {
                Minimum = SignatureInk.MinSensitivity,
                Maximum = SignatureInk.MaxSensitivity,
                Value = SignatureInk.DefaultSensitivity,
                SmallChange = 1,
                LargeChange = 10,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Width = 160,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            System.Windows.Automation.AutomationProperties.SetName(sensitivitySlider, Loc("Str_Sig_Sensitivity"));
            sensitivityRow.Children.Add(sensitivitySlider);
            sensitivityRow.Children.Add(MutedLabel(Loc("Str_Sig_Darker"), new Thickness(6, 0, 0, 0)));
            sensitivitySlider.ValueChanged += (s, e) => RefreshPreview(e.NewValue);
            contentArea.Children.Add(sensitivityRow);
            RefreshPreview(sensitivitySlider.Value);

            System.Windows.Media.Imaging.BitmapSource? result = null;
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 10, 12, 12)
            };
            var cancelBtn = UiKit.Make(Loc("Str_Btn_Cancel"), accent: false);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.Click += (s, e) => win.Close();
            var saveBtn = UiKit.Make(Loc("Str_Sig_SaveSig"), accent: true);
            saveBtn.Click += (s, e) =>
            {
                byte[] pixels = SignatureInk.ReadPixels(source, out int w, out int h);
                result = SignatureInk.ToBitmap(SignatureInk.Apply(pixels, sensitivitySlider.Value), w, h);
                win.Close();
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            win.Content = DialogFrame.Frame(win, this, "KillerPDF - " + Loc("Str_Sig_AdjustImage"), () => win.Close(), contentArea);
            win.Loaded += (s, e) => sensitivitySlider.Focus();
            win.ShowDialog();
            return result;
        }
    }
}
