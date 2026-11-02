using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;

internal static partial class Program
{
    private static void ArchiveClosedWindow(bool releaseAtPrompt = false)
    {
        Window importing = Owner;
        Owner = (Window)Activator.CreateInstance(importing.GetType())!;
        Owner.WindowStartupLocation = WindowStartupLocation.Manual;
        Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
        Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
        Owner.Show(); Pump(300);
        Window survivor = Owner;
        string source = Path.Combine(Folder, "survivor.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "keep", 20, 200, 150, 25, "SURVIVOR").Build();
        File.WriteAllBytes(source, original);
        Open(source);
        var survivorViewer = (FrameworkElement)survivor.FindName("Viewer");
        Descendants<TextBox>(survivorViewer).Single(input => input.Text == "SURVIVOR").Text = "SURVIVOR-EDIT";
        Owner = importing;
        var pending = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "", OwnerPassword = "owner", AllowDocumentModification = true });
        for (int page = 0; page < 5000; page++) pending.AddPage(300, 500,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(20, 450).ShowLatin1Text("Pending " + page + new string('x', 1000)).EndText());
        byte[] password = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" }).Build();
        byte[] pendingBytes = pending.Build();
        string zip = Path.Combine(Folder, "pending.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry("00-pending.pdf").Open()) stream.Write(pendingBytes);
            using (var stream = archive.CreateEntry("01-password.pdf").Open()) stream.Write(password);
        }
        byte[] archiveBefore = File.ReadAllBytes(zip);
        bool closed = false;
        if (!releaseAtPrompt) importing.Closed += (_, _) => closed = true;
        bool canceled = false;
        var status = (TextBlock)importing.FindName("StatusText");
        var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler observe = (_, _) => canceled |= status.Text == (string)importing.FindResource("Str_St_Canceled");
        property.AddValueChanged(status, observe);
        var nextPrompt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        nextPrompt.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, importing) && !ReferenceEquals(window, survivor) && window.IsVisible);
            if (dialog is null || !Descendants<PasswordBox>(dialog).Any()) return;
            nextPrompt.Stop();
            closed = true;
            string directory = Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Single();
            byte[][] inputs = Directory.GetFiles(directory).Select(File.ReadAllBytes).ToArray();
            File.WriteAllText(Path.Combine(Folder, "next-prompt-inputs.txt"), $"canceled={canceled}; firstPresent={inputs.Any(bytes => bytes.SequenceEqual(pendingBytes))}; nextPresent={inputs.Any(bytes => bytes.SequenceEqual(password))}");
            if (!canceled || inputs.Any(bytes => bytes.SequenceEqual(pendingBytes)) || !inputs.Any(bytes => bytes.SequenceEqual(password)))
                Failure = new InvalidOperationException("An abandoned first archive member was retained while the next member waited at its password prompt, or its pending sibling was removed.");
            QueueClick(Descendants<Button>(dialog).First(button => Text(button).Trim() == "Cancel"));
        };
        var choose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        choose.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, importing) && !ReferenceEquals(window, survivor) && window.IsVisible);
            Button? separate = dialog is null ? null : Descendants<Button>(dialog).FirstOrDefault(button => Text(button) == (string)importing.FindResource("Str_Drop_Separate"));
            if (separate is null) return;
            choose.Stop();
            dialog!.Closed += (_, _) => importing.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (!Descendants<TextBlock>(importing).Any(text => text.IsVisible && text.Text == (string)importing.FindResource("Str_Busy_OpeningProtected")))
                { Failure = new InvalidOperationException("The first archive member was not pending when its window closed."); return; }
                if (releaseAtPrompt)
                {
                    importing.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        Window confirmation = App.Windows.Cast<Window>().Last(window => !ReferenceEquals(window, importing) && !ReferenceEquals(window, survivor) && window.IsVisible);
                        Click(Descendants<Button>(confirmation).First(button => Text(button).Trim() == "Yes"));
                    }));
                    importing.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(importing), Environment.TickCount, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    return;
                }
                importing.Close();
            }));
            QueueClick(separate);
        };
        choose.Start();
        if (releaseAtPrompt) nextPrompt.Start();
        RaiseOwnedDrop((FrameworkElement)importing.FindName("Viewer"), [zip]);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Failure is null && (!closed || Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any()) && wait.Elapsed < TimeSpan.FromSeconds(15)) Pump(80);
        choose.Stop(); nextPrompt.Stop(); property.RemoveValueChanged(status, observe);
        if (Failure is not null) throw Failure;
        if (!closed || Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Closing the importing window did not finish and release its pending archive batch.");
        if (!survivor.IsLoaded || !Descendants<TextBox>(survivorViewer).Any(input => input.Text == "SURVIVOR-EDIT")
            || !original.SequenceEqual(File.ReadAllBytes(source)) || !archiveBefore.SequenceEqual(File.ReadAllBytes(zip)))
            throw new InvalidOperationException("Closing the importing window changed the surviving document or an original input.");
        Owner = survivor;
        bool approved = false;
        var save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        save.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, importing) && !ReferenceEquals(window, survivor) && window.IsVisible);
            if (dialog is null || !Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("survivor.pdf", StringComparison.OrdinalIgnoreCase))) return;
            Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Text(button).Trim() == "Yes");
            if (yes is null) return;
            approved = true; save.Stop(); QueueClick(yes);
        };
        save.Start(); Click((Button)survivor.FindName("SaveAsBtn")); Pump(300); save.Stop();
        if (!approved || PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(source)), 0)
            .Single(widget => widget.FieldName == "keep").Value != "SURVIVOR-EDIT")
            throw new InvalidOperationException("The surviving window did not save its prior form edit.");
        Owner = (Window)Activator.CreateInstance(importing.GetType())!;
        Owner.WindowStartupLocation = WindowStartupLocation.Manual;
        Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
        Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
        Owner.Show(); Pump(300); Open(source);
        if (!Descendants<TextBox>((FrameworkElement)Owner.FindName("Viewer")).Any(input => input.Text == "SURVIVOR-EDIT"))
            throw new InvalidOperationException("The surviving edit did not reopen in a fresh window.");
    }
}
