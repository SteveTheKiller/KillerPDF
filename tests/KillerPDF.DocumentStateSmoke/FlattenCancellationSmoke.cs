using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

internal static partial class Program
{
    private static void FlattenStagedCancellation(bool existing = false, bool cancel = true,
        bool recoveryFailure = false, bool unexpectedCancellation = false)
    {
        string source = Path.Combine(Folder, "flatten-source.pdf");
        byte[] sourceBytes = new PdfDocumentBuilder().AddBlankPage(300, 500).Build();
        File.WriteAllBytes(source, sourceBytes);
        Open(source);
        string outputDirectory = Path.Combine(Folder, "output");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "flattened.pdf");
        byte[] sentinel = "Independent original output"u8.ToArray();
        if (existing) File.WriteAllBytes(output, sentinel);
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? stagingPath = null;
        string? recoveryPath = null;
        long stagedLength = 0;
        bool barrierReleased = false, cancellationObserved = false, finished = false;
        Exception? callbackFailure = null;
        var dialogs = new List<string>();
        var seen = new HashSet<Window>();
        int dialogStage = 0;
        Type atomic = Owner.GetType().Assembly.GetType("KillerPDF.Services.AtomicFileOutput", true)!;
        Type operationsType = atomic.GetNestedType("FileOperations", BindingFlags.NonPublic)!;
        object operations = Activator.CreateInstance(operationsType, nonPublic: true)!;
        operationsType.GetField("FlushToDisk", Members)!.SetValue(operations, (Action<FileStream>)(stream =>
        {
            stream.Flush(flushToDisk: true);
            if (!string.Equals(Path.GetDirectoryName(stream.Name), outputDirectory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(stream.Name).StartsWith(".flattened.pdf.", StringComparison.Ordinal)
                || !stream.Name.EndsWith(".tmp", StringComparison.Ordinal)) return;
            stagingPath = stream.Name;
            stagedLength = stream.Length;
            if (recoveryFailure) return;
            staged.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Final staging barrier was not released.");
            if (unexpectedCancellation) throw new OperationCanceledException(new CancellationToken(canceled: true));
        }));
        if (recoveryFailure)
        {
            operationsType.GetField("Replace", Members)!.SetValue(operations, (Action<string, string, string?>)((incoming, target, backup) =>
            {
                if (!string.Equals(target, output, StringComparison.OrdinalIgnoreCase))
                {
                    File.Replace(incoming, target, backup, ignoreMetadataErrors: true);
                    return;
                }
                File.Move(target, backup!);
                recoveryPath = backup;
                staged.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Partial replacement barrier was not released.");
                throw new IOException("Controlled partial replacement failure.");
            }));
            operationsType.GetField("Move", Members)!.SetValue(operations, (Action<string, string>)((incoming, target) =>
            {
                if (string.Equals(target, output, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Controlled recovery publication failure.");
                File.Move(incoming, target);
            }));
        }
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in App.Windows.Cast<Window>().Where(window => window != Owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    string type = dialog.GetType().Name;
                    Console.WriteLine("Flatten dialog: " + type + ", stage=" + dialogStage);
                    if (dialogStage == 0 && type == "FileDialog")
                    {
                        dialogStage = 1;
                        ((TextBox)dialog.FindName("FileNameBox")).Text = output;
                        QueueClick((Button)dialog.FindName("AcceptButton"));
                    }
                    else if (dialogStage == 1 && type == "FlattenOptionsDialog")
                    {
                        dialogStage = 2;
                        QueueClick(Descendants<Button>(dialog).Single(button => button.IsDefault));
                    }
                    else if (existing && dialogStage == 1 && type != "FlattenOptionsDialog")
                    {
                        Button? replace = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, Owner.FindResource("Str_Btn_Replace")));
                        if (replace is null) throw new InvalidOperationException("Unexpected overwrite confirmation: " + type);
                        QueueClick(replace);
                    }
                    else
                    {
                        dialogs.Add(type + ": " + string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text)));
                        QueueClick(Descendants<Button>(dialog).First(button => button.IsDefault));
                    }
                }
                if (!barrierReleased && staged.IsSet)
                {
                    if (stagedLength <= 0 || stagingPath is null || !File.Exists(stagingPath))
                        throw new InvalidOperationException("The real final staged output is missing or empty.");
                    bool originalPreserved = recoveryFailure
                        ? !File.Exists(output) && recoveryPath is not null && File.ReadAllBytes(recoveryPath).AsSpan().SequenceEqual(sentinel)
                        : File.Exists(output) == existing && (!existing || File.ReadAllBytes(output).AsSpan().SequenceEqual(sentinel));
                    if (!originalPreserved)
                        throw new InvalidOperationException("Destination changed before staged cancellation.");
                    Border overlay = Descendants<Border>(Owner).Single(border => Equals(border.Tag, "FlattenOverlay"));
                    if (cancel)
                    {
                        Button cancelButton = Descendants<Button>(overlay).Single(button => Equals(button.Content, Owner.FindResource("Str_Btn_Cancel")));
                        Click(cancelButton);
                        cancellationObserved = (Owner.GetType().GetField("_busyCts", Members)!.GetValue(Owner) as CancellationTokenSource)
                            ?.IsCancellationRequested == true;
                        if (!cancellationObserved) throw new InvalidOperationException("Actual Cancel did not cancel the active operation before release.");
                    }
                    barrierReleased = true;
                    release.Set();
                }
                if (barrierReleased && Owner.GetType().GetField("_busyCts", Members)!.GetValue(Owner) is null
                    && !Descendants<Border>(Owner).Any(border => Equals(border.Tag, "FlattenOverlay"))) finished = true;
            }
            catch (Exception error)
            {
                callbackFailure = error; release.Set(); timer.Stop();
                foreach (Window dialog in App.Windows.Cast<Window>().Where(window => window != Owner && window.IsVisible).ToArray())
                    dialog.Dispatcher.BeginInvoke(new Action(dialog.Close));
            }
        };
        // Install the filesystem boundary in the same execution context as the actual command.
        using var scope = (IDisposable)atomic.GetMethod("UseFileOperations", Members)!.Invoke(null, [operations])!;
        timer.Start();
        try
        {
            Click((Button)Owner.FindName("FlattenBtn"));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!finished && callbackFailure is null && deadline.Elapsed < TimeSpan.FromSeconds(25)) Pump(20);
            if (callbackFailure is not null) throw new InvalidOperationException("Flatten acceptance callback failed.", callbackFailure);
            if (!finished) throw new TimeoutException("Flatten did not finish after final staging.");
            string status = ((TextBlock)Owner.FindName("StatusText")).Text;
            File.WriteAllText(Path.Combine(Folder, "flatten-publication.json"), JsonSerializer.Serialize(new
            {
                existing, cancel, recoveryFailure, unexpectedCancellation, stagingPath, recoveryPath, stagedLength,
                barrierReleased, cancellationObserved, status, dialogs,
                OutputExists = File.Exists(output), SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(sourceBytes)) throw new InvalidOperationException("Flatten changed its source.");
            if (recoveryFailure)
            {
                string messages = string.Join(" | ", dialogs);
                if (dialogs.Count != 1 || !messages.Contains("Recovery files were kept.", StringComparison.Ordinal)
                    || stagingPath is null || recoveryPath is null
                    || !messages.Contains(stagingPath, StringComparison.Ordinal) || !messages.Contains(recoveryPath, StringComparison.Ordinal)
                    || !File.Exists(stagingPath) || !File.Exists(recoveryPath)
                    || !File.ReadAllBytes(recoveryPath).AsSpan().SequenceEqual(sentinel)
                    || status == (string)Owner.FindResource("Str_St_FlattenCanceled"))
                    throw new InvalidOperationException("Recovery-bearing cancellation was hidden or lost its retained files: " + messages);
                return;
            }
            if (stagingPath is null || File.Exists(stagingPath) || Directory.GetFiles(outputDirectory).Any(path => path != output))
                throw new InvalidOperationException("Flatten left staging debris.");
            if (unexpectedCancellation)
            {
                if (File.Exists(output) || dialogs.Count != 1
                    || !dialogs[0].Contains("OperationCanceledException", StringComparison.Ordinal)
                    || status == (string)Owner.FindResource("Str_St_FlattenCanceled"))
                    throw new InvalidOperationException("Unexpected cancellation was hidden as a requested cancellation.");
                return;
            }
            if (cancel)
            {
                if (File.Exists(output) != existing || existing && !File.ReadAllBytes(output).AsSpan().SequenceEqual(sentinel))
                    throw new InvalidOperationException("Canceled flatten published or damaged its destination.");
                if (dialogs.Count != 0 || status != (string)Owner.FindResource("Str_St_FlattenCanceled"))
                    throw new InvalidOperationException("Canceled flatten did not report cancellation consistently: " + status + "; " + string.Join(" | ", dialogs));
            }
            else
            {
                if (dialogs.Count != 0 || !File.Exists(output)
                    || PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output))).Count != 1)
                    throw new InvalidOperationException("Uncanceled flatten did not publish a readable one-page output.");
                if (status != string.Format((string)Owner.FindResource("Str_St_FlattenedSaved"), Path.GetFileName(output)))
                    throw new InvalidOperationException("Successful flatten did not report the saved output.");
            }
        }
        finally { release.Set(); timer.Stop(); }
    }
}
