using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services.Signing;
using Microsoft.Win32;

namespace KillerPDF
{
    /// <summary>
    /// Themed modal dialog that cryptographically signs the open PDF with a certificate (a .pfx/.p12
    /// file, or one from the Windows store) and writes a NEW signed copy. This is the real digital
    /// signature - distinct from the drawn "Signature" stamp tool, which only places a picture.
    /// Chrome and colors mirror PrintPreviewWindow so every KillerPDF dialog looks identical.
    /// </summary>
    internal sealed class SignDocumentDialog : Window
    {
        private readonly string _sourcePdf;

        private RadioButton _fileRadio = null!;
        private RadioButton _storeRadio = null!;
        private TextBox _pfxBox = null!;
        private PasswordBox _pwBox = null!;
        private Button _browsePfx = null!;
        private ComboBox _storeCombo = null!;
        private TextBox _reasonBox = null!;
        private TextBox _locationBox = null!;
        private TextBox _contactBox = null!;
        private TextBox _outputBox = null!;
        private readonly List<X509Certificate2> _storeCerts = [];

        // Segoe MDL2 Assets close glyph, matching the main window + print dialog chrome.
        private const string CloseGlyph = "";

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];

        // Localized string from the active locale dictionary (falls back to the key if missing).
        private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;

        public SignDocumentDialog(Window? owner, string sourcePdf)
        {
            _sourcePdf = sourcePdf;
            Title = "KillerPDF - Digital Signature";
            Width = 470;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            DialogChrome.Configure(this, owner);
            BuildUi();
        }

