using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;

internal static partial class Program
{
    private static void MergeCancellation()
    {
        string previous = Path.Combine(Folder, "previous.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "keep", 20, 200, 150, 25, "KEEP").Build();
        File.WriteAllBytes(previous, original);
        Open(previous);
        var viewer = (FrameworkElement)Owner.FindName("Viewer");
        Descendants<TextBox>(viewer).Single(input => input.Text == "KEEP").Text = "KEEP-EDIT";
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 1000; page++) builder.AddBlankPage(300, 500);
        byte[] inputBytes = builder.Build();
        string archivePath = Path.Combine(Folder, "merge.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            for (int input = 0; input < 4; input++)
            {
                using var stream = archive.CreateEntry(input + ".pdf", CompressionLevel.Fastest).Open();
                stream.Write(inputBytes);
            }
        byte[] archiveBefore = File.ReadAllBytes(archivePath);
        bool requested = false, confirmed = false;
        var confirmation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        confirmation.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window =>
                !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Text(button).Trim() == "Yes");
            if (yes is null) return;
            confirmed = true;
            confirmation.Stop();
            Click(yes);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(window =>
                !ReferenceEquals(window, Owner) && window.IsVisible);
            if (dialog is null) return;
            if (!requested)
            {
                Button? merge = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Text(button) == (string)Owner.FindResource("Str_Drop_Merge"));
                if (merge is null) return;
                dialog.Closed += (_, _) => Owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    requested = Descendants<TextBlock>(Owner).Any(text => text.IsVisible
                        && text.Text == (string)Owner.FindResource("Str_Drop_Merging"));
                    if (!requested) { Failure = new InvalidOperationException("Merge was not pending when cancellation was requested."); return; }
                    confirmation.Start();
                    Owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Owner),
                        Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }));
                timer.Stop();
                Click(merge);
                return;
            }
        };
        bool canceled = false;
        var status = (TextBlock)Owner.FindName("StatusText");
        var statusProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty, typeof(TextBlock));
        EventHandler observeStatus = (_, _) => canceled |= status.Text == (string)Owner.FindResource("Str_Drop_MergeCanceled");
        statusProperty.AddValueChanged(status, observeStatus);
        timer.Start();
        RaiseOwnedDrop(viewer, [archivePath]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && Failure is null)
        {
            Pump(80);
            if (canceled) break;
        }
        timer.Stop();
        confirmation.Stop();
        statusProperty.RemoveValueChanged(status, observeStatus);
        if (Failure is not null) throw Failure;
        if (!requested || !confirmed || !canceled) throw new InvalidOperationException(
            $"The actual merge cancellation did not complete: requested={requested}, confirmed={confirmed}, canceled={canceled}. "
            + string.Join(" | ", Descendants<TextBlock>(Owner).Where(text => text.IsVisible).Select(text => text.Text)));
        if (((TextBlock)Owner.FindName("FileNameLabel")).Text != "previous.pdf"
            || !Descendants<TextBox>(viewer).Any(input => input.Text == "KEEP-EDIT"))
            throw new InvalidOperationException("Canceled merge replaced or lost the previous document's pending edits.");
        if (Directory.EnumerateDirectories(Folder, "import-*", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("Canceled merge retained its extracted input directory.");
        if (!original.SequenceEqual(File.ReadAllBytes(previous)) || !archiveBefore.SequenceEqual(File.ReadAllBytes(archivePath)))
            throw new InvalidOperationException("Canceling merge changed a source file.");
    }

    private static void RaiseOwnedDrop(FrameworkElement viewer, string[] paths)
    {
        ScrollViewer target = Descendants<ScrollViewer>(viewer).First(control => control.AllowDrop);
        var data = new DataObject(DataFormats.FileDrop, paths);
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
        target.RaiseEvent(drop);
    }
}
