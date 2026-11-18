using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF;

/// <summary>
/// The pieces KillerPDF's dialogs are built from, so every window reads the same way (the Macros window is the
/// reference). List and editor actions are icons beside what they act on, named in their tooltips. Lists and long
/// text sit in a well that lets the window's texture show through. Dialog confirm buttons (Cancel, Save, Run,
/// Close) stay worded buttons from <see cref="UiKit.Make(object, bool)"/>, on one footer row with any other footer
/// controls on its left. No shadows on buttons.
/// </summary>
internal static class DialogParts
{
    /// <summary>
    /// An icon action: bare until hover (the family's BareButton), named by its tooltip, which the style also gives
    /// screen readers. The automation id is the label key, so tests find it in any language.
    /// </summary>
    internal static Button IconAction(string labelKey, int glyph, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = Glyph(glyph),
            ToolTip = L(labelKey),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "BareButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, labelKey);
        button.Click += click;
        return button;
    }

    /// <summary>An icon with its words beside it, for an action that needs its name in view (it opens another
    /// window, or it is the one thing the dialog is for).</summary>
    internal static Button LabeledAction(string labelKey, int glyph, RoutedEventHandler click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        TextBlock icon = Glyph(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = L(labelKey), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            ToolTip = L(labelKey),
            Padding = new Thickness(8, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "BareButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, labelKey);
        button.Click += click;
        return button;
    }

    /// <summary>A Segoe MDL2 glyph sized for an icon action.</summary>
    internal static TextBlock Glyph(int codePoint, double size = 14) => new()
    {
        Text = char.ConvertFromUtf32(codePoint),
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A heading on the left and action icons on the right, on one line above a list or text.</summary>
    internal static DockPanel Header(string title, params Button[] icons)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (Button icon in icons) strip.Children.Add(icon);
        DockPanel.SetDock(strip, Dock.Right);
        row.Children.Add(strip);
        TextBlock label = UiKit.GroupLabel(title);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        label.Margin = new Thickness(0, 0, 8, 4);
        row.Children.Add(label);
        return row;
    }

    /// <summary>
    /// Sets <paramref name="content"/> in a well: the pane fill at half strength with the pane's edge, so the area
    /// reads on every theme (Light's card edge alone is invisible) while the window's texture still shows through.
    /// A list or text box inside is made see-through so the well is what shows. <paramref name="overlay"/>, such as
    /// an empty-list hint, sits on top without taking clicks.
    /// </summary>
    internal static Grid Well(FrameworkElement content, UIElement? overlay = null)
    {
        // WPF's own ListBox template paints a solid system background while the list is disabled, which showed as a
        // white box in Protect Document until Certificate recipients was chosen. A list in a well dims instead.
        if (content is ListBox list) list.Template = SeeThroughList;
        if (content is Control control)
        {
            control.Background = Brushes.Transparent;
            control.BorderThickness = new Thickness(0);
            control.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            if (control.Padding == default) control.Padding = new Thickness(3);
        }
        var well = new Grid();
        var fill = new Border { Opacity = 0.5, IsHitTestVisible = false };
        fill.SetResourceReference(Border.BackgroundProperty, "WellFillBrush");
        fill.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
        var edge = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        edge.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
        well.Children.Add(fill);
        well.Children.Add(content);
        if (overlay is not null)
        {
            overlay.IsHitTestVisible = false;
            well.Children.Add(overlay);
        }
        well.Children.Add(edge);
        return well;
    }

    private static readonly ControlTemplate SeeThroughList = BuildSeeThroughList();

    private static ControlTemplate BuildSeeThroughList()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.FocusableProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        border.AppendChild(scroll);
        var template = new ControlTemplate(typeof(ListBox)) { VisualTree = border };
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "Bd"));
        template.Triggers.Add(disabled);
        template.Seal();
        return template;
    }

    /// <summary>A centered, muted line for an empty well ("No saved macros yet...").</summary>
    internal static TextBlock EmptyHint(string labelKey)
    {
        var hint = new TextBlock
        {
            Text = L(labelKey),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return hint;
    }

    /// <summary>
    /// The footer row: <paramref name="left"/> controls (choices, a secondary action) on the left and the worded
    /// confirm buttons on the right, on one line so the dialog wastes no height under its content.
    /// </summary>
    internal static DockPanel Footer(Button[] confirm, params UIElement[] left)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        StackPanel buttons = UiKit.ButtonRow(confirm);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        buttons.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        var leftSide = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (UIElement element in left)
        {
            if (element is FrameworkElement framework)
            {
                framework.VerticalAlignment = VerticalAlignment.Center;
                if (framework.Margin == default) framework.Margin = new Thickness(0, 0, 18, 0);
            }
            leftSide.Children.Add(element);
        }
        footer.Children.Add(leftSide);
        return footer;
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