        private void BuildUi()
        {
            var body = new StackPanel { Margin = new Thickness(20, 6, 20, 18) };

            body.Children.Add(new TextBlock
            {
                Text = string.Format(L("Str_Sign_Desc"), Path.GetFileName(_sourcePdf)),
                Foreground = R("MutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // --- Certificate source --------------------------------------------------------------
            body.Children.Add(Label(L("Str_Sign_Certificate")));

            _fileRadio = Radio(L("Str_Sign_FromFile"), true);
            _storeRadio = Radio(L("Str_Sign_FromStore"), false);
            _fileRadio.Checked += (_, _) => SyncSource();
            _storeRadio.Checked += (_, _) => SyncSource();
            body.Children.Add(_fileRadio);

            var fileRow = new Grid { Margin = new Thickness(20, 2, 0, 4) };
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _pfxBox = Field("");
            _pfxBox.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(_pfxBox, 0);
            _browsePfx = MakeButton(L("Str_Sign_Browse"), false);
            _browsePfx.Click += (_, _) => BrowsePfx();
            Grid.SetColumn(_browsePfx, 1);
            fileRow.Children.Add(_pfxBox);
            fileRow.Children.Add(_browsePfx);
            body.Children.Add(fileRow);

            body.Children.Add(new TextBlock { Text = L("Str_Sign_Password"), Foreground = R("MutedTextBrush"), FontSize = 11, Margin = new Thickness(20, 4, 0, 2) });
            _pwBox = new PasswordBox
            {
                Margin = new Thickness(20, 0, 0, 10),
                Background = R("BgCanvas"), Foreground = R("TextBrush"),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                CaretBrush = R("TextBrush"), Template = MakePasswordTemplate()
            };
            body.Children.Add(_pwBox);

            body.Children.Add(_storeRadio);
            _storeCombo = new ComboBox { Margin = new Thickness(20, 2, 0, 10), Height = 26 };
            ApplyComboStyle(_storeCombo);
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
            body.Children.Add(_storeCombo);

            // --- Metadata ------------------------------------------------------------------------
            body.Children.Add(Label(L("Str_Sign_Reason")));
            _reasonBox = Field(""); body.Children.Add(_reasonBox);
            body.Children.Add(Label(L("Str_Sign_Location")));
            _locationBox = Field(""); body.Children.Add(_locationBox);
            body.Children.Add(Label(L("Str_Sign_Contact")));
            _contactBox = Field(""); body.Children.Add(_contactBox);

            // --- Output --------------------------------------------------------------------------
            body.Children.Add(Label(L("Str_Sign_SaveAs")));
            var outRow = new Grid();
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _outputBox = Field(DefaultOutputPath());
            _outputBox.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(_outputBox, 0);
            var browseOut = MakeButton(L("Str_Sign_Browse"), false);
            browseOut.Click += (_, _) => BrowseOutput();
            Grid.SetColumn(browseOut, 1);
            outRow.Children.Add(_outputBox);
            outRow.Children.Add(browseOut);
            body.Children.Add(outRow);

            // --- Buttons -------------------------------------------------------------------------
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var sign = MakeButton(L("Str_Sign_Sign"), true);
            sign.Click += (_, _) => DoSign();
            sign.IsDefault = true;    // Enter
            var cancel = MakeButton(L("Str_Sign_Cancel"), false);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;   // Esc
            btnRow.Children.Add(sign);
            btnRow.Children.Add(cancel);
            body.Children.Add(btnRow);

            SyncSource();

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + L("Str_Sign_TitleSuffix"),
                () => { DialogResult = false; Close(); }, body);
        }

        private string DefaultOutputPath()
        {
            string dir = Path.GetDirectoryName(_sourcePdf) ?? "";
            string name = Path.GetFileNameWithoutExtension(_sourcePdf);
            return Path.Combine(dir, name + "-signed.pdf");
        }

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
                          { Filter = "Certificate files|*.pfx;*.p12|All files|*.*", Title = "Choose a signing certificate" };
            if (dlg.ShowDialog(this) == true) _pfxBox.Text = dlg.FileName;
        }

        private void BrowseOutput()
        {
            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
                          { Filter = "PDF files|*.pdf", Title = "Save signed PDF as", FileName = Path.GetFileName(_outputBox.Text) };
            if (dlg.ShowDialog(this) == true) _outputBox.Text = dlg.FileName;
        }

        private void DoSign()
        {
            ICertificateProvider provider;
            if (_fileRadio.IsChecked == true)
            {
                string pfx = _pfxBox.Text?.Trim() ?? "";
                if (!File.Exists(pfx)) { Warn("Choose a certificate file (.pfx / .p12) first."); return; }
                provider = new PfxFileCertificateProvider(pfx, _pwBox.Password);
            }
            else
            {
                int i = _storeCombo.SelectedIndex;
                if (i < 0 || i >= _storeCerts.Count) { Warn("No signing certificate is available in the Windows store."); return; }
                provider = new StoreCertificateProvider(_storeCerts[i]);
            }

            string output = _outputBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(output)) { Warn("Choose where to save the signed copy."); return; }

            X509Certificate2 cert;
            try { cert = provider.GetCertificate(); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // The raw Win32 text ("The specified network password is not correct.") is misleading -
                // nothing networked is involved. Almost always a wrong password or a non-.pfx file.
                Warn("Could not open the certificate.\n\nThe password may be incorrect, or the file is not a valid .pfx / .p12 certificate.");
                return;
            }
            catch (Exception ex) { Warn("Could not load the certificate:\n\n" + ex.Message); return; }

            try
            {
                new PdfSigner().Sign(_sourcePdf, output, cert,
                    new PdfSigner.SignInfo(_reasonBox.Text ?? "", _locationBox.Text ?? "", _contactBox.Text ?? ""));
            }
            catch (Exception ex)
            {
                Warn("Signing failed:\n\n" + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            KillerDialog.Show(this, L("Str_Dlg_SignedSavedTo") + "\n" + output, "Digital Signature", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void Warn(string msg) => KillerDialog.Show(this, msg, "Digital Signature", MessageBoxButton.OK, MessageBoxImage.Warning);

        // ---- themed control helpers (mirroring PrintPreviewWindow) -------------------------------
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private static TextBlock Label(string text) => new()
        { Text = text, Foreground = R("TextBrush"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };

        private RadioButton Radio(string text, bool isChecked)
        {
            var r = new RadioButton { Content = text, IsChecked = isChecked, GroupName = "CertSource", FontSize = 12, Margin = new Thickness(0, 4, 0, 2) };
            if (FindOwnerStyle("ThemeRadio") is Style s) r.Style = s; else r.Foreground = R("TextBrush");
            return r;
        }

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s) combo.Style = s;
            else { combo.Foreground = R("TextBrush"); combo.BorderBrush = R("CardBorderBrush"); }
            combo.Background = R("BgCanvas");
        }

        private TextBox Field(string text)
        {
            var tb = new TextBox
            {
                Text = text, Margin = new Thickness(0, 0, 0, 4),
                Background = R("BgCanvas"), Foreground = R("TextBrush"),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4), CaretBrush = R("TextBrush"),
                SelectionBrush = R("RowSelectedBrush"), SelectionTextBrush = R("TextBrush"),
                Template = MakeTextBoxTemplate()
            };
            return tb;
        }

        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(TextBox)) { VisualTree = b };
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            return ct;
        }

        private static ControlTemplate MakePasswordTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            b.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(PasswordBox)) { VisualTree = b };
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            return ct;
        }

        private static Button MakeButton(string label, bool accent) => UiKit.Make(label, accent);
    }
}
