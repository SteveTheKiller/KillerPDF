using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class DocumentValuesDialog : Window
{
    private readonly List<TextBox> _fields = [];
    internal string[] Values => _fields.Select(input => input.Text.Trim()).ToArray();

    internal DocumentValuesDialog(Window owner, string title, params (string Label, string Value)[] fields)
        : this(owner, title, L("Str_Btn_Save"), fields) { }

    internal DocumentValuesDialog(Window owner, string title, string confirmText, params (string Label, string Value)[] fields)
    {
        Title = "KillerPDF - " + title;
        Width = 540;
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80);
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var fieldsPanel = new StackPanel();
        foreach (var (label, value) in fields)
        {
            var caption = UiKit.GroupLabel(label);
            caption.TextWrapping = TextWrapping.Wrap;
            fieldsPanel.Children.Add(caption);
            var input = UiKit.Field();
            input.Text = value;
            input.Margin = new Thickness(0, 5, 0, 12);
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            _fields.Add(input);
            fieldsPanel.Children.Add(input);
        }
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(confirmText, accent: true);
        apply.IsDefault = true;
        apply.Click += (_, _) => { DialogResult = true; };
        DockPanel footer = DialogParts.Footer([cancel, apply]);
        footer.Margin = new Thickness(0, 2, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        body.Children.Add(new ScrollViewer { Content = fieldsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => _fields.FirstOrDefault()?.Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
