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
    private readonly CheckBox _useTags;
    private readonly CheckBox _styledHeadings;
    private readonly CheckBox _skipRunning;

    internal PdfBookmarkDetectionOptions? Options { get; private set; }

    internal BookmarkDetectionDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_AutoBookmarks");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        // The three limits share one row; each label sits on its field even when a translation wraps.
        var limits = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        foreach (int column in new[] { 0, 2, 4 })
        {
            if (column > 0) limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            limits.ColumnDefinitions.Add(new ColumnDefinition());
        }
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _minimumSize = AddField(limits, 0, "Str_AutoBookmarks_MinimumSize", "14");
        _maximumLength = AddField(limits, 2, "Str_AutoBookmarks_MaximumLength", "160");
        _maximumDepth = AddField(limits, 4, "Str_AutoBookmarks_MaximumDepth", "6");
        body.Children.Add(limits);
        _titlePattern = AddField(body, "Str_AutoBookmarks_TitlePattern", string.Empty);
        _useTags = AddOption(body, "Str_AutoBookmarks_UseTags");
        _styledHeadings = AddOption(body, "Str_AutoBookmarks_StyledHeadings");
        _skipRunning = AddOption(body, "Str_AutoBookmarks_SkipRunning");

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var detect = UiKit.Make(L("Str_AutoBookmarks_Detect"), accent: true);
        detect.IsDefault = true;
        detect.Click += (_, _) => Commit();
        body.Children.Add(DialogParts.Footer([cancel, detect]));

        Content = DialogFrame.Frame(this, owner, Title,
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
            TitlePattern = pattern.Length == 0 ? null : pattern,
            UseDocumentTags = _useTags.IsChecked == true,
            DetectStyledHeadings = _styledHeadings.IsChecked == true,
            ExcludeRunningText = _skipRunning.IsChecked == true
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

    private static TextBox AddField(Grid row, int column, string labelKey, string value)
    {
        TextBlock label = UiKit.GroupLabel(L(labelKey));
        label.TextWrapping = TextWrapping.Wrap;
        label.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(label, column);
        row.Children.Add(label);
        TextBox field = UiKit.Field();
        field.Text = value;
        System.Windows.Automation.AutomationProperties.SetName(field, label.Text);
        Grid.SetColumn(field, column);
        Grid.SetRow(field, 1);
        row.Children.Add(field);
        return field;
    }

    private static CheckBox AddOption(StackPanel body, string labelKey)
    {
        CheckBox option = UiKit.CheckBox(L(labelKey));
        option.IsChecked = true;
        option.Margin = new Thickness(0, 2, 0, 6);
        body.Children.Add(option);
        return option;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
