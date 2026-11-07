using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using KillerFamily.Layout;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private const string PageNavigationSetting = "ShowPageNavigationButtons";
    private MenuItem? _pageNavigationMenuItem;

    private static bool PageNavigationButtonsEnabled => App.GetSetting(PageNavigationSetting) == "1";

    private void TogglePageNavigationButtons()
    {
        App.SetSetting(PageNavigationSetting, PageNavigationButtonsEnabled ? "0" : "1");
        foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
            window.SyncPageNavigationButtons();
    }

    private void SyncPageNavigationButtons()
    {
        if (PreviousPageBtn is null || NextPageBtn is null || PageNavigationHost is null) return;
        bool visible = PageNavigationButtonsEnabled && ActiveViewer is not null && ActiveViewer.ReadPageCountExt > 1;
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PageNavigationHost.Visibility = visibility;
        PreviousPageBtn.Visibility = NextPageBtn.Visibility = visibility;
        PreviousPageBtn.IsEnabled = visible && ActiveViewer!.CanNavigatePageStep(-1);
        NextPageBtn.IsEnabled = visible && ActiveViewer!.CanNavigatePageStep(1);
        if (_pageNavigationMenuItem is not null)
        {
            _pageNavigationMenuItem.IsChecked = PageNavigationButtonsEnabled;
            _pageNavigationMenuItem.InputGestureText = GestureFor("AltShift:N");
        }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => NavigatePageStep(-1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => NavigatePageStep(1);

    private void SetPageNavigationTip(Button button, string labelKey, string cap)
    {
        var label = new TextBlock();
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        var shortcut = new TextBlock { Text = GestureFor(cap) };
        shortcut.SetBinding(VisibilityProperty, new Binding(nameof(ShortcutHints.MenuVisibility)) { Source = ShortcutHints.Current });
        var content = new StackPanel();
        content.Children.Add(label); content.Children.Add(shortcut);
        button.ToolTip = content;
        button.SetResourceReference(AutomationProperties.NameProperty, labelKey);
    }
}
