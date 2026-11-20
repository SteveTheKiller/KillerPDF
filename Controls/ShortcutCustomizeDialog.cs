using KillerPDF.Shell;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF;

// Customize Shortcuts (#190). Each movable command is listed under its overlay section with the
// key it answers to now. Clicking that key waits for the next key press and moves the command
// there; a key another command already uses is refused and named. Nothing changes until Save.
internal sealed class ShortcutCustomizeDialog : Window
{
    private readonly ShortcutRemap _remap;
    private readonly Dictionary<string, Button> _keyButtons = new(StringComparer.Ordinal);
    private readonly TextBlock _status;
    private ShortcutCommand? _capturing;

    internal bool Saved { get; private set; }
    internal ShortcutRemap Result => _remap;

    internal ShortcutCustomizeDialog(Window owner, ShortcutRemap current)
    {
        _remap = ShortcutRemap.Parse(current.Serialize());
        Title = "KillerPDF - " + L("Str_KS_CustomizeTitle");
        Width = 560;
        Height = 640;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16), LastChildFill = true };

        var hint = new TextBlock
        {
            Text = L("Str_KS_CustomizeHint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 16
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var resetAll = UiKit.Make(L("Str_KS_ResetAll"), accent: false);
        resetAll.Click += (_, _) =>
        {
            _remap.ResetAll();
            _status.Text = "";
            RefreshKeys();
        };
        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.Click += (_, _) => { Saved = true; Close(); };
        // One footer row: Reset all on the left, Cancel and Save on the right.
        DockPanel buttons = DialogParts.Footer([cancel, save], resetAll);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);

        var list = new StackPanel();
        Grid.SetIsSharedSizeScope(list, true);
        foreach (var group in ShortcutTable.KsGroups)
        {
            ShortcutCommand[] commands = [.. ShortcutRemap.Commands.Where(command => command.Cat == group.Cat)];
            if (commands.Length == 0) continue;
            var header = new TextBlock
            {
                Text = L(group.TitleKey),
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 4)
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + group.Cat);
            list.Children.Add(header);
            foreach (ShortcutCommand command in commands) list.Children.Add(Row(command));
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 6, 6, 6)
        };
        body.Children.Add(DialogParts.Well(scroll));

        PreviewKeyDown += CaptureKey;
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        RefreshKeys();
    }

    private UIElement Row(ShortcutCommand command)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "KsKey" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = L(command.LabelKey),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 10, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        row.Children.Add(label);

        var key = UiKit.Make("", accent: false);
        key.MinWidth = 140;
        key.FontFamily = UiKit.MonoFont;
        key.Click += (_, _) => BeginCapture(command);
        Grid.SetColumn(key, 1);
        row.Children.Add(key);
        _keyButtons[ShortcutRemap.Name(command.Original)] = key;

        // Each row's reset is an icon beside the key it puts back, explained in its tooltip.
        Button reset = DialogParts.IconAction("Str_KS_Reset", 0xE7A7, (_, _) =>
        {
            _remap.Reset(command.Original);
            RefreshKeys();
        });
        reset.ToolTip = L("Str_KS_ResetTT");
        System.Windows.Automation.AutomationProperties.SetName(reset, L("Str_KS_Reset"));
        reset.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);
        return row;
    }

    private void BeginCapture(ShortcutCommand command)
    {
        _capturing = command;
        RefreshKeys();
        _keyButtons[ShortcutRemap.Name(command.Original)].Content = L("Str_KS_PressKey");
        _status.Text = "";
    }

    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (_capturing is not ShortcutCommand command) return;
        Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;   // wait for the key itself
        e.Handled = true;
        _capturing = null;
        if (pressed == Key.Escape)
        {
            RefreshKeys();
            return;
        }
        if (MainWindow.CapIdForKey(pressed) is not string id
            || MainWindow.LayerFor(Keyboard.Modifiers) is not KbLayer layer)
        {
            _status.Text = L("Str_KS_KeyNotAllowed");
            RefreshKeys();
            return;
        }
        string? taken = _remap.Assign(command.Original, new KsCap(layer, id, ""));
        _status.Text = taken is null ? "" : string.Format(L("Str_KS_KeyTaken"), L(taken));
        RefreshKeys();
    }

    private void RefreshKeys()
    {
        foreach (ShortcutCommand command in ShortcutRemap.Commands)
        {
            if (!_keyButtons.TryGetValue(ShortcutRemap.Name(command.Original), out Button? button)) continue;
            button.Content = ChordLabel(_remap.Effective(command.Original));
            button.FontWeight = _remap.IsCustomized(command.Original) ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>A key as the user reads it, in their language: "Ctrl+Shift+F5".</summary>
    internal static string ChordLabel(KsCap cap)
    {
        var parts = new List<string>();
        if (cap.Layer is KbLayer.Ctrl or KbLayer.CtrlShift or KbLayer.CtrlAltShift) parts.Add(L("Str_Key_Ctrl"));
        if (cap.Layer is KbLayer.Alt or KbLayer.AltShift or KbLayer.CtrlAltShift) parts.Add(L("Str_Key_Alt"));
        if (cap.Layer is KbLayer.Shift or KbLayer.CtrlShift or KbLayer.AltShift or KbLayer.CtrlAltShift) parts.Add(L("Str_Key_Shift"));
        parts.Add(cap.Id switch
        {
            "Del" => L("Str_Key_Delete"),
            "Ins" => "Ins",
            var digit when digit.Length == 2 && digit[0] == 'D' => digit[1..],
            var other => other
        });
        return string.Join("+", parts);
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
