using System.Collections.Generic;
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
        Width = 820;
        Height = 650;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var buttonsPanel = new StackPanel();
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        buttonsPanel.Children.Add(new TextBlock
        {
            Text = L("Str_AutoBookmarks_ReviewHelp"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 4)
        });
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var add = UiKit.Make(L("Str_AutoBookmarks_AddSelected"), accent: true);
        add.IsDefault = true;
        add.Click += (_, _) => Commit();
        buttonsPanel.Children.Add(UiKit.ButtonRow(cancel, add));
        body.Children.Add(buttonsPanel);

        var list = new StackPanel();
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
            title.Margin = new Thickness(0, 0, 8, 0);
            Add(grid, include, 0);
            Add(grid, title, 1);
            Add(grid, Value((proposal.PageIndex + 1).ToString()), 2);
            Add(grid, Value(proposal.PointSize.ToString("0.##")), 3);
            Add(grid, Value((proposal.Level + 1).ToString()), 4);
            list.Children.Add(grid);
            _rows.Add(new ProposalRow(proposal, include, title));
        }

        var header = new Grid();
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
        center.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        body.Children.Add(center);

        Content = DialogChrome.Frame(this, owner, Title,
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
        if (_rows.All(row => row.Include.IsChecked != true))
        {
            KillerDialog.Show(this, L("Str_AutoBookmarks_NoneSelected"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Proposals = [.. _rows.Select(row => row.Proposal with
        {
            Title = row.Title.Text.Trim(),
            Decision = row.Include.IsChecked == true
                ? PdfBookmarkProposalDecision.Accepted
                : PdfBookmarkProposalDecision.Rejected
        })];
        DialogResult = true;
        Close();
    }

    private static TextBlock Value(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private sealed record ProposalRow(
        PdfBookmarkProposal Proposal, CheckBox Include, TextBox Title);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
