using System.Windows;
using System.Windows.Input;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private bool TryRunExpandedShortcut(Key key, ModifierKeys modifiers, bool repeat)
    {
        Action? run = null;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.G)
            run = OpenSidebarSigning;
        else if (modifiers == ModifierKeys.Alt && key is >= Key.D1 and <= Key.D6)
        {
            SidebarPanel panel = (SidebarPanel)(key - Key.D1);
            run = () => OpenSidebarPanel(panel);
        }
        else if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift))
            run = key switch
            {
                Key.P => () => EditParagraph(PageList.SelectedIndex),
                Key.T => () => ExportTable(PageList.SelectedIndex),
                Key.E => () => ExportImages_Click(this, new RoutedEventArgs()),
                Key.Z => () => ReduceFileSize_Click(this, new RoutedEventArgs()),
                Key.C => () => CompressToZip_Click(this, new RoutedEventArgs()),
                Key.F => () => SaveFlattened_Click(this, new RoutedEventArgs()),
                Key.B => () => Merge_Click(this, new RoutedEventArgs()),
                Key.X => () => Split_Click(this, new RoutedEventArgs()),
                Key.V => () => ComparePdf_Click(ComparePdfBtn, new RoutedEventArgs()),
                Key.W => () => OpenWatchedFolders(this),
                Key.N => TogglePageNavigationButtons,
                Key.A => AddBlankPageAtEnd,
                Key.R => ReversePageOrder,
                Key.O => FixPageOrientation,
                Key.S => StraightenScannedPages,
                Key.D => DivideSpreads,
                Key.L => () => RasterizeRegion(PageList.SelectedIndex),
                Key.D1 => () => SelectMeasurement(MeasurementKind.Distance),
                Key.D2 => () => SelectMeasurement(MeasurementKind.Perimeter),
                Key.D3 => () => SelectMeasurement(MeasurementKind.Area),
                Key.D4 => () => SelectMeasurement(MeasurementKind.Angle),
                Key.D5 => () => OpenMeasureScale(),
                _ => null,
            };
        if (run is null) return false;
        if (!repeat && (_doc is not null || (key is Key.W or Key.N) && modifiers == (ModifierKeys.Alt | ModifierKeys.Shift)))
            run();
        return true;
    }

    private void SelectMeasurement(MeasurementKind kind)
    {
        ActiveViewer.MeasureMode = kind;
        if (_currentTool == EditTool.Measure) ShowDrawSettings(EditTool.Measure);
        else SetTool(EditTool.Measure);
    }

    private void OpenSidebarSigning()
    {
        if (_doc is null) return;
        if (_sidebarCollapsed || _sidebarPanel != SidebarPanel.Signatures)
            OpenSidebarPanel(SidebarPanel.Signatures);
        OpenSignDialog();
    }
}
