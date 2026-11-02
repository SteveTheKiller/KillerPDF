using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;

internal static partial class Program
{
    private static void ArchiveAsyncCancel(bool empty = false, bool closeOwner = false, bool cleanup = false, bool workingCopyFailure = false, bool noPages = false, bool repair = false, bool closePrevious = false)
    {
        if (workingCopyFailure && Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("The native working-copy fault belongs on a leased SN VM.");
        string previous = Path.Combine(Folder, "previous.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "keep", 20, 200, 150, 25, "KEEP").Build();
        File.WriteAllBytes(previous, original);
        string savePath = closePrevious ? Path.Combine(Folder, "survivor.pdf") : previous;
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        if (closePrevious)
        {
            File.WriteAllBytes(savePath, original);
            Open(savePath);
            Descendants<TextBox>(viewer).Single(input => input.Text == "KEEP").Text = "KEEP-EDIT";
        }
        if (!empty)
        {
            Open(previous);
            Descendants<TextBox>(viewer).Single(input => input.Text == "KEEP").Text = "KEEP-EDIT";
        }
        var builder = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "", OwnerPassword = "owner", AllowDocumentModification = !repair });
        for (int page = 0; page < (noPages ? 0 : 5000); page++) builder.AddPage(300, 500,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(20, 450).ShowLatin1Text("Protected import " + page + new string('x', 1000)).EndText());
        byte[] encrypted = builder.Build();
        string zip = Path.Combine(Folder, "async.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("protected.pdf").Open()) stream.Write(encrypted);
        byte[] archiveBefore = File.ReadAllBytes(zip);
        bool requested = false, confirmed = false, canceled = false;
        DirectoryInfo? deniedDirectory = null;
        string? originalAccess = null;
        void RestoreAccess()
        {
            if (deniedDirectory is null || originalAccess is null) return;
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(originalAccess, AccessControlSections.Access);
            deniedDirectory.SetAccessControl(restored);
            string control = Path.Combine(deniedDirectory.FullName, "access-restored.txt");
            File.WriteAllText(control, "restored");
            File.Delete(control);
        }
        var error = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        error.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            string message = (string)Owner.FindResource(repair ? "Str_Repair_Unrecoverable" : noPages ? "Str_Err_OpenProtected" : "Str_Err_WorkingCopy");
            if (dialog is null || !Descendants<TextBlock>(dialog).Any(text => text.Text.Contains(message, StringComparison.Ordinal))) return;
            error.Stop();
            RestoreAccess();
            confirmed = true;
            QueueClick(Descendants<Button>(dialog).First(button => Text(button).Trim() == "OK"));
        };
        var status = (TextBlock)Owner.FindName("StatusText");
        var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler observe = (_, _) => canceled |= status.Text == (string)Owner.FindResource("Str_St_Canceled");
        property.AddValueChanged(status, observe);
        void ConfirmCancellation()
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            Button? yes = dialog is null ? null : Descendants<Button>(dialog).FirstOrDefault(button => Text(button).Trim() == "Yes");
            if (yes is null) { Failure = new InvalidOperationException("The actual protected-open cancellation prompt did not appear."); return; }
            string directory = Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Single();
            if (!encrypted.SequenceEqual(File.ReadAllBytes(Directory.GetFiles(directory).Single())))
                Failure = new InvalidOperationException("The pending protected open lost its extracted source.");
            confirmed = true;
            Click(yes);
        }
        var choose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        choose.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            Button? separate = Descendants<Button>(dialog).FirstOrDefault(button => Text(button) == (string)Owner.FindResource("Str_Drop_Separate"));
            if (separate is null) return;
            choose.Stop();
            dialog.Closed += (_, _) => Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                requested = Descendants<TextBlock>(Owner).Any(text => text.IsVisible
                    && text.Text == (string)Owner.FindResource("Str_Busy_OpeningProtected"));
                if (!requested) { Failure = new InvalidOperationException("The protected open was not pending at the cancellation boundary."); return; }
                if (noPages) { error.Start(); return; }
                if (workingCopyFailure)
                {
                    string directory = Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Single();
                    deniedDirectory = Directory.GetParent(directory)!;
                    originalAccess = deniedDirectory.GetAccessControl(AccessControlSections.Access)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                    DirectorySecurity denied = deniedDirectory.GetAccessControl(AccessControlSections.Access);
                    denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
                    deniedDirectory.SetAccessControl(denied);
                    error.Start();
                    return;
                }
                if (closeOwner)
                {
                    var close = (Button)Owner.FindName("CloseFileBtn");
                    if (!close.IsEnabled) { Failure = new InvalidOperationException("The loading tab's Close control was unavailable."); return; }
                    Click(close);
                }
                if (closePrevious)
                {
                    TextBlock label = Descendants<TextBlock>(viewer).Single(text => text.Name == "tabLbl" && text.Text.Contains("previous", StringComparison.OrdinalIgnoreCase));
                    var contents = (DockPanel)System.Windows.Media.VisualTreeHelper.GetParent(label);
                    Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        Window dialog = App.Windows.Cast<Window>().Last(window => !ReferenceEquals(window, Owner) && window.IsVisible);
                        Click(Descendants<Button>(dialog).Single(button => Text(button) == (string)Owner.FindResource("Str_Btn_DontSave")));
                    }));
                    Click(contents.Children.OfType<Button>().Single());
                }
                // Confirm before a ready worker continuation can adopt its result inside the
                // modal dispatcher frame. A timed click can arrive after the open has finished.
                Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ConfirmCancellation));
                Owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Owner), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }));
            QueueClick(separate);
        };
        var wait = System.Diagnostics.Stopwatch.StartNew();
        bool Finished() => closeOwner || workingCopyFailure || noPages
            ? confirmed && !Descendants<TextBlock>(Owner).Any(text => text.IsVisible && text.Text == (string)Owner.FindResource("Str_Busy_OpeningProtected"))
            : canceled;
        try
        {
            choose.Start();
            RaiseOwnedDrop(viewer, [zip]);
            while (!Finished() && Failure is null && wait.Elapsed < TimeSpan.FromSeconds(15)) Pump(80);
        }
        finally
        {
            choose.Stop(); error.Stop(); property.RemoveValueChanged(status, observe);
            RestoreAccess();
        }
        if (Failure is not null) throw Failure;
        if (!requested || !confirmed || !Finished()) throw new InvalidOperationException(
            $"The protected archive cancellation did not finish. requested={requested}, confirmed={confirmed}, canceled={canceled}, status={status.Text}, label={((TextBlock)Owner.FindName("FileNameLabel")).Text}");
        var previousLabels = Descendants<TextBlock>(viewer).Where(text => text.Text.Contains("previous", StringComparison.OrdinalIgnoreCase)).ToArray();
        File.WriteAllText(Path.Combine(Folder, "cancel-state.txt"),
            $"requested={requested}; confirmed={confirmed}; canceled={canceled}; status={status.Text}; closeEnabled={((Button)Owner.FindName("CloseFileBtn")).IsEnabled}\n"
            + string.Join("\n", previousLabels.Select(text => $"text={text.Text}; visible={text.IsVisible}; name={text.Name}")));
        if (cleanup)
        {
            wait.Restart();
            while (Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any() && wait.Elapsed < TimeSpan.FromSeconds(3)) Pump(80);
            if (Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("The canceled protected import retained its abandoned extraction directory after the worker finished.");
        }
        if (empty)
        {
            if (((Button)Owner.FindName("CloseFileBtn")).IsEnabled)
                throw new InvalidOperationException("A canceled first open left a document active in the empty window.");
            Click((Button)Owner.FindName("NewBlankBtn"));
            Pump(300);
            bool saveAs = false;
            var picker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            picker.Tick += (_, _) =>
            {
                Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog" && window.IsVisible);
                if (dialog is null) return;
                saveAs = true; picker.Stop(); dialog.Close();
            };
            picker.Start();
            Click((Button)Owner.FindName("SaveAsBtn"));
            picker.Stop();
            if (!saveAs || !archiveBefore.SequenceEqual(File.ReadAllBytes(zip)) || !original.SequenceEqual(File.ReadAllBytes(previous)))
                throw new InvalidOperationException("The empty-window cancellation did not leave a usable New and Save As workflow with unchanged sources.");
            return;
        }
        wait.Restart();
        while (!Descendants<TextBox>(viewer).Any(input => input.Text == "KEEP-EDIT") && wait.Elapsed < TimeSpan.FromSeconds(5)) Pump(80);
        if (!Descendants<TextBox>(viewer).Any(input => input.Text == "KEEP-EDIT"))
            throw new InvalidOperationException("The canceled protected import lost the previous tab's edit.");
        bool savePrompt = false;
        var save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        save.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            savePrompt = Descendants<TextBlock>(dialog).Any(text => text.Text.Contains(Path.GetFileName(savePath), StringComparison.OrdinalIgnoreCase));
            save.Stop();
            Button? cancel = Descendants<Button>(dialog).FirstOrDefault(button => Text(button).Trim() == "Cancel");
            if (cancel is null) dialog.Close();
            else QueueClick(cancel);
        };
        save.Start();
        Owner.Focus();
        Click((Button)Owner.FindName("SaveAsBtn"));
        Pump(400);
        save.Stop();
        if (!savePrompt) throw new InvalidOperationException("Canceling the protected import left the displayed prior document unable to save its pending edit.");
        if (!archiveBefore.SequenceEqual(File.ReadAllBytes(zip)) || !original.SequenceEqual(File.ReadAllBytes(previous)) || !original.SequenceEqual(File.ReadAllBytes(savePath)))
            throw new InvalidOperationException("The canceled protected import changed a source file.");
        bool replaced = false;
        var replace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        replace.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window => !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            if (!Descendants<TextBlock>(dialog).Any(text => text.Text.Contains(Path.GetFileName(savePath), StringComparison.OrdinalIgnoreCase))) return;
            Button? accept = Descendants<Button>(dialog).FirstOrDefault(button => Text(button).Trim() == "Yes");
            if (accept is null) return;
            replaced = true;
            replace.Stop();
            QueueClick(accept);
        };
        replace.Start();
        Click((Button)Owner.FindName("SaveAsBtn"));
        Pump(400);
        replace.Stop();
        if (!replaced || PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(savePath)), 0)
            .Single(widget => widget.FieldName == "keep").Value != "KEEP-EDIT")
            throw new InvalidOperationException("The restored prior document did not save its pending form edit.");
        Owner = (Window)Activator.CreateInstance(Owner.GetType())!;
        Owner.WindowStartupLocation = WindowStartupLocation.Manual;
        Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
        Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
        Owner.Show();
        Pump(300);
        Open(savePath);
        if (!Descendants<TextBox>((FrameworkElement)Owner.FindName("Viewer")).Any(input => input.Text == "KEEP-EDIT"))
            throw new InvalidOperationException("The prior edit did not reopen in a fresh window after cancellation.");
    }
}
