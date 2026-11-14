using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class DocumentListDialog : Window
{
    private readonly ListBox _list = new();
    internal int SelectedIndex { get => _list.SelectedIndex; set => _list.SelectedIndex = value; }
    internal int[] Order => _list.Items.Cast<ListBoxItem>().Select(item => (int)item.Tag).ToArray();

    internal DocumentListDialog(Window owner, string title, IEnumerable<string> labels, bool reorder = false)
    {
        Title = "KillerPDF - " + title;
        Width = 650; Height = 480; MinWidth = 400; MinHeight = 300;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        int index = 0;
        foreach (string label in labels)
            _list.Items.Add(new ListBoxItem { Content = label, Tag = index++ });
        _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1;
        System.Windows.Automation.AutomationProperties.SetName(_list, title);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_Tf_Apply"), accent: true);
        apply.IsDefault = true; apply.IsEnabled = index > 0;
        apply.Click += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = true; };
        DockPanel footer = DialogParts.Footer([cancel, apply]);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        // Reordering puts Move up and Move down as icons above the list they act on.
        if (reorder)
        {
            Button up = DialogParts.IconAction("Str_Ctx_BmMoveUp", 0xE74A, (_, _) => Move(-1));
            Button down = DialogParts.IconAction("Str_Ctx_BmMoveDown", 0xE74B, (_, _) => Move(1));
            DockPanel header = DialogParts.Header(title, up, down);
            DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        }
        body.Children.Add(DialogParts.Well(_list));
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => _list.Focus();
    }

    private void Move(int delta)
    {
        int from = _list.SelectedIndex, to = from + delta;
        if (from < 0 || to < 0 || to >= _list.Items.Count) return;
        object item = _list.Items[from];
        _list.Items.RemoveAt(from); _list.Items.Insert(to, item);
        _list.SelectedIndex = to;
        _list.ScrollIntoView(item);
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
