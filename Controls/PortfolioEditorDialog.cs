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
        Width = 470;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(UiKit.GroupLabel(L("Str_Portfolio_View")));
        Choice[] views =
        [
            new(L("Str_Portfolio_Details"), PdfCollectionView.Details),
            new(L("Str_Portfolio_Tile"), PdfCollectionView.Tile),
            new(L("Str_Portfolio_Hidden"), PdfCollectionView.Hidden)
        ];
        _view = Combo(views);
        _view.SelectedItem = views.FirstOrDefault(choice => choice.Value == collection?.View)
            ?? views[0];
        body.Children.Add(_view);

        body.Children.Add(UiKit.GroupLabel(L("Str_Portfolio_InitialDocument")));
        string none = L("Str_Portfolio_None");
        string[] documents = [none, .. attachmentNames];
        _initialDocument = Combo(documents);
        _initialDocument.SelectedItem = collection?.InitialDocument is string selected
            && documents.Contains(selected, StringComparer.Ordinal)
                ? selected : none;
        body.Children.Add(_initialDocument);

        _removeMetadata = UiKit.CheckBox(L("Str_Portfolio_RemoveMetadata"));
        _removeMetadata.Margin = new Thickness(0, 4, 0, 4);
        _removeMetadata.IsEnabled = collection is not null;
        _removeMetadata.Checked += (_, _) => SetEditorsEnabled(false);
        _removeMetadata.Unchecked += (_, _) => SetEditorsEnabled(true);
        body.Children.Add(_removeMetadata);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit(none);
        var buttons = UiKit.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title,
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
        Margin = new Thickness(0, 0, 0, 10),
        ItemsSource = items,
        Style = Application.Current?.TryFindResource("DarkComboBox") as Style
    };

    private sealed record Choice(string Label, PdfCollectionView Value)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
