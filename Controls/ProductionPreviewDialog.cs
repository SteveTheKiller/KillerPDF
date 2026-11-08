using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

namespace KillerPDF;

internal sealed class ProductionPreviewDialog : Window
{
    internal ProductionPreviewDialog(Window owner, string title, byte[] bytes)
        : this(owner, title, new (string Name, byte[] Bytes)[] { (title, bytes) }) { }

    internal ProductionPreviewDialog(Window owner, string title, IReadOnlyList<(string Name, byte[] Bytes)> documents)
    {
        var entries = documents.SelectMany(item =>
        {
            var document = PdfDocument.Open(item.Bytes);
            return PdfPageInformation.Read(document).Select((page, index) =>
                (Document: document, Page: page, Index: index, Label: item.Name + ": " + L("Str_AutoBookmarks_Page") + " " + (index + 1)));
        }).ToArray();
        Title = "KillerPDF - " + title; Width = 950; Height = 720; MinWidth = 500; MinHeight = 400;
        UseLayoutRounding = true; DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.IsDefault = true; save.Click += (_, _) => { DialogResult = true; };
        var buttons = UiKit.ButtonRow(cancel, save); DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
        var picker = new ComboBox { Style = UiKit.DarkComboStyle, Margin = new Thickness(0, 0, 0, 10),
            ItemsSource = entries.Select(entry => entry.Label).ToArray() };
        System.Windows.Automation.AutomationProperties.SetName(picker, L("Str_Stamp_Pages"));
        DockPanel.SetDock(picker, Dock.Top); body.Children.Add(picker);
        var image = new Image { Stretch = Stretch.Uniform };
        body.Children.Add(new ScrollViewer { Content = image, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        picker.SelectionChanged += (_, _) =>
        {
            try
            {
                int index = picker.SelectedIndex; if (index < 0) return;
                var entry = entries[index]; var page = entry.Page; bool sideways = page.Rotation % 180 != 0;
                double ratio = sideways ? page.Width / page.Height : page.Height / page.Width;
                var rendered = new PdfPageRenderer(entry.Document).Render(entry.Index,
                    KillerPDF.Services.WorkingCmykProfiles.Apply(new PdfRenderOptions(800, Math.Max(1, (int)Math.Round(800 * ratio)))));
                var bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32,
                    null, rendered.Pixels.ToArray(), rendered.Width * 4);
                bitmap.Freeze(); image.Source = bitmap; save.IsEnabled = true;
            }
            catch (Exception error)
            {
                save.IsEnabled = false;
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + error.Message);
            }
        };
        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => picker.SelectedIndex = 0;
    }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
