using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using KillerFamily.Accessibility;

namespace KillerPDF.Controls
{
    // #502: the Restore last session bar. With the setting on Ask, a plain launch opens the empty
    // window and this one line offers the stored session at the top of the pane, built like the book
    // diagnostics notice beside it. Nothing waits on it: the window works as usual meanwhile.
    public partial class PdfViewer
    {
        // A notice's inside: the menu card's grain behind the row, stopping inside the 1px edge.
        private static Grid NoticeCard(FrameworkElement row)
        {
            var grain = new Border { Margin = new Thickness(-7) };
            grain.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutGrain");
            var card = new Grid();
            card.Children.Add(grain);
            card.Children.Add(row);
            return card;
        }

        private Border? _sessionNotice;

        internal bool SessionNoticeShown => _sessionNotice is { Visibility: Visibility.Visible };

        internal void ShowSessionNotice(int fileCount, Action restore, Action dismiss)
        {
            if (PagePreviewPanel.Parent is not Grid previewArea || Host is null) return;
            HideSessionNotice();
            var host = Host;

            var glyph = new Glyph
            {
                Text = "",
                FontFamily = UiKit.IconFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var message = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);

            // The labels follow the language menu: the buttons look their words up live, and the
            // message, which carries the file count, is formatted again on every language change.
            var restoreButton = UiKit.Make(string.Empty, accent: true);
            restoreButton.SetResourceReference(ContentControl.ContentProperty, "Str_RestoreSession_Restore");
            restoreButton.Name = "RestoreSessionRestore";
            restoreButton.Margin = new Thickness(12, 0, 0, 0);
            restoreButton.VerticalAlignment = VerticalAlignment.Center;
            restoreButton.Click += (_, _) => { HideSessionNotice(); restore(); };

            var dismissButton = UiKit.Make(string.Empty, accent: false);
            dismissButton.SetResourceReference(ContentControl.ContentProperty, "Str_RestoreSession_Dismiss");
            dismissButton.Name = "RestoreSessionDismiss";
            dismissButton.Margin = new Thickness(6, 0, 0, 0);
            dismissButton.VerticalAlignment = VerticalAlignment.Center;
            dismissButton.Click += (_, _) => { HideSessionNotice(); dismiss(); };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(glyph, Dock.Left);
            DockPanel.SetDock(dismissButton, Dock.Right);
            DockPanel.SetDock(restoreButton, Dock.Right);
            row.Children.Add(glyph);
            row.Children.Add(dismissButton);
            row.Children.Add(restoreButton);
            row.Children.Add(message);

            var bar = new Border
            {
                Child = NoticeCard(row),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 640,
                Margin = new Thickness(0, 12, 0, 0)
            };
            // A notice floats over the page, so it is the menus' own card: FlyoutCard's fill, edge, corners and shadow.
            bar.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutCard");
            // 7 plus the 1px edge at the sides, so the glyph and the buttons sit 8px in. Above and below
            // it is 3 plus the edge: 4px, like the slimmer tool options bar and tabs, where 8 made the
            // bar stand out as taller than everything around it.
            bar.Padding = new Thickness(7, 3, 7, 3);
            void Relabel()
            {
                message.Text = string.Format(host.Loc("Str_RestoreSession_Prompt"), fileCount);
                AutomationProperties.SetName(bar, message.Text);
            }
            Relabel();
            // Subscribed only while the bar is in the tree, so a closed window is never held by
            // the static event.
            bar.Loaded += (_, _) => { Services.LocaleManager.LocaleChanged -= Relabel; Services.LocaleManager.LocaleChanged += Relabel; Relabel(); };
            bar.Unloaded += (_, _) => Services.LocaleManager.LocaleChanged -= Relabel;
            Panel.SetZIndex(bar, 90);
            _sessionNotice = bar;
            previewArea.Children.Add(bar);

            Dispatcher.BeginInvoke(() =>
                UIElementAutomationPeer.CreatePeerForElement(message)
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void HideSessionNotice()
        {
            if (_sessionNotice is null) return;
            (_sessionNotice.Parent as Panel)?.Children.Remove(_sessionNotice);
            _sessionNotice = null;
        }
    }
}
