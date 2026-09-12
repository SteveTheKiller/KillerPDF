using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF
{
    // #338: initial-view settings written into the catalog (open page + zoom, layout,
    // panel, toolbar chrome). Null unless the user touches the section, so a pure
    // metadata save never clobbers existing catalog preferences.
    internal sealed record InitialViewSpec(
        int PageIndex,
        PdfEngineIntegration.InitialViewZoom Zoom,
        PdfPageLayout Layout,
        PdfPageMode Mode,
        bool HideToolbar,
        bool HideMenuBar);

    // Read/edit the PDF Document Info dictionary (Title, Author, Subject, Keywords, Creator). Themed via
    // DialogChrome, no preview pane. Producer/dates/structure are shown read-only.
    internal sealed class DocumentInfoDialog : Window
    {
        private readonly PdfDocumentInformation _info;
        private readonly Action<PdfDocumentMetadata> _save;
        private readonly Action<InitialViewSpec?> _saveView;
        private readonly int _pageCount;
        private TextBox _title = null!, _author = null!, _subject = null!, _keywords = null!, _creator = null!;
        private TextBox _openPage = null!;
        private ComboBox _zoomCombo = null!, _layoutCombo = null!, _panelCombo = null!;
        private CheckBox _hideToolbar = null!, _hideMenuBar = null!;
        private bool _viewTouched;

        public bool Saved { get; private set; }
        public InitialViewSpec? View { get; private set; }

        public DocumentInfoDialog(Window owner, PdfDocumentInformation info,
            Action<PdfDocumentMetadata> save, Action<InitialViewSpec?> saveView,
            string? filePath, int openPage1Based, int pageCount)
        {
            _info = info;
            _save = save;
            _saveView = saveView;
            _pageCount = Math.Max(1, pageCount);
            Title = "KillerPDF - " + L("Str_DocInfo_Suffix");
            Width = 460;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            DialogChrome.Configure(this, owner);
            BuildUi(filePath, Math.Max(1, Math.Min(openPage1Based, _pageCount)));
        }

        private void BuildUi(string? filePath, int openPage1Based)
        {
            var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

            _title    = AddField(body, L("Str_DocInfo_Title"),    _info.Title);
            _author   = AddField(body, L("Str_DocInfo_Author"),   _info.Author);
            _subject  = AddField(body, L("Str_DocInfo_Subject"),  _info.Subject);
            _keywords = AddField(body, L("Str_DocInfo_Keywords"), _info.Keywords, wrap: true);
            _creator  = AddField(body, L("Str_DocInfo_Creator"),  _info.Creator);

            BuildInitialViewSection(body, openPage1Based);

            body.Children.Add(new TextBlock
            {
                Text = BuildSummary(filePath),
                FontFamily = UiKit.MonoFont, FontSize = 11,
                Foreground = UiKit.Brush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });

            var cancel = UiKit.Make(L("Str_DocInfo_Cancel"), accent: false);
            cancel.Click += (_, _2) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;   // Esc
            var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
            save.Click += (_, _2) => SaveAndClose();
            save.IsDefault = true;    // Enter
            var row = UiKit.ButtonRow(cancel, save);
            row.Margin = new Thickness(0, 16, 0, 0);
            body.Children.Add(row);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + L("Str_DocInfo_Suffix"),
                () => { DialogResult = false; Close(); }, body);

            Loaded += (_, _2) => _title.Focus();
        }

        private static TextBox AddField(StackPanel host, string label, string? value, bool wrap = false)
        {
            host.Children.Add(UiKit.GroupLabel(label));
            var f = UiKit.Field();
            f.Text = value ?? "";
            f.Margin = new Thickness(0, 0, 0, 8);
            // Every field wraps and grows with its content up to a cap, then scrolls - so long titles,
            // subjects, or keyword lists aren't cramped on a single line. Enter is not a newline (each value
            // stays a single metadata string). The `wrap` hint just gives the long-form fields more room.
            f.TextWrapping = TextWrapping.Wrap;
            f.AcceptsReturn = false;
            f.VerticalContentAlignment = VerticalAlignment.Top;
            f.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            f.MaxHeight = wrap ? 110 : 72;   // grow up to ~5 lines (keywords) / ~3 lines (others), then scroll
            host.Children.Add(f);
            return f;
        }

        // #338: initial-view picker. Every control marks the section touched; untouched means
        // View stays null and a metadata-only save leaves existing catalog prefs alone.
        private void BuildInitialViewSection(StackPanel body, int openPage1Based)
        {
            var header = UiKit.SectionHeader(L("Str_View_Initial"));
            header.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(header);

            void Touch() => _viewTouched = true;
            ComboBox Combo()
            {
                var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Height = 28 };
                if (Owner?.TryFindResource("DarkComboBox") is Style s) combo.Style = s;
                return combo;
            }
            // Handlers attach AFTER the initial SelectedIndex below: setting it fires
            // SelectionChanged, which must not count as the user touching the section.
            void Watch(ComboBox combo) => combo.SelectionChanged += (_, _) => Touch();

            body.Children.Add(UiKit.GroupLabel(L("Str_View_OpenPage")));
            _openPage = UiKit.Field();
            _openPage.Text = openPage1Based.ToString();
            _openPage.Margin = new Thickness(0, 0, 0, 8);
            _openPage.TextChanged += (_, _) => Touch();
            body.Children.Add(_openPage);

            body.Children.Add(UiKit.GroupLabel(L("Str_View_Zoom")));
            _zoomCombo = Combo();
            _zoomCombo.Items.Add(L("Str_Print_Fit"));
            _zoomCombo.Items.Add(L("Str_Zoom_FitWidth"));
            _zoomCombo.Items.Add(L("Str_Print_Actual"));
            _zoomCombo.SelectedIndex = 0;
            Watch(_zoomCombo);
            body.Children.Add(_zoomCombo);

            body.Children.Add(UiKit.GroupLabel(L("Str_View_Layout")));
            _layoutCombo = Combo();
            _layoutCombo.Items.Add(L("Str_View_Single"));
            _layoutCombo.Items.Add(L("Str_View_Continuous"));
            _layoutCombo.Items.Add(L("Str_View_TwoPage"));
            _layoutCombo.SelectedIndex = 0;
            Watch(_layoutCombo);
            body.Children.Add(_layoutCombo);

            body.Children.Add(UiKit.GroupLabel(L("Str_View_Panel")));
            _panelCombo = Combo();
            _panelCombo.Items.Add(L("Str_Margin_None"));
            _panelCombo.Items.Add(L("Str_View_PanelBookmarks"));
            _panelCombo.Items.Add(L("Str_View_PanelThumbs"));
            _panelCombo.SelectedIndex = 0;
            Watch(_panelCombo);
            body.Children.Add(_panelCombo);

            _hideToolbar = UiKit.CheckBox(L("Str_View_HideToolbar"));
            _hideToolbar.Margin = new Thickness(0, 2, 0, 2);
            _hideToolbar.Checked += (_, _) => Touch();
            _hideToolbar.Unchecked += (_, _) => Touch();
            body.Children.Add(_hideToolbar);
            _hideMenuBar = UiKit.CheckBox(L("Str_View_HideMenuBar"));
            _hideMenuBar.Margin = new Thickness(0, 2, 0, 2);
            _hideMenuBar.Checked += (_, _) => Touch();
            _hideMenuBar.Unchecked += (_, _) => Touch();
            body.Children.Add(_hideMenuBar);
        }

        private string BuildSummary(string? filePath)
        {
            var parts = new List<string>();
            string producer = _info.Producer ?? "";
            if (producer.Length > 0) parts.Add($"Producer: {producer}");
            parts.Add($"{L("Str_Print_Pages")}: {_info.PageCount}");
            parts.Add($"PDF {_info.Version.Major}.{_info.Version.Minor}");
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) parts.Add($"{new FileInfo(filePath).Length / 1024.0:N0} KB"); } catch { }
            return string.Join("\n", parts);
        }

        private void SaveAndClose()
        {
            _save(new PdfDocumentMetadata
            {
                Title = _title.Text,
                Author = _author.Text,
                Subject = _subject.Text,
                Keywords = _keywords.Text,
                Creator = _creator.Text,
                Producer = _info.Producer
            });
            if (_viewTouched)
            {
                int page = 1;
                _ = int.TryParse(_openPage.Text?.Trim(), out page);
                page = Math.Max(1, Math.Min(_pageCount, page));
                View = new InitialViewSpec(
                    page - 1,
                    _zoomCombo.SelectedIndex switch
                    {
                        1 => PdfEngineIntegration.InitialViewZoom.FitWidth,
                        2 => PdfEngineIntegration.InitialViewZoom.ActualSize,
                        _ => PdfEngineIntegration.InitialViewZoom.FitPage,
                    },
                    _layoutCombo.SelectedIndex switch
                    {
                        1 => PdfPageLayout.OneColumn,
                        2 => PdfPageLayout.TwoPageRight,
                        _ => PdfPageLayout.SinglePage,
                    },
                    _panelCombo.SelectedIndex switch
                    {
                        1 => PdfPageMode.UseOutlines,
                        2 => PdfPageMode.UseThumbs,
                        _ => PdfPageMode.UseNone,
                    },
                    _hideToolbar.IsChecked == true,
                    _hideMenuBar.IsChecked == true);
            }
            else View = null;
            _saveView(View);
            Saved = true;
            Close();
        }

        private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    }
}
