using System.Diagnostics;
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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        private void LoadSignatures() => _signatureStore.Load();

        private void PersistSignatures() => _signatureStore.Persist();

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
                ImageData = sig.ImageData
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
            if (_signedFields.ContainsKey(objNum))
            {
                ShowSignedFieldMenu(initials, objNum, pageIndex, x, y, w, h);
                return;
            }
            var choice = initials ? _activeInitialsChoice : _activeSignatureChoice;
            if (choice is null)
            {
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                ShowSignaturePopup();
                SetStatus(initials ? Loc("Str_Sig_Initials") : Loc("Str_Sig_Signatures"));
                return;
            }
            DropSignatureInField(objNum, choice, pageIndex, x, y, w, h);
        }

        // Re-clicking a signed field: change (re-pick) or remove it.
        private void ShowSignedFieldMenu(bool initials, int objNum, int pageIndex, double x, double y, double w, double h)
        {
            string what = initials ? "initials" : "signature";
            var menu = MakeThemedMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.Items.Add(MakeMenuItem("Change " + what, (_, _) =>
            {
                RemoveSignedField(objNum, pageIndex);
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                ShowSignaturePopup();
            }, glyph: ""));
            menu.Items.Add(MakeMenuItem("Remove " + what, (_, _) => RemoveSignedField(objNum, pageIndex), glyph: ""));
            menu.IsOpen = true;
        }

        // Deletes the signature placed in a field and clears its signed state.
        private void RemoveSignedField(int objNum, int pageIndex)
        {
            if (!_signedFields.TryGetValue(objNum, out var annot)) return;
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

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                var popup = _signaturePopup;
                _signaturePopup = null;   // detach now so a re-open builds a fresh popup
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
            // NOTE: this popup is rebuilt on every open. All event handlers here are lambdas
            // on the popup's own child elements - no external source subscriptions, so no leak.
            // If SignatureStore.Signatures ever becomes ObservableCollection and this popup
            // subscribes to CollectionChanged, use CollectionChangedEventManager instead of +=.
            HideSignaturePopup();

            bool classicCaption = FindResource("UseDialogCaption") is true;
            var stack = new StackPanel { Margin = classicCaption ? new Thickness(0) : new Thickness(4) };

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
                FontFamily = classicCaption
                    ? (FontFamily)FindResource("ChromeFontFamily")
                    : UiKit.MonoFont,
                FontWeight = FontWeights.Bold,
                FontSize = classicCaption ? 11 : 14,
                Margin = classicCaption ? new Thickness(0) : new Thickness(4, 2, 4, 2),
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
                closeButton.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; ClosePicker(); };
                sigCloseControl = closeButton;
            }
            else
            {
                var closeText = new TextBlock
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
                closeText.MouseLeftButtonDown += (_, e) => { e.Handled = true; ClosePicker(); };
                sigCloseControl = closeText;
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
                var item = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = _swatchDimBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(0),
                    Margin = new Thickness(4, 2, 4, 2),
                    Padding = new Thickness(4),
                    Cursor = Cursors.Hand,
                    Height = 60,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                item.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");

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
                        item.Child = new System.Windows.Controls.Image
                        {
                            Source = bmpImg,
                            Height = 50,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            IsHitTestVisible = false
                        };
                    }
                    catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
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
                    item.Child = canvas;
                }

                item.MouseLeftButtonDown += (s, e) =>
                {
                    HideSignaturePopup();
                    // If a signature field is waiting, fill it and remember the choice (pick once, reuse).
                    if (_pendingSignField is { } tgt)
                    {
                        if (tgt.Initials) _activeInitialsChoice = sigCopy; else _activeSignatureChoice = sigCopy;
                        var t = tgt; _pendingSignField = null;
                        DropSignatureInField(t.ObjNum, sigCopy, t.Page, t.X, t.Y, t.W, t.H);
                        return;
                    }
                    _pendingSignature = sigCopy;
                    _annotationCanvas.Cursor = Cursors.Cross;
                    SetStatus(sigCopy.Kind == SignatureKind.Initials
                        ? Loc("Str_St_PlaceInitials")
                        : Loc("Str_St_PlaceSignature"));
                };
                item.MouseEnter += (s, e) =>
                    ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush");
                item.MouseLeave += (s, e) =>
                    ((Border)s!).BorderBrush = _swatchDimBorder;

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
                delBtn.Click += (s, e) =>
                {
                    _signatureStore.Remove(sigCopy);
                    PersistSignatures();
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
                createBtn.Click += (s, e) => { HideSignaturePopup(); OpenSignatureCreator(kind); ShowSignaturePopup(); };
                var importBtn = UiKit.Make(Loc("Str_Sig_Import"), accent: false);
                importBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                importBtn.Margin = new Thickness(3, 0, 0, 0);
                importBtn.Click += (s, e) => { HideSignaturePopup(); ImportImageSignature(kind); ShowSignaturePopup(); };
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
                Padding = classicCaption ? new Thickness(0) : new Thickness(4),
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
                    // Place it (saved position, or default to the old top-right spot) and wire up
                    // dragging now that ActualWidth/Height are known.
                    ApplySavedPanelPosition(popup, previewGrid, "SigPopup", fallbackRightInset: 80, fallbackTop: 4);
                    EnablePanelDrag(sigHeader, popup, previewGrid, "SigPopup");
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
            DialogChrome.Configure(win, this);
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
                    penBtns[bi].Background  = active ? (SolidColorBrush)FindResource("SelectionBg") : (SolidColorBrush)FindResource("PaneBrush");
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
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus(Loc("Str_St_SignatureSaved"));

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);

            // Pen-size selector on its own row above the buttons. A single shared row doesn't survive
            // longer translated labels (e.g. Bengali Clear/Save) - the last pen option ("Thick") clipped.
            contentArea.Children.Add(penRow);
            btnPanel.Margin = new Thickness(12, 4, 12, 12);
            contentArea.Children.Add(btnPanel);

            win.Content = DialogChrome.Frame(win, this, "KillerPDF - " + Loc("Str_Sig_Create"), () => win.Close(), contentArea);
            win.ShowDialog();
        }

        private void ImportImageSignature(SignatureKind kind = SignatureKind.Signature)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Filter = Loc("Str_Filter_Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif|" + Loc("Str_Filter_AllFiles") + "|*.*",
                Title = Loc("Str_Sign_ImportImage"),
                ShowImagePreview = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.FileName));
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Kind = kind,
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _signatureStore.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus(Loc("Str_St_ImageLoaded"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_ImportImageFailed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
