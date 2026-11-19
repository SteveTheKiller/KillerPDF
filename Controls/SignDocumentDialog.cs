using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services.Signing;
using KillerPDF.Services;
using Layout = KillerPDF.Controls.PageWindowLayout;
using SignLayout = KillerPDF.Controls.SignDialogLayout;
using Kit = KillerPDF.UiKit;

namespace KillerPDF
{
    /// <summary>
    /// Themed modal dialog that cryptographically signs the open PDF with a certificate (a .pfx/.p12
    /// file, or one from the Windows store) and writes a NEW signed copy. This is the real digital
    /// signature - distinct from the drawn "Signature" stamp tool, which only places a picture.
    /// Two compact columns built from the page windows' parts (Print, Transform, Stamp): the
    /// certificate, signer details and timestamp on the left, the visible signature and the signed
    /// copy's path on the right, Cancel and Sign in the footer.
    /// </summary>
    internal sealed class SignDocumentDialog : Window
    {
        private readonly string _sourcePdf;
        private readonly string _suggestedSource;
        private readonly string? _suggestedName;

        private RadioButton _fileRadio = null!;
        private RadioButton _storeRadio = null!;
        private TextBox _pfxBox = null!;
        private PasswordBox _pwBox = null!;
        private Button _browsePfx = null!;
        private ComboBox _storeCombo = null!;
        private TextBox _reasonBox = null!;
        private TextBox _locationBox = null!;
        private TextBox _contactBox = null!;
        private CheckBox _timestamp = null!;
        private TextBox _timestampServer = null!;
        private CheckBox _visibleAppearance = null!;
        private TextBox _appearanceText = null!;
        private TextBox _pageBox = null!;
        private ComboBox _positionCombo = null!;
        private TextBox _appearanceWidth = null!;
        private TextBox _appearanceHeight = null!;
        private TextBox _appearanceFontSize = null!;
        private TextBlock _appearancePreview = null!;
        private TextBox _outputBox = null!;
        private readonly List<X509Certificate2> _storeCerts = [];
        private const string DefaultTimestampServer = "http://timestamp.digicert.com";

        // The sub-options of a certificate source sit indented under its radio button.
        private const double SourceIndent = 20;

        // Brush, never a SolidColorBrush cast: any theme (98SE among them) may state a surface as
        // a gradient, and a missing key becomes transparent instead of throwing.
        private static Brush R(string key) => Layout.ThemeBrush(Application.Current?.TryFindResource(key));

        // Localized string from the active locale dictionary (falls back to the key if missing).
        private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;

        // Pictures the signed copy left out because they could not be read, said in the result message.
        private readonly int _skippedPictures;

        public SignDocumentDialog(Window? owner, string sourcePdf, string? suggestedSource = null,
            string? suggestedName = null, int skippedPictures = 0)
        {
            _skippedPictures = skippedPictures;
            _sourcePdf = sourcePdf;
            _suggestedSource = suggestedSource ?? sourcePdf;
            _suggestedName = suggestedName;
            Title = "KillerPDF - " + L("Str_Sign_Name");
            // Two compact columns at a fixed opening width. The window opens exactly as tall as
            // they are plus the buttons, and never taller or wider than the work area; the columns
            // scroll there instead.
            Width = Math.Min(SignLayout.OpeningWidth, SystemParameters.WorkArea.Width);
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            // Resizable from every edge like the other redesigned windows, and it remembers its
            // size and position the way they do. The minimum is set from the content below, not
            // from the size it opened at, so a remembered larger size can still be made smaller.
            DialogFrame.Configure(this, owner, resizable: true);
            DialogFrame.KeepDeclaredMinimum(this);
            // Reuse the main window's themed scrollbar for the small-screen scroller, as Print does.
            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style scrollBarStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = scrollBarStyle;
            BuildUi();
            SourceInitialized += (_, _) => SizeFromContent();
            Closing += (_, _) => Controls.PageWindowSizing.Save(this, PlacementType, App.SetSetting);
        }

        // The settings name its remembered placement is stored under.
        private const string PlacementType = "Sign";

