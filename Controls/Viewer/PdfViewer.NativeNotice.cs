using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Accessibility;

namespace KillerPDF.Controls
{
    // The book diagnostics notice. A native book that opened without some chapters or items says so
    // at the top of the pane, and its Details button is the one way into the diagnostics panel.
    // It follows the active tab with the restriction notice, so RebuildTabStrip refreshes it.
    public partial class PdfViewer
    {
        private Border? _nativeNotice;
        private TextBlock? _nativeNoticeMessage;
        // One table for the process, keyed by the document: a dismissed notice stays dismissed when its tab moves to the
        // other pane or a new window, where a different viewer shows the same document.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> NativeNoticeDismissed = [];

        private void RescaleNativeNotice()
        {
            if (_nativeNotice is null) return;
            double scale = ScaledDialogFit.OwnerScale<Window>(Window.GetWindow(this),
                window => window is Shell.MainWindow main ? main.AppScale : null, window => window.Owner);
            // The narrowest window at a large app size leaves the pane narrower than the notice at that scale; it is
            // then drawn just small enough to fit, so its message and buttons stay whole.
            if (_nativeNotice.Parent is FrameworkElement pane && _nativeNoticeMessage is not null)
                scale = NoticePlacement.FitScale(scale, pane.ActualWidth,
                    NoticePlacement.MinimumWidth(_nativeNotice, _nativeNoticeMessage));
            double current = _nativeNotice.LayoutTransform is ScaleTransform applied ? applied.ScaleX : 1;
            if (Math.Abs(current - scale) > 0.0005)
                _nativeNotice.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        private void UpdateNativeNotice()
        {
            // This runs on every open, tab switch, close and move between panes or windows, so the viewer showing a
            // document is the one that follows its later notices.
            FollowNativeNoticesExt(_nativeDoc);
            // Ignored style rules alone are normal for real books, so only omissions raise the notice.
            if (_nativeDoc is not { HasOmissions: true } native || NativeNoticeDismissed.TryGetValue(native, out _))
            {
                if (_nativeNotice is not null) _nativeNotice.Visibility = Visibility.Collapsed;
                return;
            }
            if (_nativeNotice is null)
            {
                if (PagePreviewPanel.Parent is not Grid previewArea || Host is null) return;
                (_nativeNotice, _nativeNoticeMessage) = BuildNativeNotice(Host, ShowNativeDiagnostics, () =>
                {
                    if (_nativeDoc is { } current) NativeNoticeDismissed.AddOrUpdate(current, current);
                    UpdateNativeNotice();
                });
                previewArea.Children.Add(_nativeNotice);
                // The find bar floats over the same pane and is moved, opened and closed at will, so the notice
                // checks its place after every layout pass; the answer only changes when the find bar or the pane does.
                _nativeNotice.LayoutUpdated += (_, _) => PlaceNativeNotice();
                // Refitted after the pane's layout pass, since the fit measures the notice's content.
                previewArea.SizeChanged += (_, _) => Dispatcher.BeginInvoke(() => RescaleNativeNotice(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            RescaleNativeNotice();
            // One bar serves every tab, so it takes the wording of the active document's format each time.
            _nativeNoticeMessage?.SetResourceReference(TextBlock.TextProperty,
                Services.NativeDocumentDiagnostics.WordingFor(native.Format).Notice);
            bool appearing = _nativeNotice.Visibility != Visibility.Visible;
            _nativeNotice.Visibility = Visibility.Visible;
            if (appearing && _nativeNoticeMessage is not null)
                Dispatcher.BeginInvoke(() =>
                    UIElementAutomationPeer.CreatePeerForElement(_nativeNoticeMessage)
                        ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged),
                    System.Windows.Threading.DispatcherPriority.Background);
        }

        // Below the find bar while the two would overlap, at the top of the pane otherwise.
        private void PlaceNativeNotice()
        {
            if (_nativeNotice is not { Visibility: Visibility.Visible } notice) return;
            double top = NoticePlacement.TopClearOf(notice, Host?.SearchBar);
            if (Math.Abs(notice.Margin.Top - top) > 0.5) notice.Margin = new Thickness(0, top, 0, 0);
        }

        private void ShowNativeDiagnostics()
        {
            if (_nativeDoc is not { } native || Window.GetWindow(this) is not Window owner || Host is null) return;
            var host = Host;
            var rows = Services.NativeDocumentDiagnostics.Build(native, host.Loc);
            // A book still counting its pages says so, and the list fills in when counting ends.
            new NativeDiagnosticsDialog(owner, rows, () => Services.NativeDocumentDiagnostics.Build(native, host.Loc),
                () => Services.NativeDocumentDiagnostics.CountingNote(native, host.Loc),
                Services.NativeDocumentDiagnostics.WordingFor(native.Format)).ShowDialog();
        }

        private Services.NativeWorkingDocument? _noticesFollowed;
        private Action? _noticesChanged;

        /// <summary>
        /// Follows the notices of the native document this viewer shows: a DjVu page whose text is first read with
        /// characters it could not read raises the bar then, on the UI thread, if that document is still the one shown.
        /// Each viewer follows only the document it shows now, so a document moved to the other pane or torn out to a
        /// new window is followed by the viewer that shows it there. Repeat calls for the same document do nothing.
        /// </summary>
        internal void FollowNativeNoticesExt(Services.NativeWorkingDocument? document)
        {
            if (ReferenceEquals(_noticesFollowed, document)) return;
            _noticesChanged ??= () => Dispatcher.BeginInvoke(() =>
            {
                if (_noticesFollowed is { } followed && ReferenceEquals(_nativeDoc, followed)) UpdateNativeNotice();
            });
            if (_noticesFollowed is not null) _noticesFollowed.NoticesChanged -= _noticesChanged;
            _noticesFollowed = document;
            if (document is not null) document.NoticesChanged += _noticesChanged;
        }

        // Same surface tokens, grain host and shadow as the restriction notice, centered at the top of the pane.
        private static (Border Bar, TextBlock Message) BuildNativeNotice(Features.IViewerHost host,
            Action details, Action dismiss)
        {
            var infoGlyph = new Glyph
            {
                Text = "",
                FontFamily = UiKit.IconFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            infoGlyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var message = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);

            var open = UiKit.Make(host.Loc("Str_Epub_Diagnostics_Details"), accent: true);
            open.Name = "NativeDiagnosticsDetails";
            open.SetResourceReference(ContentControl.ContentProperty, "Str_Epub_Diagnostics_Details");
            open.Margin = new Thickness(12, 0, 0, 0);
            open.VerticalAlignment = VerticalAlignment.Center;
            open.Click += (_, _) => details();

            var close = new Button { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            close.SetResourceReference(FrameworkElement.StyleProperty, "OverlayCloseButton");
            close.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
            close.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_Restricted_Dismiss");
            AutomationProperties.SetName(close, host.Loc("Str_Restricted_Dismiss"));
            close.Click += (_, _) => dismiss();

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 2, 2) };
            DockPanel.SetDock(infoGlyph, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(infoGlyph);
            row.Children.Add(close);
            row.Children.Add(open);
            row.Children.Add(message);

            var bar = new Border
            {
                Child = host.BuildBarHost(row),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 640,
                Effect = host.AnnotBarShadow()
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AnnotationBarBrush");
            bar.SetResourceReference(Border.CornerRadiusProperty, "AnnotationBarCornerRadius");
            bar.SetResourceReference(Border.BorderBrushProperty, "AnnotationBarBorderBrush");
            bar.SetResourceReference(Border.BorderThicknessProperty, "BarEdgeThickness");
            bar.SetResourceReference(Border.PaddingProperty, "BarPadding");
            Panel.SetZIndex(bar, 90);
            return (bar, message);
        }
    }
}
