using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using KillerFamily.Accessibility;

namespace KillerPDF.Controls
{
    // The owner-restriction notice. A PDF whose owner password forbids editing opens read-only,
    // and this bar says so at the top of the pane with a button that asks for the owner password.
    // It follows the active tab, so RebuildTabStrip refreshes it on every open, switch and close.
    public partial class PdfViewer
    {
        private Border? _restrictionNotice;
        private TextBlock? _restrictionMessage;
        private Button? _restrictionUnlock;

        /// <summary>True while the active tab is a protected PDF opened read-only because its owner
        /// restricts editing.</summary>
        internal bool IsOwnerRestricted => _doc?.IsReadOnly == true && _openedFromProtected;

        // Closing the notice lasts for that document; the title keeps saying it is read-only.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _restrictionDismissed = [];
        private string? _titleBeforeRestriction;

        private void UpdateReadOnlyTitle()
        {
            if (Window.GetWindow(this) is not Window window) return;
            if (IsOwnerRestricted)
            {
                string name = System.IO.Path.GetFileName(_originalFile ?? "");
                string suffix = Host?.Loc("Str_Restricted_TitleSuffix") ?? "";
                FileNameLabel.Text = $"{name} - {suffix}";
                _titleBeforeRestriction ??= window.Title;
                window.Title = $"{name} - {suffix} - {_titleBeforeRestriction}";
            }
            else if (_titleBeforeRestriction is not null)
            {
                window.Title = _titleBeforeRestriction;
                _titleBeforeRestriction = null;
                FileNameLabel.Text = System.IO.Path.GetFileName(_originalFile ?? "");
            }
        }

        /// <summary>The notice sits over the document, which the app's scale leaves alone, so it takes
        /// that scale itself, as the toolbar and the sidebar do.</summary>
        internal void RescaleRestrictionNotice()
        {
            if (_restrictionNotice is null) return;
            double scale = ScaledDialogFit.OwnerScale<Window>(Window.GetWindow(this),
                window => window is Shell.MainWindow main ? main.AppScale : null, window => window.Owner);
            _restrictionNotice.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        internal void UpdateRestrictionNotice()
        {
            UpdateReadOnlyTitle();
            if (!IsOwnerRestricted || _restrictionDismissed.TryGetValue(_doc!, out _))
            {
                if (_restrictionNotice is not null) _restrictionNotice.Visibility = Visibility.Collapsed;
                return;
            }
            if (_restrictionNotice is null)
            {
                // Hosted where the annotation bars live, above the document and below the pane bevel.
                if (PagePreviewPanel.Parent is not Grid previewArea || Host is null) return;
                (_restrictionNotice, _restrictionMessage, _restrictionUnlock) = BuildRestrictionNotice(Host, () =>
                {
                    if (_doc is not null) _restrictionDismissed.AddOrUpdate(_doc, _doc);
                    UpdateRestrictionNotice();
                });
                previewArea.Children.Add(_restrictionNotice);
            }
            // A certificate recipient's permissions have no owner password to lift them.
            _restrictionMessage?.SetResourceReference(TextBlock.TextProperty,
                _restrictedByCertificate ? "Str_Restricted_CertificateNotice" : "Str_Restricted_Notice");
            if (_restrictionUnlock is not null)
                _restrictionUnlock.Visibility = _restrictedByCertificate ? Visibility.Collapsed : Visibility.Visible;
            RescaleRestrictionNotice();
            bool appearing = _restrictionNotice.Visibility != Visibility.Visible;
            _restrictionNotice.Visibility = Visibility.Visible;
            if (appearing && _restrictionMessage is not null)
                Dispatcher.BeginInvoke(() =>
                    UIElementAutomationPeer.CreatePeerForElement(_restrictionMessage)
                        ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged),
                    System.Windows.Threading.DispatcherPriority.Background);
        }

        // Same surface tokens, grain host and shadow as the floating annotation bars, centered at
        // the top of the pane.
        private static (Border Bar, TextBlock Message, Button Unlock) BuildRestrictionNotice(
            Features.IViewerHost host, Action dismiss)
        {
            var lockGlyph = new Glyph
            {
                Text = "\uE72E",
                FontFamily = UiKit.IconFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            lockGlyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var message = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            message.SetResourceReference(TextBlock.TextProperty, "Str_Restricted_Notice");
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);

            var unlock = UiKit.Make(host.Loc("Str_Restricted_EnterPassword"), accent: true);
            unlock.SetResourceReference(ContentControl.ContentProperty, "Str_Restricted_EnterPassword");
            unlock.Margin = new Thickness(12, 0, 0, 0);
            unlock.VerticalAlignment = VerticalAlignment.Center;
            unlock.Click += (_, _) => host.UnlockOwnerRestrictions();

            var close = new Button { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            close.SetResourceReference(FrameworkElement.StyleProperty, "OverlayCloseButton");
            close.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
            close.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_Restricted_Dismiss");
            AutomationProperties.SetName(close, host.Loc("Str_Restricted_Dismiss"));
            close.Click += (_, _) => dismiss();

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 2, 2) };
            DockPanel.SetDock(lockGlyph, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(unlock, Dock.Right);
            row.Children.Add(lockGlyph);
            row.Children.Add(close);
            row.Children.Add(unlock);
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
            return (bar, message, unlock);
        }
    }
}