        // Before the first show: the content's height at the opening width is the window's height
        // and its minimum, as for the page windows. A remembered placement is then restored, raised
        // to that minimum, and kept on its monitor's work area.
        private void SizeFromContent()
        {
            if (Content is not FrameworkElement content) return;
            double width = Width;
            content.Measure(new Size(width, double.PositiveInfinity));
            double height = Math.Ceiling(Math.Min(content.DesiredSize.Height, SystemParameters.WorkArea.Height));
            SizeToContent = SizeToContent.Manual;
            MinWidth = width;
            MinHeight = height;
            Width = width;
            Height = height;
            if (Controls.PageWindowSizing.Restore(this, PlacementType, App.GetSetting))
                KillerFamily.Windowing.WorkAreaFit.Fit(this, keepMargin: false);
        }

        private void BuildUi()
        {
            // Controls are created in visual order, left column top to bottom, then the right
            // column, then the footer, so Tab follows what the eye reads.
            var left = BuildCertificateColumn();
            var right = BuildAppearanceColumn();

            _visibleAppearance.Checked += (_, _) => SyncAppearance();
            _visibleAppearance.Unchecked += (_, _) => SyncAppearance();
            _appearanceText.TextChanged += (_, _) => UpdateAppearancePreview();
            _reasonBox.TextChanged += (_, _) => UpdateAppearancePreview();
            _locationBox.TextChanged += (_, _) => UpdateAppearancePreview();

            // --- Buttons: Cancel then Sign, the family order, Sign as the accent on the right ----
            var cancel = MakeButton(L("Str_Sign_Cancel"), false);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;   // Esc
            var sign = MakeButton(L("Str_Sign_Sign"), true);
            sign.Click += (_, _) => DoSign();
            sign.IsDefault = true;    // Enter
            var footer = Layout.Footer(null, cancel, sign);
            footer.Margin = new Thickness(SignLayout.Inset, 12, SignLayout.Inset, 14);

            SyncSource();
            SyncTimestamp();
            SyncAppearance();

            var description = new TextBlock
            {
                Text = string.Format(L("Str_Sign_Desc"), Path.GetFileName(_suggestedSource)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(SignLayout.Inset, 4, SignLayout.Inset, 6)
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var columns = SignLayout.Columns(left, right);
            columns.Margin = new Thickness(SignLayout.Inset, 0, SignLayout.Inset, 0);
            // Only a work area smaller than the window scrolls; the footer stays pinned under it.
            var scroll = new ScrollViewer
            {
                Content = columns,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 1);
            Grid.SetRow(footer, 2);
            body.Children.Add(description);
            body.Children.Add(scroll);
            body.Children.Add(footer);

            Content = DialogFrame.Frame(this, Owner, "KillerPDF - " + L("Str_Sign_TitleSuffix"),
                () => { DialogResult = false; Close(); }, body);
        }

        // Left: where the certificate comes from, the optional signer details, the timestamp.
        private StackPanel BuildCertificateColumn()
        {
            var column = new StackPanel();

            // --- Certificate source --------------------------------------------------------------
            var certificateHeader = Header(L("Str_Sign_Certificate"));
            certificateHeader.Margin = new Thickness(0, 0, 0, 2);
            column.Children.Add(certificateHeader);

            _fileRadio = Radio(L("Str_Sign_FromFile"), true);
            _storeRadio = Radio(L("Str_Sign_FromStore"), false);
            _fileRadio.Checked += (_, _) => SyncSource();
            _storeRadio.Checked += (_, _) => SyncSource();
            column.Children.Add(_fileRadio);

            var fileRow = new Grid { Margin = new Thickness(SourceIndent, 2, 0, 6) };
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pfxBox = Field("", L("Str_Sign_FromFile"));
            _pfxBox.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(_pfxBox, 0);
            _browsePfx = MakeButton(L("Str_Sign_Browse"), false);
            _browsePfx.Height = Layout.ControlHeight;
            _browsePfx.Padding = SignLayout.CompactButtonPadding;
            _browsePfx.Click += (_, _) => BrowsePfx();
            Grid.SetColumn(_browsePfx, 1);
            fileRow.Children.Add(_pfxBox);
            fileRow.Children.Add(_browsePfx);
            column.Children.Add(fileRow);

            _pwBox = Kit.PasswordField();
            _pwBox.Height = Layout.ControlHeight;
            _pwBox.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(_pwBox, L("Str_Sign_Password"));
            DimWhenDisabled(_pwBox);
            var password = Layout.Labeled(L("Str_Sign_Password"), _pwBox);
            password.Margin = new Thickness(SourceIndent, 0, 0, 6);
            column.Children.Add(password);

            column.Children.Add(_storeRadio);
            _storeCombo = new ComboBox { Margin = new Thickness(SourceIndent, 2, 0, 0) };
            ApplyComboStyle(_storeCombo);
            AutomationProperties.SetName(_storeCombo, L("Str_Sign_FromStore"));
            try
            {
                foreach (var c in WindowsCertificateStore.ListSigningCertificates())
                {
                    _storeCerts.Add(c);
                    _storeCombo.Items.Add(new StoreCertificateProvider(c).DisplayName);
                }
            }
            catch { /* store unavailable - leave empty */ }
            if (_storeCombo.Items.Count > 0) _storeCombo.SelectedIndex = 0;
            column.Children.Add(_storeCombo);

            // --- Metadata: each optional field beside its label ---------------------------------
            column.Children.Add(Layout.Divider());
            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _reasonBox = DetailRow(details, 0, L("Str_Sign_Reason"));
            _locationBox = DetailRow(details, 1, L("Str_Sign_Location"));
            _contactBox = DetailRow(details, 2, L("Str_Sign_Contact"));
            column.Children.Add(details);

            // --- Trusted timestamp: the explanation rides in the tooltip ----------------------------
            column.Children.Add(Layout.Divider());
            _timestamp = SectionToggle(L("Str_Sign_Timestamp"), App.GetSetting("SignTimestamp") == "1");
            _timestamp.ToolTip = L("Str_Sign_TimestampHint");
            AutomationProperties.SetHelpText(_timestamp, L("Str_Sign_TimestampHint"));
            column.Children.Add(HeaderRow(_timestamp));
            _timestampServer = Field(App.GetSetting("SignTimestampServer") is { Length: > 0 } savedServer
                ? savedServer : DefaultTimestampServer, L("Str_Sign_TimestampServer"));
            _timestampServer.ToolTip = L("Str_Sign_TimestampHint");
            ToolTipService.SetShowOnDisabled(_timestampServer, true);
            AutomationProperties.SetHelpText(_timestampServer, L("Str_Sign_TimestampHint"));
            var server = Layout.Labeled(L("Str_Sign_TimestampServer"), _timestampServer);
            server.Margin = new Thickness(0);
            column.Children.Add(server);
            _timestamp.Checked += (_, _) => SyncTimestamp();
            _timestamp.Unchecked += (_, _) => SyncTimestamp();

            return column;
        }

        // Right: the visible signature with its placement and preview, then where the copy goes.
        private StackPanel BuildAppearanceColumn()
        {
            var column = new StackPanel();

            _visibleAppearance = SectionToggle(L("Str_Sign_VisibleAppearance"), true);
            var visibleHeader = HeaderRow(_visibleAppearance);
            visibleHeader.Margin = new Thickness(0, 0, 0, 2);
            column.Children.Add(visibleHeader);

            _appearanceText = Field(L("Str_Sign_AppearanceDefault"), L("Str_Sign_AppearanceText"));
            _appearanceText.AcceptsReturn = true;
            _appearanceText.Height = 76;
            _appearanceText.VerticalContentAlignment = VerticalAlignment.Top;
            _appearanceText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            column.Children.Add(Layout.Labeled(L("Str_Sign_AppearanceText"), _appearanceText));

            // Page and Position on one row, label over each field.
            var placement = new Grid();
            placement.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            placement.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            placement.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _pageBox = Field("1", L("Str_Sign_Page"));
            placement.Children.Add(Layout.Labeled(L("Str_Sign_Page"), _pageBox));
            _positionCombo = new ComboBox();
            foreach (string key in new[] { "Str_Sign_BottomLeft", "Str_Sign_BottomRight", "Str_Sign_TopLeft", "Str_Sign_TopRight" })
                _positionCombo.Items.Add(L(key));
            _positionCombo.SelectedIndex = 0; ApplyComboStyle(_positionCombo);
            AutomationProperties.SetName(_positionCombo, L("Str_Sign_Position"));
            var position = Layout.Labeled(L("Str_Sign_Position"), _positionCombo);
            Grid.SetColumn(position, 2);
            placement.Children.Add(position);
            column.Children.Add(placement);

            // Width, Height and Text size share one row in three equal cells.
            var dimensions = new Grid();
            for (int i = 0; i < 5; i++)
                dimensions.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = i % 2 == 1 ? new GridLength(8) : new GridLength(1, GridUnitType.Star) });
            dimensions.Children.Add(DimensionField(L("Str_Sign_Width"), "240", 0, out _appearanceWidth));
            dimensions.Children.Add(DimensionField(L("Str_Sign_Height"), "84", 2, out _appearanceHeight));
            dimensions.Children.Add(DimensionField(L("Str_Sign_FontSize"), "10", 4, out _appearanceFontSize));
            column.Children.Add(dimensions);

            // The preview is paper, so it stays white with black text in every theme.
            var previewBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = R("PrimaryBrush"),
                BorderThickness = new Thickness(2),
                CornerRadius = Kit.RadControl,
                Padding = new Thickness(10, 6, 10, 6),
                Height = 96
            };
            previewBorder.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            _appearancePreview = new TextBlock
            {
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            previewBorder.Child = _appearancePreview;
            column.Children.Add(previewBorder);

            // --- Output --------------------------------------------------------------------------
            column.Children.Add(Layout.Divider());
            var saveHeader = Header(L("Str_Sign_SaveAs"));
            saveHeader.Margin = new Thickness(0, 0, 0, 4);
            column.Children.Add(saveHeader);
            var outRow = new Grid();
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _outputBox = Field(DefaultOutputPath(), L("Str_Sign_SaveAs"));
            _outputBox.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(_outputBox, 0);
            var browseOut = MakeButton(L("Str_Sign_Browse"), false);
            browseOut.Height = Layout.ControlHeight;
            browseOut.Padding = SignLayout.CompactButtonPadding;
            browseOut.Click += (_, _) => BrowseOutput();
            Grid.SetColumn(browseOut, 1);
            outRow.Children.Add(_outputBox);
            outRow.Children.Add(browseOut);
            column.Children.Add(outRow);

            return column;
        }

