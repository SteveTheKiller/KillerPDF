using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class PortfolioEditorDialog : Window
{
    private readonly ComboBox _view;
    private readonly ComboBox _initialDocument;
    private readonly CheckBox _removeMetadata;

    internal PdfCollectionView View { get; private set; }
    internal string? InitialDocument { get; private set; }
    internal bool RemoveMetadata => _removeMetadata.IsChecked == true;

    internal PortfolioEditorDialog(
        Window owner, PdfCollectionInfo? collection, IReadOnlyList<string> attachmentNames)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_EditPortfolio");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        Choice[] views =
        [
            new(L("Str_Portfolio_Details"), PdfCollectionView.Details),
            new(L("Str_Portfolio_Tile"), PdfCollectionView.Tile),
            new(L("Str_Portfolio_Hidden"), PdfCollectionView.Hidden)
        ];
        _view = Combo(views);
        _view.SelectedItem = views.FirstOrDefault(choice => choice.Value == collection?.View)
            ?? views[0];

        string none = L("Str_Portfolio_None");
        string[] documents = [none, .. attachmentNames];
        _initialDocument = Combo(documents);
        _initialDocument.SelectedItem = collection?.InitialDocument is string selected
            && documents.Contains(selected, StringComparer.Ordinal)
                ? selected : none;

        // The view and the initial document side by side on one row.
        var choices = new Grid();
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        var viewColumn = new StackPanel();
        viewColumn.Children.Add(UiKit.GroupLabel(L("Str_Portfolio_View")));
        viewColumn.Children.Add(_view);
        choices.Children.Add(viewColumn);
        var documentColumn = new StackPanel();
        documentColumn.Children.Add(UiKit.GroupLabel(L("Str_Portfolio_InitialDocument")));
        documentColumn.Children.Add(_initialDocument);
        Grid.SetColumn(documentColumn, 2);
        choices.Children.Add(documentColumn);
        body.Children.Add(choices);

        _removeMetadata = UiKit.CheckBox(L("Str_Portfolio_RemoveMetadata"));
        _removeMetadata.IsEnabled = collection is not null;
        _removeMetadata.Checked += (_, _) => SetEditorsEnabled(false);
        _removeMetadata.Unchecked += (_, _) => SetEditorsEnabled(true);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit(none);
        // One footer row: the metadata choice on the left, Cancel and Save on the right.
        body.Children.Add(DialogParts.Footer([cancel, save], _removeMetadata));

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _view.Focus();
    }

    private void Commit(string none)
    {
        View = ((Choice)_view.SelectedItem).Value;
        InitialDocument = string.Equals(
            _initialDocument.SelectedItem as string, none, StringComparison.Ordinal)
            ? null : _initialDocument.SelectedItem as string;
        DialogResult = true;
        Close();
    }

    private void SetEditorsEnabled(bool enabled)
    {
        _view.IsEnabled = enabled;
        _initialDocument.IsEnabled = enabled;
    }

    private static ComboBox Combo(System.Collections.IEnumerable items) => new()
    {
        Height = 28,
        Margin = new Thickness(0, 0, 0, 4),
        ItemsSource = items,
        Style = UiKit.DarkComboStyle
    };

    private sealed record Choice(string Label, PdfCollectionView Value)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
