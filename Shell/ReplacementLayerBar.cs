using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private FrameworkElement? BuildReplacementLayerControls()
    {
        if (_selectedAnnotation is not { } selected
            || !_annotations.TryGetValue(selected.PageIndex, out var page)
            || ReplacementLayers.FindPair(selected, page) is not { } pair) return null;

        var grid = new Grid { Name = "ReplacementLayerControls", Margin = new Thickness(8, 0, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int column = 0; column < 3; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        for (int row = 0; row < 3; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });

        AddRow(ReplacementLayer.Group, "Str_Replacement_Group", 0, pair.Text.PairVisible,
            !pair.Text.PairMovementLocked);
        AddRow(ReplacementLayer.Cover, "Str_Annot_Cover", 1, pair.Cover.IsVisible,
            !pair.Cover.IsMovementLocked);
        AddRow(ReplacementLayer.Text, "Str_Annot_TextBox", 2, pair.Text.IsVisible,
            !pair.Text.IsMovementLocked);
        return grid;

        void AddRow(ReplacementLayer layer, string labelKey, int row, bool visible, bool movable)
        {
            string prefix = "ReplacementLayer" + layer;
            string rowLabel = Loc(labelKey);
            Button label = UiKit.Make(rowLabel, false);
            label.Name = prefix + "Select";
            label.MinHeight = 24;
            label.Padding = new Thickness(5, 0, 5, 0);
            label.Margin = new Thickness(0, 1, 3, 1);
            label.FontSize = 11;
            label.ToolTip = Loc(layer == ReplacementLayer.Group ? "Str_Replacement_SelectGroup" : labelKey);
            label.Click += (_, _) => SelectLayer(layer);
            Place(label, row, 0);
            Toggle(prefix + "Visible", "Str_Panel_LayerToggle", visible,
                "M1,8 C4,2 12,2 15,8 C12,14 4,14 1,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8",
                value => ReplacementLayers.SetVisibility(pair, layer, value), rowLabel, row, 1);
            Toggle(prefix + "Movable", "Str_Replacement_LayerMove", movable,
                "M5,8 L5,3 C5,1 7,1 7,3 L7,7 L7,2 C7,0 9,0 9,2 L9,7 L9,3 C9,1 11,1 11,3 L11,8 L11,5 C11,3 13,3 13,5 L13,10 C13,14 10,16 7,16 L5,16 L1,10 C0,8 2,7 3,8 L5,10 Z",
                value => ReplacementLayers.SetMovementAllowed(pair, layer, value), rowLabel, row, 2);
            if (ReplacementLayers.ShowsLinkToggle(layer))
                Toggle(prefix + "Linked", "Str_Replacement_Link", pair.Text.PairLinked && pair.Cover.PairLinked,
                    "M6,5 L4,5 A3,3 0 0 0 4,11 L7,11 M10,5 L12,5 A3,3 0 0 1 12,11 L9,11 M5,8 L11,8",
                    value => ReplacementLayers.SetLinked(pair, value), rowLabel, row, 3);
        }

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        void Toggle(string name, string tooltip, bool state, string geometry,
            Action<bool> change, string rowLabel, int row, int column)
        {
            var button = new ToggleButton
            {
                Name = name, Width = 24, Height = 24, Padding = new Thickness(3),
                Margin = new Thickness(1), IsChecked = state, ToolTip = Loc(tooltip)
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarToggleButton");
            // Each row has the same three buttons, so the name says which row it belongs to: "Text cover: ...".
            AutomationProperties.SetName(button, string.Format(Loc("Str_Bar_SwatchName"), rowLabel, Loc(tooltip)));
            AutomationProperties.SetAutomationId(button, name);
            var icon = new ShapePath
            {
                Data = Geometry.Parse(geometry), StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform
            };
            icon.SetBinding(ShapePath.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
            button.Content = icon;
            RoutedEventHandler changed = (_, _) =>
            {
                if (!StillSelected()) return;
                bool groupSelected = _selectedSet.Contains(pair.Cover) && ReferenceEquals(_selectedAnnotation, pair.Text)
                    || _selectedSet.Contains(pair.Text) && ReferenceEquals(_selectedAnnotation, pair.Cover);
                var selection = groupSelected ? ReplacementLayer.Group
                    : _selectedAnnotation is CoverAnnotation ? ReplacementLayer.Cover : ReplacementLayer.Text;
                ActiveViewer.PushPageSnapshotUndoExt(selected.PageIndex);
                change(button.IsChecked == true);
                MarkDirty();
                SelectLayer(selection);
                // A mouse click leaves the keyboard in this popup, which keeps the arrow keys from the selection, so
                // they scrolled the page instead of moving it. Hand the keyboard back to the page (#467).
                if (InputManager.Current.MostRecentInputDevice is MouseDevice)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                        new Action(ActiveViewer.FocusReadingSurfaceExt));
            };
            button.Checked += changed;
            button.Unchecked += changed;
            Place(button, row, column);
        }

        bool StillSelected() => _selectedAnnotation is { } current
            && _annotations.TryGetValue(current.PageIndex, out var currentPage)
            && ReplacementLayers.FindPair(current, currentPage) is { } currentPair
            && ReferenceEquals(currentPair.Text, pair.Text) && ReferenceEquals(currentPair.Cover, pair.Cover);

        void SelectLayer(ReplacementLayer layer)
        {
            if (!StillSelected()) return;
            PageAnnotation lead = layer == ReplacementLayer.Cover ? pair.Cover : pair.Text;
            if (layer == ReplacementLayer.Group)
            {
                ActiveViewer.SelectReplacementGroupExt(lead);
                return;
            }
            ActiveViewer.ClearSelection();
            ActiveViewer.RenderAllAnnotations(lead.PageIndex);
            ActiveViewer.SelectAnnotationExt(lead, Controls.PdfViewer.AnnotBoundsExt(lead));
        }
    }
}
