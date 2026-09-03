using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF
{
    // Export pages as images (#132): PNG/JPEG + DPI + page range, themed via DialogChrome like
    // Document Info. The destination and base file name are picked afterwards with the standard
    // save dialog; pages are written as <base>-page-NNN.<ext> through the same render pipeline
    // the CLI --to-image command uses (FileOperations.ExportImages_Click).
    internal sealed class ExportImagesDialog : Window
    {
        private RadioButton _png = null!, _jpg = null!;
        private TextBox _dpi = null!, _range = null!;
        private TextBlock _pixelDimensions = null!;
        private readonly double _pageWidthPoints;
        private readonly double _pageHeightPoints;

        public bool Confirmed { get; private set; }
        public bool Jpeg { get; private set; }
        public double Dpi { get; private set; } = 150;
        public string Range { get; private set; } = "";

        /// <summary>presetRange seeds the page-range field - the Pages panel's per-page export
        /// (#207) opens the same dialog scoped to the clicked page(s), still editable.</summary>
        public ExportImagesDialog(Window owner, string presetRange = "",
            double pageWidthPoints = 0, double pageHeightPoints = 0)
        {
            _pageWidthPoints = pageWidthPoints;
            _pageHeightPoints = pageHeightPoints;
            Title = "KillerPDF - " + L("Str_ExportImg_Suffix");
            // Width follows the caption. "Export Pages as Images" is 22 characters in en-US and
            // up to 35 translated, which ran the title under the close button at a fixed 380 (#223).
            MinWidth = 380;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            DialogChrome.Configure(this, owner);
            BuildUi();
            _range.Text = presetRange;
        }

        private void BuildUi()
        {
            var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

            body.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Format")));
            var formatRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _png = UiKit.Radio("PNG");
            _png.IsChecked = true;
            _png.Margin = new Thickness(0, 0, 14, 0);
            _jpg = UiKit.Radio("JPEG");
            formatRow.Children.Add(_png);
            formatRow.Children.Add(_jpg);
            body.Children.Add(formatRow);

            body.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Dpi")));
            _dpi = UiKit.Field();
            _dpi.Text = "150";
            _dpi.TextChanged += (_, _) => UpdatePixelDimensions();
            body.Children.Add(_dpi);
            _pixelDimensions = new TextBlock
            {
                Foreground = UiKit.Brush("MutedTextBrush"),
                FontFamily = UiKit.MonoFont,
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 8)
            };
            body.Children.Add(_pixelDimensions);
            UpdatePixelDimensions();

            body.Children.Add(UiKit.GroupLabel(L("Str_Stamp_Pages")));
            _range = UiKit.Field();
            _range.ToolTip = L("Str_Crop_RangeTip");
            _range.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(_range);

            var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
            cancel.Click += (_, _2) => { Confirmed = false; Close(); };
            cancel.IsCancel = true;   // Esc
            var export = UiKit.Make(L("Str_ExportImg_Export"), accent: true);
            export.Click += (_, _2) => Commit();
            export.IsDefault = true;  // Enter
            var row = UiKit.ButtonRow(cancel, export);
            row.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(row);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + L("Str_ExportImg_Suffix"),
                () => { Confirmed = false; Close(); }, body);

            Loaded += (_, _2) => _dpi.Focus();
        }

        private void Commit()
        {
            Jpeg = _jpg.IsChecked == true;
            // Same accepted DPI window as the CLI (24-1200); anything unparsable falls back to 150.
            Dpi = double.TryParse(_dpi.Text.Trim(), out double d) && d >= 24 && d <= 1200 ? d : 150;
            Range = _range.Text.Trim();
            Confirmed = true;
            Close();
        }

        private void UpdatePixelDimensions()
        {
            if (_pixelDimensions is null) return;
            double dpi = double.TryParse(_dpi.Text.Trim(), out double parsed)
                && parsed >= 24 && parsed <= 1200 ? parsed : 150;
            var (width, height) = OutputPixelDimensions.FromPoints(
                _pageWidthPoints, _pageHeightPoints, dpi);
            _pixelDimensions.Text = width > 0 && height > 0
                ? string.Format(L("Str_OutputPixels"), width, height) + "  |  " +
                  OutputPixelDimensions.ScaleLabel(width, height,
                      OutputPixelDimensions.FromPoints(_pageWidthPoints, _pageHeightPoints, 150).Width,
                      OutputPixelDimensions.FromPoints(_pageWidthPoints, _pageHeightPoints, 150).Height)
                : "";
        }

        private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    }
}
