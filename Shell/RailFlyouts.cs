using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KillerPDF
{
    /// <summary>
    /// The rail's flyout buttons (family order, locked 2026-07-30: app-specific toggles, then
    /// ? / language / theme, theme bottom-most) and their flyouts. The theme, language and view
    /// pickers all moved here OUT of the retired Settings panel - one implementation each, as
    /// family-standard flyouts: ContextMenus with FlyoutCard/FlyoutGrain chrome, opened against
    /// the content pane's bottom-left corner via FlyoutPlacement so they never cover the rail,
    /// the footer, or the desktop. Their radio/dot sync is SyncPickerState (SettingsPanel.cs).
    /// </summary>
    public partial class MainWindow
    {
        // The shortcuts ? is the strip's ORIGINAL button (ShortcutHelp_Click) - it just moved
        // into the family slot above language; no second implementation was added.

        private void RailLang_Click(object sender, RoutedEventArgs e) => ToggleRailFlyout(LangFlyout);

        private void RailTheme_Click(object sender, RoutedEventArgs e) => ToggleRailFlyout(ThemeFlyout);

        private void RailView_Click(object sender, RoutedEventArgs e) => ToggleRailFlyout(ViewFlyout);

        // Rolling the wheel over the view-mode rail button steps through the modes without
        // opening the flyout: up = next, down = previous (Steve, 2026-07-31 - down-as-next felt
        // reversed). F9 jogs forward from the keyboard.
        private void RailView_Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            CycleViewMode(forward: e.Delta > 0);
            e.Handled = true;
        }

        /// <summary>Steps to the neighboring view mode, wrapping at the ends. Cycle order is the
        /// enum order: Single -> Continuous -> TwoPage -> Grid. Syncs the flyout radios in case
        /// the flyout is open while the wheel or F9 drives the change.</summary>
        private void CycleViewMode(bool forward = true)
        {
            var modes = (ViewMode[])Enum.GetValues(typeof(ViewMode));
            // Step from the PENDING mode when a fade-wrapped switch is in flight: _viewMode only
            // updates after the ~90ms fade-out, so wheel notches faster than that would otherwise
            // recompute from the stale mode and retarget the same switch - several notches
            // collapsing into one step (2-4 clicks per mode, as first built).
            int idx = Array.IndexOf(modes, _pendingViewMode ?? _viewMode);
            int next = (idx + (forward ? 1 : -1) + modes.Length) % modes.Length;
            SetViewMode(modes[next]);
            SyncPickerState();
        }

        private void ToggleRailFlyout(ContextMenu menu)
        {
            if (menu.IsOpen) { menu.IsOpen = false; return; }

            // Radios and accent dots reflect live state before the card shows - the same single
            // sync the Settings panel runs on open.
            SyncPickerState();

            // The pane the document sits on bounds the window, the footer and the rail at once -
            // the one corner a flyout can hug without covering any of them. ALWAYS pane A's panel,
            // never through the PagePreviewPanel accessor: that resolves via ActiveViewer, so with
            // the split open and the RIGHT pane focused the flyout anchored to pane B and opened
            // mid-window instead of at the window's bottom-left content corner (Steve, 2026-08-01).
            // Pane A is the leftmost pane and never collapses, so its corner is the rail-adjacent one.
            if (Viewer.PreviewScroller.Parent is FrameworkElement pane)
                FlyoutPlacement.UsePane(pane);
            FlyoutPlacement.Attach(menu, this);

            menu.IsOpen = true;
            // 150ms ease-out, the family fade (the flyout template replaces the implicit
            // ContextMenu template, whose Loaded trigger normally drives this).
            menu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }
}
