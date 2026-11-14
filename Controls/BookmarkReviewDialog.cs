using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class BookmarkReviewDialog : Window
{
    private readonly List<ProposalRow> _rows = [];

    internal IReadOnlyList<PdfBookmarkProposal> Proposals { get; private set; } = [];

    internal BookmarkReviewDialog(
        Window owner, IReadOnlyList<PdfBookmarkProposal> proposals)
    {
        Title = "KillerPDF - " + L("Str_AutoBookmarks_Review");
        // As tall as the list needs, up to most of the screen, where the list starts scrolling.
        Width = 680;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var add = UiKit.Make(L("Str_AutoBookmarks_AddSelected"), accent: true);
        add.IsDefault = true;
        add.Click += (_, _) => Commit();
        DockPanel footer = DialogParts.Footer([cancel, add]);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        // What the review does, above the list it describes.
        var help = new TextBlock
        {
            Text = L("Str_AutoBookmarks_ReviewHelp"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(help, Dock.Top);
        body.Children.Add(help);

        var list = new StackPanel { Margin = new Thickness(4, 5, 4, 5) };
        foreach (PdfBookmarkProposal proposal in proposals)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            CheckBox include = UiKit.CheckBox(string.Empty);
            include.IsChecked = true;
            include.HorizontalAlignment = HorizontalAlignment.Center;
            TextBox title = UiKit.Field();
            title.Text = proposal.Title;
            TextBox level = UiKit.Field();
            level.Text = (proposal.Level + 1).ToString(CultureInfo.CurrentCulture);
            level.HorizontalContentAlignment = HorizontalAlignment.Center;
            level.Margin = new Thickness(8, 0, 8, 0);
            // The title steps in with its level so the hierarchy reads at a glance.
            void Indent() => title.Margin = new Thickness(
                TryLevel(level.Text, out int value) ? Math.Min(value - 1, 8) * 16 : 0, 0, 8, 0);
            level.TextChanged += (_, _) => Indent();
            Indent();
            Add(grid, include, 0);
            Add(grid, title, 1);
            Add(grid, Value((proposal.PageIndex + 1).ToString()), 2);
            Add(grid, Value(proposal.PointSize.ToString("0.##")), 3);
            Add(grid, level, 4);
            list.Children.Add(grid);
            _rows.Add(new ProposalRow(proposal, include, title, level));
        }

        var header = new Grid { Margin = new Thickness(7, 0, 7, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        Add(header, UiKit.GroupLabel(L("Str_AutoBookmarks_Include")), 0);
        Add(header, UiKit.GroupLabel(L("Str_AutoBookmarks_Title")), 1);
        Add(header, UiKit.GroupLabel(L("Str_AutoBookmarks_Page")), 2);
        Add(header, UiKit.GroupLabel(L("Str_AutoBookmarks_Size")), 3);
        Add(header, UiKit.GroupLabel(L("Str_AutoBookmarks_Level")), 4);

        var center = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        center.Children.Add(header);
        center.Children.Add(DialogParts.Well(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }));
        body.Children.Add(center);

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _rows[0].Title.Focus();
    }

    private void Commit()
    {
        if (_rows.Where(row => row.Include.IsChecked == true)
            .Any(row => string.IsNullOrWhiteSpace(row.Title.Text)))
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_InvalidTitle"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_rows.Where(row => row.Include.IsChecked == true)
            .Any(row => !TryLevel(row.Level.Text, out _)))
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_InvalidLevel"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_rows.All(row => row.Include.IsChecked != true))
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_NoneSelected"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Proposals = [.. _rows.Select(row => row.Proposal with
        {
            Title = row.Title.Text.Trim(),
            Level = TryLevel(row.Level.Text, out int level) ? level - 1 : row.Proposal.Level,
            Decision = row.Include.IsChecked == true
                ? PdfBookmarkProposalDecision.Accepted
                : PdfBookmarkProposalDecision.Rejected
        })];
        DialogResult = true;
        Close();
    }

    private static bool TryLevel(string text, out int level) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out level)
        && level is >= 1 and <= 256;

    private static TextBlock Value(string text)
    {
        var value = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return value;
    }

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private sealed record ProposalRow(
        PdfBookmarkProposal Proposal, CheckBox Include, TextBox Title, TextBox Level);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
