using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Controls;

namespace KillerPDF.Shell
{
    // #485: the tool options row ("tool menu") docks above the page, which moves the page down when a
    // tool is chosen, or floats over the page's top edge so the page stays put. One setting for every
    // window, switched from the toolbar's right-click menu, a right-click on the row itself, or Alt+B.
    public partial class MainWindow
    {
        private const string ToolMenuFloatingSetting = "ToolMenuFloating";

        // Floating is the default, as it was before 1.9.3; docking is the choice that gets saved.
        internal static bool ToolMenuFloating => App.GetSetting(ToolMenuFloatingSetting) != "0";

        private void InitToolMenuPlacement()
        {
            Action themed = () =>
                Dispatcher.BeginInvoke(new Action(() => { ApplyFloatingState(Viewer.ToolOptionsDockRef); ApplyFloatingState(ViewerB.ToolOptionsDockRef); }));
            Services.ThemeManager.ThemeChanged += themed;
            Closed += (_, _) => Services.ThemeManager.ThemeChanged -= themed;
            foreach (PdfViewer pane in new[] { Viewer, ViewerB })
            {
                ToolOptionsDock dock = pane.ToolOptionsDockRef;
                ApplyFloatingState(dock);
                dock.FloatingStateChanged += (_, _) =>
                {
                    // Where it was slid and whether it is minimized carry to every pane and window.
                    App.SetSetting(ToolMenuMinimizedSetting, dock.Minimized ? "1" : "0");
                    if (dock.CenterFraction is double fraction)
                        App.SetSetting(ToolMenuCenterSetting, fraction.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                        foreach (PdfViewer other in new[] { window.Viewer, window.ViewerB })
                            if (!ReferenceEquals(other.ToolOptionsDockRef, dock)) ApplyFloatingState(other.ToolOptionsDockRef);
                };
                dock.MouseRightButtonUp += (_, e) =>
                {
                    if (dock.Bar is not DependencyObject bar || e.OriginalSource is not DependencyObject source
                        || !IsWithin(source, bar)) return;
                    var menu = new ContextMenu { PlacementTarget = dock, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
                    menu.Items.Add(ToolMenuPlacementRow());
                    menu.IsOpen = true;
                    e.Handled = true;
                };
            }
        }

        private const string ToolMenuMinimizedSetting = "ToolMenuMinimized";
        private const string ToolMenuCenterSetting = "ToolMenuCenter";

        private static void ApplyFloatingState(ToolOptionsDock dock)
        {
            // 98SE bars are square, as its floating bar was in 1.9.2; every other theme rounds the bottom.
            dock.CornerRadius = Services.ThemeManager.Current == Services.Theme.SE98 ? 0 : ToolOptionsDock.FloatingCornerRadius;
            dock.Floating = ToolMenuFloating;
            dock.Minimized = App.GetSetting(ToolMenuMinimizedSetting) == "1";
            dock.CenterFraction = double.TryParse(App.GetSetting(ToolMenuCenterSetting),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double fraction) ? fraction : null;
        }

        private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
        {
            for (DependencyObject? d = source; d is not null;
                 d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, ancestor)) return true;
            return false;
        }

        /// <summary>The row names the placement a click switches to.</summary>
        internal MenuItem ToolMenuPlacementRow()
        {
            var row = new MenuItem
            {
                Header = Loc(ToolMenuFloating ? "Str_ToolMenu_Docked" : "Str_ToolMenu_Floating"),
                Tag = "toolmenu",
                Icon = ToolMenuIcons.For(floating: !ToolMenuFloating),
                InputGestureText = GestureFor("Alt:B"),
            };
            row.Click += (_, _) => ToggleToolMenuFloating();
            return row;
        }

        internal void ToggleToolMenuFloating()
        {
            App.SetSetting(ToolMenuFloatingSetting, ToolMenuFloating ? "0" : "1");
            foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
            {
                foreach (PdfViewer pane in new[] { window.Viewer, window.ViewerB })
                    ApplyFloatingState(pane.ToolOptionsDockRef);
                window.SyncToolbarMenuChecks();   // the toolbar card's placement tile names the other placement
            }
        }
    }
}
