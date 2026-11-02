using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Security;

internal static partial class Program
{
    private static void ArchiveMixed()
    {
        byte[] pdf = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "keep", 20, 200, 150, 25, "MIXED").Build();
        byte[] protectedPdf = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" }).Build();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32,
            null, new byte[] { 255, 0, 0, 255 }, 4)));
        using var image = new MemoryStream();
        encoder.Save(image);
        string zip = Path.Combine(Folder, "mixed.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var entry in new[] { ("a.pdf", pdf), ("b.png", image.ToArray()), ("c.pdf", protectedPdf) })
            {
                using var stream = archive.CreateEntry(entry.Item1).Open();
                stream.Write(entry.Item2);
            }
        byte[] archiveBefore = File.ReadAllBytes(zip);
        bool separate = false, canceledPassword = false;
        int closedImports = 0;
        var discarded = new HashSet<Window>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window =>
                !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            if (!separate)
            {
                Button? choose = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Text(button) == (string)Owner.FindResource("Str_Drop_Separate"));
                if (choose is null) return;
                separate = true;
                QueueClick(choose);
            }
            else if (!canceledPassword && Descendants<PasswordBox>(dialog).Any())
            {
                var cancel = Descendants<Button>(dialog).Single(button => Text(button).Trim() == "Cancel");
                canceledPassword = true;
                QueueClick(cancel);
            }
            else
            {
                Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Text(button) == (string)Owner.FindResource("Str_Btn_DontSave"));
                if (discard is null || !discarded.Add(dialog)) return;
                dialog.Closed += (_, _) => closedImports++;
                QueueClick(discard);
            }
        };
        timer.Start();
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        RaiseOwnedDrop(viewer, [zip]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!canceledPassword && watch.Elapsed < TimeSpan.FromSeconds(12)) Pump(80);
        Pump(300);
        bool blueImage = Descendants<Image>(viewer).Where(control => control.IsVisible)
            .Select(control => control.Source).OfType<BitmapSource>().Any(bitmap =>
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                byte[] center = new byte[4];
                converted.CopyPixels(new Int32Rect(converted.PixelWidth / 2, converted.PixelHeight / 2, 1, 1), center, 4, 0);
                return center[0] > 200 && center[1] < 30 && center[2] < 30 && center[3] == 255;
            });
        if (!separate || !canceledPassword || !blueImage)
            throw new InvalidOperationException($"Mixed Separate import did not keep the image after the protected open was canceled. Separate={separate}, canceledPassword={canceledPassword}, label={((TextBlock)Owner.FindName("FileNameLabel")).Text}. "
                + string.Join(" | ", App.Windows.Cast<Window>().Where(window => window.IsVisible && !ReferenceEquals(window, Owner))
                    .SelectMany(window => Descendants<TextBlock>(window)).Select(text => text.Text)));
        string directory = Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Single();
        string[] retained = Directory.GetFiles(directory);
        if (retained.Length != 1 || !pdf.SequenceEqual(File.ReadAllBytes(retained[0])))
            throw new InvalidOperationException("Mixed import retained a canceled PDF or converted image, or removed the adopted PDF.");
        Click((Button)Owner.FindName("CloseFileBtn"));
        Pump(300);
        if (!Descendants<TextBox>(viewer).Any(input => input.Text == "MIXED"))
            throw new InvalidOperationException("Closing the image lost the remaining imported PDF.");
        Descendants<TextBox>(viewer).Single(input => input.Text == "MIXED").Text = "MIXED-EDIT";
        Owner.Focus();
        Click((Button)Owner.FindName("CloseFileBtn"));
        Pump(300);
        timer.Stop();
        if (closedImports != 2) throw new InvalidOperationException("The two unsaved imported documents did not both offer to preserve their work. Closed prompts: " + closedImports);
        if (!archiveBefore.SequenceEqual(File.ReadAllBytes(zip))) throw new InvalidOperationException("Mixed import changed the archive.");
        bool exited = false;
        App.Exit += (_, _) => exited = true;
        App.Shutdown();
        var shutdown = System.Diagnostics.Stopwatch.StartNew();
        while (!exited && shutdown.Elapsed < TimeSpan.FromSeconds(3)) Pump(80);
        if (!exited) throw new InvalidOperationException("The owned application did not finish shutting down.");
        if (Directory.Exists(directory)) throw new InvalidOperationException("Process shutdown retained disposable import bytes.");
    }
}
