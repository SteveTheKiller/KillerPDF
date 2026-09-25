using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class TableOfContentsDialog : Window
{
    private readonly TextBox _title;
    private readonly TextBox _depth;

    internal string ContentsTitle { get; private set; } = string.Empty;
    internal int MaximumDepth { get; private set; } = 6;

    internal TableOfContentsDialog(Window owner, int entryCount)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_InsertToc");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(new TextBlock
        {
            Text = string.Format(L("Str_DocumentTools_TocEntries"), entryCount),
            Foreground = UiKit.Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(UiKit.GroupLabel(L("Str_DocumentTools_TocTitle")));
        _title = UiKit.Field();
        _title.Text = L("Str_DocumentTools_TocDefaultTitle");
        _title.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(_title);
        body.Children.Add(UiKit.GroupLabel(L("Str_DocumentTools_TocDepth")));
        _depth = UiKit.Field();
        _depth.Text = "6";
        _depth.Width = 64;
        _depth.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(_depth);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var insert = UiKit.Make(L("Str_DocumentTools_InsertToc"), accent: true);
        insert.IsDefault = true;
        insert.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, insert);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _title.Focus();
    }

    private void Commit()
    {
        string title = _title.Text.Trim();
        if (title.Length == 0
            || !int.TryParse(_depth.Text, NumberStyles.Integer, CultureInfo.CurrentCulture,
                out int depth)
            || depth is < 1 or > 6)
        {
            KillerDialog.Show(this, L("Str_DocumentTools_TocInvalid"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ContentsTitle = title;
        MaximumDepth = depth;
        DialogResult = true;
        Close();
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
