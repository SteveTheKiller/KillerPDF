using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using KillerPDF.Services.Signing;

namespace KillerPDF.Controls;

internal sealed class CertificateCatalogDialog : Window
{
    private readonly CertificateCatalog _catalog;
    private readonly TextBox _search = UiKit.Field();
    private readonly ListBox _list = new();
    private readonly TextBox _details = UiKit.Field();
    private readonly TextBlock _empty;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly Button _import;
    private readonly Button _trust;
    private readonly Button _untrust;
    private readonly Button _remove;
    private readonly Button _refresh;
    private readonly Button _close;
    private IReadOnlyList<CertificateCatalogEntry> _entries = [];
    private bool _busy;
    private bool _mutating;
    private bool _closed;

    internal bool Changed { get; private set; }

    internal CertificateCatalogDialog(Window owner, CertificateCatalog? catalog = null)
    {
        _catalog = catalog ?? new CertificateCatalog();
        Title = "KillerPDF - " + L("Str_CertCatalog_Title");
        Width = 900;
        Height = 650;
        MinWidth = 650;
        MinHeight = 440;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner, resizable: true);
        AutomationProperties.SetAutomationId(this, "CertificateCatalogDialog");

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };

        // The hint, then the search on one row: its label on the left, the box filling the rest.
        var top = new StackPanel();
        var hint = Label(L("Str_CertCatalog_Hint"), muted: true);
        hint.Margin = new Thickness(0, 0, 0, 10);
        top.Children.Add(hint);
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var searchLabel = Label(L("Str_CertCatalog_Search"));
        searchLabel.TextWrapping = TextWrapping.NoWrap;
        searchLabel.VerticalAlignment = VerticalAlignment.Center;
        searchLabel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        AutomationProperties.SetName(_search, L("Str_CertCatalog_Search"));
        AutomationProperties.SetAutomationId(_search, "CertificateCatalogSearch");
        _search.TextChanged += (_, _) => ApplyFilter(SelectedKey);
        searchRow.Children.Add(_search);
        top.Children.Add(searchRow);
        DockPanel.SetDock(top, Dock.Top);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_list, L("Str_CertCatalog_Title"));
        AutomationProperties.SetAutomationId(_list, "CertificateCatalogList");
        _list.SelectionChanged += (_, _) => UpdateSelection();
        _empty = DialogParts.EmptyHint("Str_CertCatalog_Empty");

        // List actions are icons in the list's header, named in their tooltips; each keeps its automation id.
        _import = ListAction("Str_CertCatalog_Import", 0xE896, "CertificateCatalogImport");
        _import.Click += async (_, _) => await ImportAsync();
        _trust = ListAction("Str_CertCatalog_Trust", 0xEA18, "CertificateCatalogTrust");
        _trust.Click += async (_, _) => await SetTrustAsync(true);
        _untrust = ListAction("Str_CertCatalog_Untrust", 0xEA18, "CertificateCatalogUntrust");
        _untrust.Click += async (_, _) => await SetTrustAsync(false);
        _remove = ListAction("Str_Protect_Remove", 0xE74D, "CertificateCatalogRemove");
        _remove.Click += async (_, _) => await RemoveAsync();
        _refresh = ListAction("Str_CertCatalog_Refresh", 0xE72C, "CertificateCatalogRefresh");
        _refresh.Click += async (_, _) => await ReloadAsync();

        var listHost = new DockPanel();
        DockPanel listHeader = DialogParts.Header(L("Str_CertCatalog_Title"), _import, _trust, _untrust, _remove, _refresh);
        DockPanel.SetDock(listHeader, Dock.Top);
        listHost.Children.Add(listHeader);
        listHost.Children.Add(DialogParts.Well(_list, _empty));
        panes.Children.Add(listHost);

        var detailsHost = new DockPanel();
        // The details heading lines up with the list's heading beside it, which its icons make taller.
        var detailsHeader = DialogParts.Header(L("Str_Portfolio_Details"));
        detailsHeader.MinHeight = 0;
        detailsHeader.SetBinding(FrameworkElement.HeightProperty,
            new Binding(nameof(ActualHeight)) { Source = listHeader });
        DockPanel.SetDock(detailsHeader, Dock.Top);
        detailsHost.Children.Add(detailsHeader);
        _details.IsReadOnly = true;
        _details.AcceptsReturn = true;
        _details.AcceptsTab = false;
        _details.TextWrapping = TextWrapping.Wrap;
        _details.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetName(_details, L("Str_Portfolio_Details"));
        AutomationProperties.SetAutomationId(_details, "CertificateCatalogDetails");
        detailsHost.Children.Add(DialogParts.Well(_details));
        Grid.SetColumn(detailsHost, 2);
        panes.Children.Add(detailsHost);

        var feedback = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _progress = new ProgressBar { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        _progress.SetResourceReference(Control.ForegroundProperty, "PrimaryBrush");
        AutomationProperties.SetName(_progress, L("Str_CertCatalog_Refresh"));
        feedback.Children.Add(_progress);
        _message = Label(string.Empty, muted: true);
        _message.Margin = new Thickness(0, 6, 0, 0);
        _message.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        AutomationProperties.SetName(_close, L("Str_DocumentTools_Close"));
        AutomationProperties.SetAutomationId(_close, "CertificateCatalogClose");
        _close.IsCancel = true;
        _close.Click += (_, _) => RequestClose();

        // Progress and messages sit under the panes; Close alone on the footer row.
        DockPanel footer = DialogParts.Footer([_close]);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        DockPanel.SetDock(feedback, Dock.Bottom);
        body.Children.Add(feedback);
        body.Children.Add(top);
        body.Children.Add(panes);

        Content = DialogFrame.Frame(this, owner, Title, RequestClose, body);
        Closed += (_, _) => _closed = true;
        Loaded += async (_, _) =>
        {
            _search.Focus();
            await ReloadAsync();
        };
        UpdateSelection();
    }

    private readonly record struct EntryKey(string Fingerprint, CertificateCatalogSource Source);

    private CertificateCatalogEntry? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as CertificateCatalogEntry;
    private EntryKey? SelectedKey => Selected is { } entry ? new EntryKey(entry.Fingerprint, entry.Source) : null;

    private async Task ReloadAsync()
    {
        if (_busy || _closed) return;
        var selected = SelectedKey;
        SetBusy(true);
        ShowMessage(string.Empty);
        try
        {
            var entries = await Task.Run(() => _catalog.Read());
            if (_closed) return;
            _entries = entries;
            ApplyFilter(selected);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _entries = [];
                ApplyFilter(null);
                ShowError(ex, "Str_CertCatalog_LoadError");
            }
        }
        finally
        {
            if (!_closed) SetBusy(false);
        }
    }

    private void ApplyFilter(EntryKey? selected)
    {
        string search = _search.Text.Trim();
        string fingerprintSearch = string.Concat(search.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-'));
        _list.Items.Clear();
        foreach (var entry in _entries.Where(entry => search.Length == 0
                     || entry.Subject.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                     || entry.Issuer.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                     || (fingerprintSearch.Length > 0 && entry.Fingerprint.Contains(fingerprintSearch, StringComparison.OrdinalIgnoreCase))))
        {
            var name = Label(entry.Subject.Length == 0 ? entry.Fingerprint : entry.Subject);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = entry.Subject;
            name.ClearValue(TextBlock.ForegroundProperty);
            var detail = Label(SourceLabel(entry.Source) + "\n" + Status(entry), muted: true);
            detail.FontSize = 11;
            detail.ClearValue(TextBlock.ForegroundProperty);
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("MutedTextBrush")));
            var selectedDetail = new DataTrigger
            {
                Binding = new Binding(nameof(ListBoxItem.IsSelected))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
                },
                Value = true
            };
            // The selected row's own text color, which the dialog's row style picks to suit its fill.
            selectedDetail.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ListBoxItem.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
            }));
            style.Triggers.Add(selectedDetail);
            detail.Style = style;
            var row = new StackPanel();
            row.Children.Add(name);
            row.Children.Add(detail);
            var item = new ListBoxItem { Content = row, Tag = entry, Padding = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(item, name.Text + ", " + SourceLabel(entry.Source) + ", " + Status(entry));
            _list.Items.Add(item);
            if (selected == new EntryKey(entry.Fingerprint, entry.Source)) _list.SelectedItem = item;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        _empty.Visibility = _list.Items.Count == 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var entry = Selected;
        bool local = entry is { IsReadOnly: false };
        _trust.IsEnabled = !_busy && local && entry!.IsCertificateAuthority && !entry.IsTrustedRoot;
        _untrust.IsEnabled = !_busy && local && entry!.IsTrustedRoot;
        _remove.IsEnabled = !_busy && local;
        _trust.Visibility = entry?.IsTrustedRoot == true ? Visibility.Collapsed : Visibility.Visible;
        _untrust.Visibility = entry?.IsTrustedRoot == true ? Visibility.Visible : Visibility.Collapsed;
        _details.Text = entry is null ? string.Empty : Details(entry);
    }

    private static string Details(CertificateCatalogEntry entry)
    {
        var text = new StringBuilder();
        void Add(string key, string value) => text.AppendLine(L(key) + ":").AppendLine(value).AppendLine();
        Add("Str_CertCatalog_Subject", entry.Subject);
        Add("Str_CertCatalog_Issuer", entry.Issuer);
        Add("Str_CertCatalog_Type", L(entry.IsCertificateAuthority ? "Str_CertCatalog_CA" : "Str_CertCatalog_Leaf"));
        Add("Str_CertCatalog_ValidFrom", entry.NotBefore.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        Add("Str_CertCatalog_ValidUntil", entry.NotAfter.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        Add("Str_CertCatalog_Fingerprint", entry.Fingerprint);
        Add("Str_CertCatalog_Source", SourceLabel(entry.Source));
        Add("Str_CertCatalog_Status", Status(entry));
        if (entry.IsReadOnly) text.AppendLine(L("Str_CertCatalog_ReadOnly"));
        return text.ToString().TrimEnd();
    }

    private static string Status(CertificateCatalogEntry entry)
    {
        string status = L(entry.IsTrustedRoot ? "Str_CertCatalog_Trusted" : "Str_CertCatalog_Untrusted");
        DateTime now = DateTime.UtcNow;
        if (entry.NotAfter.ToUniversalTime() < now) status += ", " + L("Str_CertCatalog_Expired");
        else if (entry.NotBefore.ToUniversalTime() > now) status += ", " + L("Str_CertCatalog_NotYetValid");
        return status;
    }

    private static string SourceLabel(CertificateCatalogSource source) => L(source switch
    {
        CertificateCatalogSource.WindowsCurrentUserRoot => "Str_CertCatalog_WindowsUserRoots",
        CertificateCatalogSource.WindowsLocalMachineRoot => "Str_CertCatalog_WindowsMachineRoots",
        CertificateCatalogSource.WindowsCurrentUserIntermediate => "Str_CertCatalog_WindowsUserIntermediates",
        CertificateCatalogSource.WindowsLocalMachineIntermediate => "Str_CertCatalog_WindowsMachineIntermediates",
        _ => "Str_CertCatalog_Local"
    });

    private async Task ImportAsync()
    {
        if (_busy || _closed) return;
        var picker = new FileDialog(FileDialogMode.Open)
        {
            Title = L("Str_CertCatalog_Import"),
            Filter = L("Str_Filter_Cert") + "|*.cer;*.crt;*.pem",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        int imported = 0;
        await UpdateCatalogAsync(() => imported = _catalog.Import(picker.FileName),
            () => string.Format(CultureInfo.CurrentCulture, L("Str_CertCatalog_Imported"), imported));
    }

    private async Task SetTrustAsync(bool trusted)
    {
        if (_busy || Selected is not { IsReadOnly: false } entry) return;
        if (trusted && !entry.IsCertificateAuthority) return;
        if (trusted && KillerDialog.Show(this,
                string.Format(CultureInfo.CurrentCulture, L("Str_CertCatalog_TrustPrompt"), entry.Subject),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await UpdateCatalogAsync(() => _catalog.SetTrusted(entry.Fingerprint, trusted));
    }

    private async Task RemoveAsync()
    {
        if (_busy || Selected is not { IsReadOnly: false } entry) return;
        if (KillerDialog.Show(this,
                string.Format(CultureInfo.CurrentCulture, L("Str_CertCatalog_RemovePrompt"), entry.Subject),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await UpdateCatalogAsync(() => _catalog.Remove(entry.Fingerprint));
    }

    private async Task UpdateCatalogAsync(Action update, Func<string>? successMessage = null)
    {
        if (_busy || _closed) return;
        var selected = SelectedKey;
        _mutating = true;
        SetBusy(true);
        ShowMessage(string.Empty);
        try
        {
            // A change that is refused leaves the catalog as it was, so the list stays as shown.
            try { await Task.Run(update); }
            catch (Exception ex)
            {
                ShowError(ex, "Str_CertCatalog_Error");
                return;
            }
            Changed = true;
            try
            {
                _entries = await Task.Run(() => _catalog.Read());
                ApplyFilter(selected);
                if (successMessage is not null) ShowMessage(successMessage());
            }
            catch (Exception ex)
            {
                _entries = [];
                ApplyFilter(null);
                ShowError(ex, "Str_CertCatalog_LoadError");
            }
        }
        finally
        {
            _mutating = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _import.IsEnabled = _refresh.IsEnabled = !busy;
        _close.IsEnabled = !_mutating;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = _list.Items.Count == 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void ShowMessage(string message)
    {
        _message.Text = message;
        _message.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowError(Exception exception, string prefixKey)
    {
        string message = L(prefixKey) + "\n" + Describe(exception);
        ShowMessage(message);
        KillerDialog.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RequestClose()
    {
        if (!_mutating) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_mutating)
        {
            e.Cancel = true;
            return;
        }
        _closed = true;
        SetBusy(true);
        base.OnClosing(e);
    }

    private static TextBlock Label(string text, bool muted = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedTextBrush" : "TextBrush");
        return label;
    }

    // An icon action named by its label key, keeping the automation id the catalog's tests find it by.
    private static Button ListAction(string key, int glyph, string automationId)
    {
        Button button = DialogParts.IconAction(key, glyph, (_, _) => { });
        AutomationProperties.SetName(button, L(key));
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    // Expected import and catalog problems in the user's language; anything else keeps its own text.
    private string Describe(Exception exception) => CertificateCatalog.ProblemOf(exception) is { } problem
        ? problem switch
        {
            CertificateCatalogProblem.WrongFileType => L("Str_CertCatalog_WrongFileType"),
            CertificateCatalogProblem.NotACertificate => L("Str_CertCatalog_NotACertificate"),
            CertificateCatalogProblem.PrivateKey => L("Str_CertCatalog_PrivateKey"),
            CertificateCatalogProblem.TooLarge => L("Str_CertCatalog_TooLarge"),
            CertificateCatalogProblem.CatalogFull => string.Format(CultureInfo.CurrentCulture,
                L("Str_CertCatalog_Full"), CertificateCatalog.MaximumCertificates),
            _ => string.Format(CultureInfo.CurrentCulture, L("Str_CertCatalog_Damaged"), _catalog.CatalogPath)
        }
        : exception.Message;
}
