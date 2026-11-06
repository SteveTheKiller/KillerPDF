using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;

internal static class ProofCopySmoke
{
    internal static int Run(Window owner, string appPath, string folder, bool saveAs = false)
    {
        if (Environment.MachineName is not ("SN01" or "SN02"))
            throw new InvalidOperationException("Proof copy acceptance requires an owned VM.");
        string engineHash = SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocument).Assembly, appPath);
        string codecHash = SelectedAssemblyIdentity.HashLoaded(Assembly.Load("CoreJ2K"), appPath);
        Directory.CreateDirectory(folder);
        string originalPath = Path.Combine(folder, "proof-source.pdf");
        string copyPath = Path.Combine(folder, "proof-copy.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(600, 750).Build();
        File.WriteAllBytes(originalPath, original);
        string stage = "open";
        bool cancel = false, expectPicker = false, pickerHandled = false;
        int assertions = 0;
        Exception? failure = null;
        var events = new List<object>();
        var modals = new List<string>();
        var deadline = Stopwatch.StartNew();
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(item => item != owner && item.IsVisible).ToArray())
            {
                if (handled.Contains(window)) continue;
                try
                {
                    if (!expectPicker || window.GetType().FullName != "KillerPDF.Controls.FileDialog")
                    {
                        modals.Add(window.GetType().FullName ?? window.Title);
                        throw new InvalidOperationException("Unexpected modal during " + stage);
                    }
                    var action = (Button)window.FindName(cancel ? "CaptionCloseButton" : "AcceptButton");
                    if (action is not { IsVisible: true, IsEnabled: true }) continue;
                    if (!cancel)
                    {
                        var name = (TextBox)window.FindName("FileNameBox");
                        if (!name.IsVisible || !name.IsEnabled) continue;
                        ((IValueProvider)new TextBoxAutomationPeer(name).GetPattern(PatternInterface.Value)!).SetValue(copyPath);
                        if (!action.IsEnabled) continue;
                    }
                    handled.Add(window);
                    pickerHandled = true;
                    expectPicker = false;
                    events.Add(new { stage, actualFilePicker = true, cancel });
                    var invoke = (IInvokeProvider)new ButtonAutomationPeer(action).GetPattern(PatternInterface.Invoke)!;
                    owner.Dispatcher.BeginInvoke(new Action(invoke.Invoke));
                }
                catch (Exception error) { failure ??= error; window.Close(); }
            }
        };
        try
        {
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            if (!owner.IsVisible) owner.Show();
            owner.Activate();
            timer.Start();
            Open(originalPath);
            var source = View();
            var requested = new PdfDocumentProofSettings(enabled: true,
                printerProfile: PdfColorProfile.Load(NativeReadingSmoke.ProofPrinterFixture()),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true },
                gamutWarnings: true);
            source.Document.Proof = requested;
            Guid sourceDocument = source.Document.Id, sourceView = source.Id;
            stage = "cancel-save-copy";
            SaveCopy(cancelPicker: true);
            Product(!File.Exists(copyPath) && View().Id == sourceView && View().Document.Id == sourceDocument
                && View().CaptureProof().Settings == requested, "Cancel must preserve the original proof owner and requested preferences.");
            stage = "save-copy";
            SaveCopy(cancelPicker: false);
            Wait(() => File.Exists(copyPath) && ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(copyPath)
                && ViewOrNull() is not null);
            var copied = View();
            if (saveAs)
            {
                stage = "save-as-reopen";
                Product(copied.CaptureProof().Settings == requested, "Save As must retain the current requested proof settings.");
                Guid savedOwner = copied.Document.Id;
                ShortcutHintsSmoke.Key(owner, 0x57, 0x11);
                Wait(() => ViewOrNull() is null);
                Open(copyPath);
                var restored = View().CaptureProof().Settings;
                Product(View().Document.Id != savedOwner, "Reopening the saved destination must create a new logical owner.");
                Product(restored.Enabled && restored.GamutWarnings
                    && restored.PrinterProfileSha256 == requested.PrinterProfileSha256
                    && !restored.Options.BlackPointCompensation && restored.Options.SimulatePaper && restored.Options.SimulateBlack,
                    "The Save As destination must restore its inherited proof request after actual close and reopen.");
                Product(File.ReadAllBytes(originalPath).SequenceEqual(original), "Save As and proof restoration must preserve the original file.");
                Product(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(copyPath))).Count == 1,
                    "The Save As destination must independently reopen as a valid one-page PDF.");
            }
            else
            {
                var actual = copied.CaptureProof().Settings;
                events.Add(new { stage, sourceDocument, sourceView, copyDocument = copied.Document.Id, copyView = copied.Id,
                    actual.Enabled, actual.GamutWarnings, profile = actual.PrinterProfile?.ContentSha256, actual.Options });
                Product(actual.Enabled && actual.GamutWarnings
                    && actual.PrinterProfile?.ContentSha256 == requested.PrinterProfile!.ContentSha256
                    && !actual.Options.BlackPointCompensation && actual.Options.SimulatePaper && actual.Options.SimulateBlack,
                    "Save a Copy must inherit the source's requested proof profile and options.");
                Product(copied.Document.Id != sourceDocument && copied.Id != sourceView && copied.ComparisonOverride is null,
                    "The copied file must have independent document and view identities.");
                stage = "independent-copy-settings";
                copied.Document.Proof = new PdfDocumentProofSettings();
                Product(source.CaptureProof().Settings == requested,
                    "Changing the copied document must leave the source proof settings unchanged.");
                Open(originalPath);
                Product(View().Id == sourceView && View().Document.Id == sourceDocument && View().CaptureProof().Settings == requested,
                    "Returning to the original tab must restore its original proof identity and preferences.");
                stage = "independent-output";
                var output = PdfDocument.Open(File.ReadAllBytes(copyPath));
                Product(PdfPageInformation.Read(output).Count == 1 && new PdfPageContentReader(output).Read(0).Letters.Count == 0,
                    "The saved copy must independently reopen as the same one-page text-free PDF.");
                Product(File.ReadAllBytes(originalPath).SequenceEqual(original), "Save a Copy must preserve the original file.");
            }
            stage = "complete";
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            expectPicker = false;
            try
            {
                if (owner.IsVisible) owner.Close();
                var closing = Stopwatch.StartNew();
                while (owner.IsVisible && closing.Elapsed < TimeSpan.FromSeconds(5))
                {
                    owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(25);
                }
                if (owner.IsVisible) throw new InvalidOperationException("Owned proof copy window did not close.");
            }
            catch (Exception error) { failure ??= error; }
            timer.Stop();
            bool preserved = File.ReadAllBytes(originalPath).SequenceEqual(original);
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null && modals.Count == 0 && preserved, stage, assertions, preserved,
                closed = !owner.IsVisible, error = failure?.ToString(), modals, events, saveAs, engineHash, codecHash,
                appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null && modals.Count == 0 ? 0 : 1;

        PdfDocumentProofView? ViewOrNull()
        {
            var viewer = (FrameworkElement)owner.GetType().GetProperty("ActiveViewer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
            return (PdfDocumentProofView?)viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer);
        }
        PdfDocumentProofView View() => ViewOrNull() ?? throw new InvalidOperationException("Readable document proof view absent.");
        void Open(string path)
        {
            owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [path]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path) && ViewOrNull() is not null);
        }
        void SaveCopy(bool cancelPicker)
        {
            cancel = cancelPicker;
            expectPicker = true;
            pickerHandled = false;
            if (saveAs)
            {
                ShortcutHintsSmoke.Key(owner, 0x53, 0x11, 0x10);
                Wait(() => pickerHandled && !expectPicker
                    && !Application.Current.Windows.Cast<Window>().Any(item => item != owner && item.IsVisible));
            }
            else owner.GetType().Assembly.GetType("KillerPDF.Features.IViewerHost", true)!.GetMethod("SaveAsWithName")!
                .Invoke(owner, ["proof-copy.pdf"]);
            if (failure is not null) throw failure;
            Product(pickerHandled && !expectPicker, "The real save picker must handle the requested copy action.");
        }
        void Product(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        void Wait(Func<bool> ready)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (failure is not null) throw failure;
                if (ready()) return;
                if (wait.Elapsed > TimeSpan.FromSeconds(30) || deadline.Elapsed > TimeSpan.FromSeconds(90))
                    throw new TimeoutException("Proof copy deadline expired at " + stage);
                Thread.Sleep(25);
            }
        }
    }
}
