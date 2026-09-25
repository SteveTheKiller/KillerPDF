using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class InitialViewDialog : Window
{
    private readonly PdfInitialView _current;
    private readonly int _pageCount;
    private readonly ComboBox _layout;
    private readonly ComboBox _panel;
    private readonly ComboBox _zoom;
    private readonly TextBox _page;
    private readonly TextBox _zoomPercent;
    private readonly CheckBox _hideToolbar;
    private readonly CheckBox _hideMenu;
    private readonly CheckBox _hideWindowUi;
    private readonly CheckBox _fitWindow;
    private readonly CheckBox _centerWindow;
    private readonly CheckBox _displayTitle;
    private readonly CheckBox _pickTray;

    internal PdfInitialView? InitialView { get; private set; }

    internal InitialViewDialog(Window owner, PdfInitialView current, int pageCount)
    {
        _current = current;
        _pageCount = pageCount;
        Title = "KillerPDF - " + L("Str_DocumentTools_InitialView");
        Width = 470;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        _layout = AddCombo(body, L("Str_InitialView_Layout"), LayoutChoices(),
            choice => choice.Value == current.PageLayout);
        _panel = AddCombo(body, L("Str_InitialView_Panel"), PanelChoices(),
            choice => choice.Value == current.PageMode);

        var opening = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        opening.ColumnDefinitions.Add(new ColumnDefinition());
        opening.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        opening.ColumnDefinitions.Add(new ColumnDefinition());
        _page = AddField(opening, 0, L("Str_InitialView_OpenPage"));
        _page.Text = current.PageIndex is int page ? (page + 1).ToString(CultureInfo.CurrentCulture) : "";
        _zoomPercent = AddField(opening, 2, L("Str_InitialView_ZoomPercent"));
        _zoomPercent.Text = CurrentZoomPercent(current)?.ToString("0.##", CultureInfo.CurrentCulture) ?? "100";
        body.Children.Add(opening);

        _zoom = AddCombo(body, L("Str_InitialView_Zoom"), ZoomChoices(),
            choice => choice.Value == CurrentZoom(current));
        _hideToolbar = AddCheck(body, "Str_InitialView_HideToolbar", current.ViewerPreferences.HideToolbar);
        _hideMenu = AddCheck(body, "Str_InitialView_HideMenu", current.ViewerPreferences.HideMenuBar);
        _hideWindowUi = AddCheck(body, "Str_InitialView_HideWindowUi", current.ViewerPreferences.HideWindowUi);
        _fitWindow = AddCheck(body, "Str_InitialView_FitWindow", current.ViewerPreferences.FitWindow);
        _centerWindow = AddCheck(body, "Str_InitialView_CenterWindow", current.ViewerPreferences.CenterWindow);
        _displayTitle = AddCheck(body, "Str_InitialView_DisplayTitle", current.ViewerPreferences.DisplayDocumentTitle);
        _pickTray = AddCheck(body, "Str_InitialView_PickTray", current.ViewerPreferences.PickTrayByPdfSize);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _layout.Focus();
    }

    private void Commit()
    {
        int? pageIndex = null;
        PdfDestination? destination = null;
        string? namedDestination = null;
        string pageText = _page.Text.Trim();
        ZoomChoice zoom = ((Choice<ZoomChoice>)_zoom.SelectedItem).Value;
        if (pageText.Length == 0 && zoom == ZoomChoice.Preserve)
        {
            pageIndex = _current.PageIndex;
            destination = _current.Destination;
            namedDestination = _current.NamedDestination;
        }
        else
        {
            int page = 1;
            if (pageText.Length != 0
                && (!int.TryParse(pageText, NumberStyles.Integer, CultureInfo.CurrentCulture, out page)
                    || page < 1 || page > _pageCount))
            {
                Invalid();
                return;
            }
            pageIndex = page - 1;
            destination = Destination(zoom);
            if (destination is null)
            {
                Invalid();
                return;
            }
        }

        PdfViewerPreferences preferences = _current.ViewerPreferences with
        {
            HideToolbar = _hideToolbar.IsChecked == true,
            HideMenuBar = _hideMenu.IsChecked == true,
            HideWindowUi = _hideWindowUi.IsChecked == true,
            FitWindow = _fitWindow.IsChecked == true,
            CenterWindow = _centerWindow.IsChecked == true,
            DisplayDocumentTitle = _displayTitle.IsChecked == true,
            PickTrayByPdfSize = _pickTray.IsChecked == true
        };
        InitialView = new PdfInitialView
        {
            PageLayout = ((Choice<PdfPageLayout?>)_layout.SelectedItem).Value,
            PageMode = ((Choice<PdfPageMode?>)_panel.SelectedItem).Value,
            PageIndex = pageIndex,
            Destination = destination,
            NamedDestination = namedDestination,
            ViewerPreferences = preferences
        };
        DialogResult = true;
        Close();
    }

    private PdfDestination? Destination(ZoomChoice zoom) => zoom switch
    {
        ZoomChoice.Preserve when _current.Destination is not null => _current.Destination,
        ZoomChoice.Preserve => PdfDestination.FitPage(),
        ZoomChoice.FitPage => PdfDestination.FitPage(),
        ZoomChoice.FitWidth => PdfDestination.FitWidth(),
        ZoomChoice.FitHeight => PdfDestination.FitHeight(),
        ZoomChoice.ActualSize => PdfDestination.At(zoom: 1),
        ZoomChoice.Custom when double.TryParse(_zoomPercent.Text.Trim(), NumberStyles.Float,
            CultureInfo.CurrentCulture, out double percent) && percent is >= 1 and <= 6400
            => PdfDestination.At(zoom: percent / 100.0),
        _ => null
    };

    private void Invalid() => KillerDialog.Show(this, L("Str_InitialView_Invalid"),
        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static ComboBox AddCombo<T>(StackPanel host, string label,
        Choice<T>[] choices, System.Func<Choice<T>, bool> selected)
    {
        host.Children.Add(UiKit.GroupLabel(label));
        var combo = new ComboBox
        {
            Height = 28,
            Margin = new Thickness(0, 0, 0, 10),
            ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(selected) ?? choices[0]
        };
        if (Application.Current?.TryFindResource("DarkComboBox") is Style style)
            combo.Style = style;
        host.Children.Add(combo);
        return combo;
    }

    private static TextBox AddField(Grid host, int column, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(UiKit.GroupLabel(label));
        TextBox field = UiKit.Field();
        panel.Children.Add(field);
        Grid.SetColumn(panel, column);
        host.Children.Add(panel);
        return field;
    }

    private static CheckBox AddCheck(StackPanel host, string key, bool value)
    {
        CheckBox check = UiKit.CheckBox(L(key));
        check.IsChecked = value;
        check.Margin = new Thickness(0, 2, 0, 4);
        host.Children.Add(check);
        return check;
    }

    private static Choice<PdfPageLayout?>[] LayoutChoices() =>
    [
        new(L("Str_InitialView_Default"), null),
        new(L("Str_View_Single"), PdfPageLayout.SinglePage),
        new(L("Str_View_Continuous"), PdfPageLayout.OneColumn),
        new(L("Str_InitialView_TwoColumnsLeft"), PdfPageLayout.TwoColumnLeft),
        new(L("Str_InitialView_TwoColumnsRight"), PdfPageLayout.TwoColumnRight),
        new(L("Str_InitialView_TwoPagesLeft"), PdfPageLayout.TwoPageLeft),
        new(L("Str_InitialView_TwoPagesRight"), PdfPageLayout.TwoPageRight)
    ];

    private static Choice<PdfPageMode?>[] PanelChoices() =>
    [
        new(L("Str_InitialView_Default"), null),
        new(L("Str_InitialView_NoPanel"), PdfPageMode.UseNone),
        new(L("Str_InitialView_Outlines"), PdfPageMode.UseOutlines),
        new(L("Str_InitialView_Thumbnails"), PdfPageMode.UseThumbs),
        new(L("Str_KS_FullScreen"), PdfPageMode.FullScreen),
        new(L("Str_InitialView_Layers"), PdfPageMode.UseOptionalContent),
        new(L("Str_InitialView_Attachments"), PdfPageMode.UseAttachments)
    ];

    private static Choice<ZoomChoice>[] ZoomChoices() =>
    [
        new(L("Str_InitialView_Default"), ZoomChoice.Preserve),
        new(L("Str_Zoom_FitPage"), ZoomChoice.FitPage),
        new(L("Str_Zoom_FitWidth"), ZoomChoice.FitWidth),
        new(L("Str_InitialView_FitHeight"), ZoomChoice.FitHeight),
        new(L("Str_InitialView_ActualSize"), ZoomChoice.ActualSize),
        new(L("Str_InitialView_CustomZoom"), ZoomChoice.Custom)
    ];

    private static ZoomChoice CurrentZoom(PdfInitialView view) => view.Destination?.Kind switch
    {
        PdfDestinationKind.Fit => ZoomChoice.FitPage,
        PdfDestinationKind.FitH => ZoomChoice.FitWidth,
        PdfDestinationKind.FitV => ZoomChoice.FitHeight,
        PdfDestinationKind.Xyz when CurrentZoomPercent(view) == 100 => ZoomChoice.ActualSize,
        PdfDestinationKind.Xyz when CurrentZoomPercent(view).HasValue => ZoomChoice.Custom,
        _ => ZoomChoice.Preserve
    };

    private static double? CurrentZoomPercent(PdfInitialView view) =>
        view.Destination?.Kind == PdfDestinationKind.Xyz
        && view.Destination.Values.Count >= 3
        && view.Destination.Values[2] is double zoom ? zoom * 100 : null;

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private enum ZoomChoice { Preserve, FitPage, FitWidth, FitHeight, ActualSize, Custom }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
