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
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _nativeNoticeDismissed = [];

        private void RescaleNativeNotice()
        {
            if (_nativeNotice is null) return;
            double scale = ScaledDialogFit.OwnerScale<Window>(Window.GetWindow(this),
                window => window is Shell.MainWindow main ? main.AppScale : null, window => window.Owner);
            _nativeNotice.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        private void UpdateNativeNotice()
        {
            // Ignored style rules alone are normal for real books, so only omissions raise the notice.
            if (_nativeDoc is not { HasOmissions: true } native || _nativeNoticeDismissed.TryGetValue(native, out _))
            {
                if (_nativeNotice is not null) _nativeNotice.Visibility = Visibility.Collapsed;
                return;
            }
            if (_nativeNotice is null)
            {
                if (PagePreviewPanel.Parent is not Grid previewArea || Host is null) return;
                (_nativeNotice, _nativeNoticeMessage) = BuildNativeNotice(Host, ShowNativeDiagnostics, () =>
                {
                    if (_nativeDoc is { } current) _nativeNoticeDismissed.AddOrUpdate(current, current);
                    UpdateNativeNotice();
                });
                previewArea.Children.Add(_nativeNotice);
            }
            RescaleNativeNotice();
            bool appearing = _nativeNotice.Visibility != Visibility.Visible;
            _nativeNotice.Visibility = Visibility.Visible;
            if (appearing && _nativeNoticeMessage is not null)
                Dispatcher.BeginInvoke(() =>
                    UIElementAutomationPeer.CreatePeerForElement(_nativeNoticeMessage)
                        ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged),
                    System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowNativeDiagnostics()
        {
            if (_nativeDoc is not { } native || Window.GetWindow(this) is not Window owner || Host is null) return;
            var rows = Services.NativeDocumentDiagnostics.Build(native, Host.Loc);
            new NativeDiagnosticsDialog(owner, rows).ShowDialog();
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
            message.SetResourceReference(TextBlock.TextProperty, "Str_Epub_Diagnostics_Notice");
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
