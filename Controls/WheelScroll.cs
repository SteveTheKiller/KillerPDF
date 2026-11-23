#nullable enable
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerFamily.Controls
{
    /// <summary>
    /// The family's mouse-wheel step. Stock WPF moves a pixel-scrolled view 48px a notch, under two rows of most
    /// lists, and every hand-rolled step in the apps had drifted lower still. One notch here moves 40px for each
    /// line of Windows' "lines to scroll" setting (120px at the default 3), "one screen at a time" moves a viewport
    /// less a line, and finer wheels and touchpads move in proportion to their delta.
    ///
    /// <see cref="Register"/> runs once at startup (App.OnStartup) and covers every ScrollViewer in the app,
    /// dialogs included. Views that scroll by item (a ListBox without pixel scrolling) keep WPF's three items a
    /// notch, a view that only scrolls sideways takes the wheel sideways, and Ctrl, Shift and Alt are left to the
    /// zoom and horizontal handlers the apps already have. A control that scrolls itself on the wheel's preview
    /// (a note editor, a terminal) is untouched, since the event reaches here already handled.
    /// </summary>
    public static class WheelScroll
    {
        /// <summary>Pixels a notch moves for each line of the Windows wheel setting.</summary>
        public const double PerLine = 40;

        private static bool _registered;

        private static readonly PropertyInfo? HandlesWheel =
            typeof(ScrollViewer).GetProperty("HandlesMouseWheelScrolling", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly PropertyInfo? ScrollInfo =
            typeof(ScrollViewer).GetProperty("ScrollInfo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        }

        /// <summary>Pixels one wheel movement scrolls. <paramref name="wheelLines"/> is
        /// SystemParameters.WheelScrollLines: -1 is one screen at a time and 0 turns the wheel off.</summary>
        public static double Step(int delta, int wheelLines, double viewport)
        {
            if (wheelLines == 0) return 0;
            double perNotch = wheelLines < 0 ? Math.Max(PerLine, viewport - PerLine) : PerLine * wheelLines;
            return delta / 120.0 * perNotch;
        }

        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || sender is not ScrollViewer sv) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) != 0) return;
            if (HandlesWheel?.GetValue(sv) is false || ScrollsByItem(sv)) return;

            bool vertical = sv.ScrollableHeight > 0;
            bool sideways = !vertical && sv.ScrollableWidth > 0 && sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled;
            if (!vertical && !sideways) return;   // nothing to move: WPF handles it as it always has

            double step = Step(e.Delta, SystemParameters.WheelScrollLines, vertical ? sv.ViewportHeight : sv.ViewportWidth);
            if (vertical) sv.ScrollToVerticalOffset(sv.VerticalOffset - step);
            else sv.ScrollToHorizontalOffset(sv.HorizontalOffset - step);
            e.Handled = true;
        }

        // A StackPanel, and a VirtualizingStackPanel without pixel scrolling, count their offset in items.
        private static bool ScrollsByItem(ScrollViewer sv)
        {
            if (!sv.CanContentScroll) return false;
            object? info = ScrollInfo?.GetValue(sv);
            if (info is StackPanel) return true;
            if (info is not VirtualizingStackPanel panel) return false;
            DependencyObject owner = ItemsControl.GetItemsOwner(panel) ?? (DependencyObject)panel;
            return VirtualizingPanel.GetScrollUnit(owner) != ScrollUnit.Pixel;
        }
    }
}
