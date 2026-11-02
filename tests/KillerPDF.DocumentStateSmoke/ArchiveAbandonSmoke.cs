using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;

internal static partial class Program
{
    private static void ArchiveAbandon(string kind)
    {
        string previous = Path.Combine(Folder, "previous.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "keep", 20, 200, 150, 25, "KEEP").Build();
        File.WriteAllBytes(previous, original);
        Open(previous);
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        Descendants<TextBox>(viewer).Single(input => input.Text == "KEEP").Text = "KEEP-EDIT";
        string zip = Path.Combine(Folder, "abandoned.zip");
        int count = kind == "cap" ? 51 : kind == "tabs" ? 31 : 2;
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            for (int index = 0; index < count; index++)
            {
                string name = kind == "unsafe" && index == 1 ? "../outside.pdf" : index.ToString("D2") + ".pdf";
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(original);
            }
        byte[] archiveBefore = File.ReadAllBytes(zip);
        bool handled = false, separate = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window =>
                !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            if (kind == "tabs" && !separate)
            {
                Button? choose = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Text(button) == (string)Owner.FindResource("Str_Drop_Separate"));
                if (choose is null) return;
                separate = true;
                QueueClick(choose);
                return;
            }
            string expected = kind switch
            {
                "cap" => string.Format((string)Owner.FindResource("Str_Drop_TooMany"), count, 50),
                "tabs" => string.Format((string)Owner.FindResource("Str_Drop_ManyTabs"), count),
                _ => (string)Owner.FindResource("Str_Drop_Unsafe")
            };
            if (!Descendants<TextBlock>(dialog).Any(text => text.Text == expected)) return;
            Button? dismiss = Descendants<Button>(dialog).FirstOrDefault(button =>
                Text(button).Trim() == (kind == "unsafe" ? "OK" : "Cancel"));
            if (dismiss is null) return;
            handled = true;
            timer.Stop();
            QueueClick(dismiss);
        };
        timer.Start();
        RaiseOwnedDrop(viewer, [zip]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!handled && watch.Elapsed < TimeSpan.FromSeconds(12)) Pump(80);
        timer.Stop();
        Pump(400);
        if (!handled) throw new InvalidOperationException("The expected " + kind + " import refusal prompt did not appear.");
        if (((TextBlock)Owner.FindName("FileNameLabel")).Text != "previous.pdf"
            || !Descendants<TextBox>(viewer).Any(input => input.Text == "KEEP-EDIT"))
            throw new InvalidOperationException("Abandoned archive import changed the current document.");
        if (Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Abandoned archive import retained extracted bytes.");
        if (!original.SequenceEqual(File.ReadAllBytes(previous)) || !archiveBefore.SequenceEqual(File.ReadAllBytes(zip)))
            throw new InvalidOperationException("Abandoned archive import changed source bytes.");
    }
}