        private string DefaultOutputPath()
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads)) downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!Directory.Exists(downloads)) downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return SigningOutputPath.Create(_suggestedSource, App.IsCleanupOwnedPath(_suggestedSource), downloads, _suggestedName);
        }

        private void SyncTimestamp() => _timestampServer.IsEnabled = _timestamp.IsChecked == true;

        // Enable only the inputs for the selected certificate source.
        private void SyncSource()
        {
            bool file = _fileRadio.IsChecked == true;
            _pfxBox.IsEnabled = _browsePfx.IsEnabled = _pwBox.IsEnabled = file;
            _storeCombo.IsEnabled = !file;
        }

        private void BrowsePfx()
        {
            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Open)
                          { Filter = L("Str_Filter_Cert") + "|*.pfx;*.p12|" + L("Str_Filter_AllFiles") + "|*.*", Title = L("Str_Sign_ChooseCert") };
            if (dlg.ShowDialog(this) == true) _pfxBox.Text = dlg.FileName;
        }

        private void BrowseOutput()
        {
            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
                          { Filter = L("Str_Filter_Pdf") + "|*.pdf", Title = L("Str_Sign_SaveAs"), FileName = Path.GetFileName(_outputBox.Text) };
            string? directory = Path.GetDirectoryName(_outputBox.Text);
            if (Directory.Exists(directory)) dlg.InitialDirectory = directory;
            if (dlg.ShowDialog(this) == true) _outputBox.Text = dlg.FileName;
        }

        private void DoSign()
        {
            ICertificateProvider provider;
            if (_fileRadio.IsChecked == true)
            {
                string pfx = _pfxBox.Text?.Trim() ?? "";
                if (!File.Exists(pfx)) { Warn(L("Str_Sign_NeedCertFile")); return; }
                provider = new PfxFileCertificateProvider(pfx, _pwBox.Password);
            }
            else
            {
                int i = _storeCombo.SelectedIndex;
                if (i < 0 || i >= _storeCerts.Count) { Warn(L("Str_Sign_NoStoreCert")); return; }
                provider = new StoreCertificateProvider(_storeCerts[i]);
            }

            string output = _outputBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(output)) { Warn(L("Str_Sign_NeedOutput")); return; }
            if (App.IsCleanupOwnedPath(output))
            {
                _outputBox.Text = DefaultOutputPath();
                BrowseOutput();
                return;
            }
            // The default -signed.pdf name repeats, and a typed path skips Browse's prompt, so the
            // final command asks before it replaces anything (BF-A006).
            if (!Services.OutputOverwrite.Confirm(this, output, out Services.FileRevision? approved)) return;

            X509Certificate2 cert;
            try { cert = provider.GetCertificate(); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // The raw Win32 text ("The specified network password is not correct.") is misleading -
                // nothing networked is involved. Almost always a wrong password or a non-.pfx file.
                Warn(L("Str_Sign_BadCert"));
                return;
            }
            catch (Exception ex) { Warn(L("Str_Sign_CertLoadFailed") + "\n\n" + ex.Message); return; }

            using X509Certificate2? importedCertificate = provider is PfxFileCertificateProvider ? cert : null;

            string? timestampServer = null;
            if (_timestamp.IsChecked == true)
            {
                string server = _timestampServer.Text?.Trim() ?? "";
                if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? serverUri)
                    || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
                { Warn(L("Str_Sign_BadTimestampServer")); return; }
                timestampServer = server;
            }
            App.SetSetting("SignTimestamp", _timestamp.IsChecked == true ? "1" : "0");
            if (timestampServer is not null) App.SetSetting("SignTimestampServer", timestampServer);

            PdfSigner.VisibleSignatureInfo? appearance;
            try
            {
                if (!TryBuildVisibleAppearance(cert, out appearance)) return;
            }
            catch (Exception ex)
            {
                Warn(L("Str_Sign_BadAppearance") + "\n\n" + ex.Message);
                return;
            }
            // A timestamp waits on the server for up to 30 seconds, so show that work is underway.
            if (!Services.OutputOverwrite.StillAsApproved(this, output, approved)) return;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                new PdfSigner().Sign(_sourcePdf, output, cert,
                    new PdfSigner.SignInfo(_reasonBox.Text ?? "", _locationBox.Text ?? "",
                        _contactBox.Text ?? "", appearance, timestampServer));
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                Warn(L(timestampServer is not null && IsTimestampFailure(ex) ? "Str_Sign_TimestampFailed" : "Str_Sign_Failed")
                    + "\n\n" + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            KillerDialog.Show(this, KillerPDF.Services.PicturesSkippedNote.Append(L("Str_Dlg_SignedSavedTo") + "\n" + output,
                _skippedPictures, L("Str_Save_PicturesSkipped")), L("Str_Sign_Name"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        // The engine reports every network, reply and verification failure of the timestamp step
        // with its own exception, so it is not mistaken for a certificate problem.
        private static bool IsTimestampFailure(Exception ex) =>
            ex is KillerPdf.Engine.Signing.PdfTimestampException;

        private void Warn(string msg) => KillerDialog.Show(this, msg, L("Str_Sign_Name"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // A label over a field in one of the dimension row's cells.
        private static StackPanel DimensionField(string label, string value, int gridColumn, out TextBox box)
        {
            box = Field(value, label);
            var cell = Layout.Labeled(label, box);
            Grid.SetColumn(cell, gridColumn);
            return cell;
        }

        // One optional detail: its label in the shared label column, the field filling the rest.
        private static TextBox DetailRow(Grid grid, int row, string label)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Layout.Label(label);
            text.FontSize = 12;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 10, row < 2 ? 6 : 0);
            Grid.SetRow(text, row);
            var box = Field("", label);
            box.Margin = new Thickness(0, 0, 0, row < 2 ? 6 : 0);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
            return box;
        }

        private void SyncAppearance()
        {
            bool enabled = _visibleAppearance.IsChecked == true;
            _appearanceText.IsEnabled = _pageBox.IsEnabled = _positionCombo.IsEnabled =
                _appearanceWidth.IsEnabled = _appearanceHeight.IsEnabled =
                _appearanceFontSize.IsEnabled = enabled;
            _appearancePreview.Opacity = enabled ? 1 : 0.35;
            UpdateAppearancePreview();
        }

        private void UpdateAppearancePreview()
        {
            if (_appearancePreview is null || _appearanceText is null) return;
            _appearancePreview.Text = ExpandAppearanceText(
                _appearanceText.Text, L("Str_Sign_PreviewSigner"));
        }

        private string ExpandAppearanceText(string template, string signerName) => template
            .Replace("{name}", signerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{reason}", _reasonBox.Text ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{location}", _locationBox.Text ?? "", StringComparison.OrdinalIgnoreCase);

        private bool TryBuildVisibleAppearance(X509Certificate2 certificate,
            out PdfSigner.VisibleSignatureInfo? appearance)
        {
            appearance = null;
            if (_visibleAppearance.IsChecked != true) return true;
            if (!int.TryParse(_pageBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int pageNumber))
            { Warn(L("Str_Sign_BadAppearance")); return false; }
            var pages = PdfEngineIntegration.ReadPageInformation(_sourcePdf, null);   // the plain copy being signed
            if (pageNumber < 1 || pageNumber > pages.Count)
            { Warn(L("Str_Sign_BadAppearance")); return false; }
            if (!TryDimension(_appearanceWidth, out double width)
                || !TryDimension(_appearanceHeight, out double height)
                || !TryDimension(_appearanceFontSize, out double fontSize)
                || width < 72 || height < 36 || fontSize is < 6 or > 72)
            { Warn(L("Str_Sign_BadAppearance")); return false; }

            var page = pages[pageNumber - 1];
            const double margin = 36;
            width = Math.Min(width, Math.Max(1, page.Width - margin * 2));
            height = Math.Min(height, Math.Max(1, page.Height - margin * 2));
            bool right = _positionCombo.SelectedIndex is 1 or 3;
            bool top = _positionCombo.SelectedIndex is 2 or 3;
            double left = right ? page.Width - margin - width : margin;
            double bottom = top ? page.Height - margin - height : margin;
            string signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(signer)) signer = certificate.Subject;
            appearance = new PdfSigner.VisibleSignatureInfo(pageNumber - 1, left, bottom, width, height,
                fontSize, ExpandAppearanceText(_appearanceText.Text, signer));
            return true;

            static bool TryDimension(TextBox box, out double value) =>
                double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && double.IsFinite(value);
        }

        // ---- themed control helpers (the page windows' parts) ---------------------------------
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        // A section title in the page windows' header style: 11 semibold muted in a 28 high row.
        private static TextBlock Header(string text)
        {
            var header = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Height = Layout.HeaderHeight, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(0, 7, 0, 0)
            };
            header.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            header.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return header;
        }

        // An option that turns a whole section on, as Stamp's Page numbers and Watermark toggles.
        private static CheckBox SectionToggle(string text, bool on)
        {
            var check = Kit.CheckBox(text);
            check.IsChecked = on;
            check.FontSize = 13;
            check.FontWeight = FontWeights.SemiBold;
            check.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(check, text);
            return check;
        }

        // A section toggle in a 28 high header row.
        private static Border HeaderRow(CheckBox toggle) => new()
        {
            Height = Layout.HeaderHeight, Margin = new Thickness(0, 0, 0, 4), Child = toggle
        };

        private RadioButton Radio(string text, bool isChecked)
        {
            var r = new RadioButton { Content = text, IsChecked = isChecked, GroupName = "CertSource", FontSize = 12, Margin = new Thickness(0, 4, 0, 2) };
            if (FindOwnerStyle("ThemeRadio") is Style s) r.Style = s; else r.Foreground = R("TextBrush");
            return r;
        }

        // Print's combo treatment: 26 high, the owner's themed style, the field brush only as a fallback.
        private void ApplyComboStyle(ComboBox combo)
        {
            combo.Height = Layout.ControlHeight;
            if (FindOwnerStyle("DarkComboBox") is Style s)
            {
                combo.Style = s;
            }
            else
            {
                combo.Foreground = R("TextBrush");
                combo.BorderBrush = R("CardBorderBrush");
                combo.Background = R("ComboFieldBrush");
            }
        }

        // The shared themed field (98SE's sunken bevel included), 26 high, named for screen readers.
        private static TextBox Field(string text, string accessibleName)
        {
            var tb = Kit.Field();
            tb.Text = text;
            tb.Height = Layout.ControlHeight;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(tb, accessibleName);
            DimWhenDisabled(tb);
            return tb;
        }

        // The fields read as unavailable while their option is off, as they always have.
        private static void DimWhenDisabled(Control control)
            => control.IsEnabledChanged += (_, _) => control.Opacity = control.IsEnabled ? 1 : 0.4;

        private static Button MakeButton(string label, bool accent) => Kit.Make(label, accent);
    }
}
