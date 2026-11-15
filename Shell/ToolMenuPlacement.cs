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

        internal static bool ToolMenuFloating => App.GetSetting(ToolMenuFloatingSetting) == "1";

        private void InitToolMenuPlacement()
        {
            foreach (PdfViewer pane in new[] { Viewer, ViewerB })
            {
                ToolOptionsDock dock = pane.ToolOptionsDockRef;
                dock.Floating = ToolMenuFloating;
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
                InputGestureText = GestureFor("Alt:B"),
            };
            row.Click += (_, _) => ToggleToolMenuFloating();
            return row;
        }

        internal void ToggleToolMenuFloating()
        {
            App.SetSetting(ToolMenuFloatingSetting, ToolMenuFloating ? "0" : "1");
            foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                foreach (PdfViewer pane in new[] { window.Viewer, window.ViewerB })
                    pane.ToolOptionsDockRef.Floating = ToolMenuFloating;
        }
    }
}
