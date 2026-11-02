using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

internal static partial class Program
{
    private static void ArchiveFirstSave(bool closeUntouched = false, bool responsive = false, bool durableSave = false)
    {
        string source = Path.Combine(Folder, "archived.pdf");
        File.WriteAllBytes(source, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "customer", 20, 200, 150, 25, "ARCHIVE").Build());
        string zip = Path.Combine(Folder, "documents.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(source, "archived.pdf");
        byte[] originalArchive = File.ReadAllBytes(zip);
        bool separateChosen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            Window? prompt = App.Windows.Cast<Window>().LastOrDefault(window =>
                !ReferenceEquals(window, Owner) && window.IsVisible);
            if (prompt is null) return;
            Button? separate = Descendants<Button>(prompt).FirstOrDefault(button =>
                Text(button) == (string)Owner.FindResource("Str_Drop_Separate"));
            if (separate is null) return;
            separateChosen = true;
            timer.Stop();
            Click(separate);
        };
        timer.Start();
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        ScrollViewer target = Descendants<ScrollViewer>(viewer).First(control => control.AllowDrop);
        var data = new DataObject(DataFormats.FileDrop, new[] { zip });
        ConstructorInfo constructor = typeof(DragEventArgs).GetConstructors(Members)
            .Single(item => item.GetParameters().Length == 5);
        object Argument(ParameterInfo parameter)
        {
            if (typeof(IDataObject).IsAssignableFrom(parameter.ParameterType)) return data;
            if (parameter.ParameterType == typeof(int)) return 0;
            if (parameter.ParameterType == typeof(DragDropKeyStates)) return DragDropKeyStates.None;
            if (parameter.ParameterType == typeof(DependencyObject)) return target;
            if (parameter.ParameterType == typeof(Point)) return new Point(10, 10);
            if (parameter.ParameterType == typeof(DragDropEffects)) return DragDropEffects.Copy;
            throw new InvalidOperationException("Unsupported drop argument: " + parameter.ParameterType);
        }
        var drop = (DragEventArgs)constructor.Invoke(constructor.GetParameters().Select(Argument).ToArray());
        drop.RoutedEvent = DragDrop.DropEvent;
        bool pendingInputRan = false;
        if (responsive)
        {
            var before = Directory.EnumerateFiles(Folder, "*.pdf", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                pendingInputRan = true;
                if (Directory.EnumerateFiles(Folder, "*.pdf", SearchOption.AllDirectories)
                    .Any(path => !before.Contains(path)))
                    Failure = new InvalidOperationException("Archive extraction ran before the window could process pending input.");
            }));
        }
        target.RaiseEvent(drop);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TextBox? field = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump(80);
            field = Descendants<TextBox>(viewer).FirstOrDefault(input => input.Text == "ARCHIVE");
            if (field is not null) break;
        }
        timer.Stop();
        if (responsive && (!pendingInputRan || Failure is not null))
            throw Failure ?? new InvalidOperationException("The pending input callback never ran.");
        if (!separateChosen || field is null) throw new InvalidOperationException("The actual ZIP Separate flow did not open the form.");
        if (!closeUntouched) field.Text = "ARCHIVE-EDIT";
        Owner.Focus();
        Pump(100);
        bool saveAsShown = false;
        bool closePromptShown = false;
        string? unexpectedPrompt = null;
        string durable = Path.Combine(Folder, "durable.pdf");
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            Window? picker = App.Windows.Cast<Window>().LastOrDefault(window =>
                window.IsVisible && !ReferenceEquals(window, Owner));
            if (picker is null) return;
            if (closeUntouched)
            {
                Button? cancel = Descendants<Button>(picker).FirstOrDefault(button => Text(button).Trim() == "Cancel");
                if (cancel is null) return;
                closePromptShown = true;
                timer.Stop();
                Click(cancel);
                return;
            }
            saveAsShown = picker.GetType().Name == "FileDialog";
            if (!saveAsShown) unexpectedPrompt = picker.Title + ": "
                + string.Join(" | ", Descendants<TextBlock>(picker).Select(text => text.Text));
            timer.Stop();
            if (saveAsShown && durableSave)
            {
                ((TextBox)picker.FindName("FileNameBox")).Text = durable;
                Click((Button)picker.FindName("AcceptButton"));
            }
            else picker.Close();
        };
        timer.Start();
        if (closeUntouched) Owner.Close();
        else Click((Button)Owner.FindName("SaveAsBtn"));
        Pump(400);
        timer.Stop();
        if (closeUntouched)
        {
            if (!closePromptShown || !Owner.IsVisible)
                throw new InvalidOperationException("Closing an unsaved archived PDF did not offer to preserve it.");
        }
        else if (!saveAsShown) throw new InvalidOperationException("Save did not ask for a durable destination for the archived PDF. " + unexpectedPrompt);
        if (!originalArchive.SequenceEqual(File.ReadAllBytes(zip))) throw new InvalidOperationException("Saving an import changed its source archive.");
        if (!Descendants<TextBox>(viewer).Any(input => input.Text == (closeUntouched ? "ARCHIVE" : "ARCHIVE-EDIT")))
            throw new InvalidOperationException("Canceling Save As lost the imported document's pending form edit.");
        if (durableSave)
        {
            var saved = PdfDocument.Open(File.ReadAllBytes(durable));
            if (PdfPageInformation.Read(saved).Count != 1 || PdfFormWidgetReader.ReadPage(saved, 0)
                .Single(widget => widget.FieldName == "customer").Value != "ARCHIVE-EDIT")
                throw new InvalidOperationException("Durable Save As did not preserve the imported form edit.");
            var importedWindow = Owner;
            Owner = (Window)Activator.CreateInstance(importedWindow.GetType())!;
            Owner.WindowStartupLocation = WindowStartupLocation.Manual;
            Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
            Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
            Owner.Show();
            Pump(300);
            Open(durable);
            Pump(400);
            if (!Descendants<TextBox>((FrameworkElement)Owner.FindName("Viewer")).Any(input => input.Text == "ARCHIVE-EDIT"))
                throw new InvalidOperationException("The durable archive save did not reopen with its form value.");
        }
    }
}
