using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class BookmarkDetectionDialog : Window
{
    private readonly TextBox _minimumSize;
    private readonly TextBox _maximumLength;
    private readonly TextBox _maximumDepth;
    private readonly TextBox _titlePattern;

    internal PdfBookmarkDetectionOptions? Options { get; private set; }

    internal BookmarkDetectionDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_AutoBookmarks");
        Width = 470;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        _minimumSize = AddField(body, "Str_AutoBookmarks_MinimumSize", "14");
        _maximumLength = AddField(body, "Str_AutoBookmarks_MaximumLength", "160");
        _maximumDepth = AddField(body, "Str_AutoBookmarks_MaximumDepth", "6");
        _titlePattern = AddField(body, "Str_AutoBookmarks_TitlePattern", string.Empty);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var detect = UiKit.Make(L("Str_AutoBookmarks_Detect"), accent: true);
        detect.IsDefault = true;
        detect.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, detect);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _minimumSize.Focus();
    }

    private void Commit()
    {
        if (!double.TryParse(_minimumSize.Text, NumberStyles.Float,
                CultureInfo.CurrentCulture, out double minimumSize)
            || !double.IsFinite(minimumSize) || minimumSize <= 0
            || !int.TryParse(_maximumLength.Text, NumberStyles.Integer,
                CultureInfo.CurrentCulture, out int maximumLength) || maximumLength < 1
            || !int.TryParse(_maximumDepth.Text, NumberStyles.Integer,
                CultureInfo.CurrentCulture, out int maximumDepth)
            || maximumDepth is < 1 or > 256)
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_Invalid"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string pattern = _titlePattern.Text.Trim();
        Options = new PdfBookmarkDetectionOptions
        {
            MinimumPointSize = minimumSize,
            MaximumTitleLength = maximumLength,
            MaximumDepth = maximumDepth,
            TitlePattern = pattern.Length == 0 ? null : pattern
        };
        try
        {
            _ = new System.Text.RegularExpressions.Regex(
                Options.TitlePattern ?? string.Empty,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_InvalidPattern"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private static TextBox AddField(StackPanel body, string labelKey, string value)
    {
        body.Children.Add(UiKit.GroupLabel(L(labelKey)));
        TextBox field = UiKit.Field();
        field.Text = value;
        field.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(field);
        return field;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
