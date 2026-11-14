using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class NavigationRepairDialog : Window
{
    private readonly CheckBox _unsafeLinks;
    private readonly CheckBox _unresolvedLinks;
    private readonly Button _repair;

    internal bool RemoveUnsafeLinks => _unsafeLinks.IsChecked == true;
    internal bool RemoveUnresolvedLinks => _unresolvedLinks.IsChecked == true;

    internal NavigationRepairDialog(
        Window owner, string report, bool hasUnsafeLinks, bool hasUnresolvedLinks)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_NavigationAudit");
        // As tall as the report needs, up to most of the screen, where it starts scrolling.
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 520;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };

        var reportBox = UiKit.Field();
        reportBox.Text = report;
        reportBox.IsReadOnly = true;
        reportBox.AcceptsReturn = true;
        reportBox.AcceptsTab = true;
        reportBox.TextWrapping = TextWrapping.NoWrap;
        reportBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        reportBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        reportBox.FontFamily = UiKit.MonoFont;
        reportBox.FontSize = 11;
        System.Windows.Automation.AutomationProperties.SetName(reportBox, L("Str_DocumentTools_NavigationAudit"));

        // The repair choices sit on the footer's left, stacked, with Cancel and Repair selected on the right.
        var options = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        _unsafeLinks = UiKit.CheckBox(L("Str_Navigation_RemoveUnsafe"));
        _unsafeLinks.IsEnabled = hasUnsafeLinks;
        _unsafeLinks.IsChecked = hasUnsafeLinks;
        _unsafeLinks.Checked += (_, _) => UpdateRepairButton();
        _unsafeLinks.Unchecked += (_, _) => UpdateRepairButton();
        options.Children.Add(_unsafeLinks);
        _unresolvedLinks = UiKit.CheckBox(L("Str_Navigation_RemoveUnresolved"));
        _unresolvedLinks.Margin = new Thickness(0, 6, 0, 0);
        _unresolvedLinks.IsEnabled = hasUnresolvedLinks;
        _unresolvedLinks.IsChecked = hasUnresolvedLinks;
        _unresolvedLinks.Checked += (_, _) => UpdateRepairButton();
        _unresolvedLinks.Unchecked += (_, _) => UpdateRepairButton();
        options.Children.Add(_unresolvedLinks);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _repair = UiKit.Make(L("Str_Navigation_Repair"), accent: true);
        _repair.IsDefault = true;
        _repair.Click += (_, _) => { DialogResult = true; Close(); };
        DockPanel footer = DialogParts.Footer([cancel, _repair], options);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(DialogParts.Well(reportBox));

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => reportBox.Focus();
        UpdateRepairButton();
    }

    private void UpdateRepairButton() =>
        _repair.IsEnabled = RemoveUnsafeLinks || RemoveUnresolvedLinks;

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
