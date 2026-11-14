using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerPDF.Controls;

namespace KillerPDF;

// A document tool launcher (Production layouts, Form data, Preflight and the rest): one icon tile per command, the
// KillerUI card quick-button look, laid out in a grid instead of a column of identical buttons.
internal sealed class DocumentCommandDialog : Window
{
    internal Action? Command { get; private set; }

    internal DocumentCommandDialog(Window owner, string title, params (string Label, string Glyph, Action Run)[] commands)
    {
        Title = "KillerPDF - " + title;
        int columns = CommandLauncher.Columns(commands.Length);
        Width = Math.Max(380, columns * 118 + 44);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80);
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var choices = new UniformGrid { Columns = CommandLauncher.Columns(commands.Length) };
        // UniformGrid gives every cell the tallest child's height, so Close in a tile cell centers on the tiles.
        KeyboardNavigation.SetDirectionalNavigation(choices, KeyboardNavigationMode.Cycle);
        foreach (var (label, glyph, run) in commands)
        {
            // The caption is on the tile and is its accessible name (CardQuickButton binds Tag), so no tooltip repeats it.
            var tile = new Button { Content = glyph, Tag = label, Margin = new Thickness(3), MinHeight = 76 };
            if (TryFindResource("CardQuickButton") is Style style) tile.Style = style;
            tile.Click += (_, _) => { Command = run; DialogResult = true; };
            choices.Children.Add(tile);
        }
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: false);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        int open = CommandLauncher.OpenCells(commands.Length, columns);
        if (open > 0)
        {
            // The last row has room, so Close sits at its right end, centered on the tiles, instead of on a row
            // of its own under them.
            for (int cell = 1; cell < open; cell++) choices.Children.Add(new FrameworkElement());
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Center;
            close.Margin = new Thickness(3);
            choices.Children.Add(close);
        }
        else
        {
            var footer = UiKit.ButtonRow(close);
            footer.Margin = new Thickness(0, 12, 0, 0);
            DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        }
        body.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => (choices.Children.OfType<Control>().FirstOrDefault() ?? close).Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
