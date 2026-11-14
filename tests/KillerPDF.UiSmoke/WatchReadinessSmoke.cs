using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class WatchReadinessSmoke
{
    internal static int Run(Window owner, string folder, bool failedJob = false, bool unsupportedStep = false)
    {
        string name = unsupportedStep ? "NATIVE_WATCH_UNSUPPORTED" : failedJob ? "NATIVE_WATCH_COLOR_FAILURE" : "NATIVE_WATCH_DEU";
        string input = Path.Combine(folder, "watch-input"), output = Path.Combine(folder, "watch-output");
        Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        byte[] bytes = failedJob ? LabFixture() : TableExportSmoke.Fixture();
        string held = Path.Combine(input, "preserved.hold"), source = Path.Combine(folder, "source.pdf");
        File.WriteAllBytes(held, bytes); File.WriteAllBytes(source, bytes);
        string watchedSource = Path.Combine(input, "watched-source.pdf");
        if (failedJob) File.WriteAllBytes(watchedSource, bytes);
        string macroPath = Path.Combine(folder, "macro-input.json");
        string json = JsonSerializer.Serialize(new { version = 1, name,
            steps = new[] { new { operation = "ocr", settings = new { language = "deu" } } } });
        if (failedJob) json = JsonSerializer.Serialize(new { version = 1, name, steps = new object[] {
            new { operation = "resize", settings = new { percent = "100" } },
            new { operation = "convertColor", settings = new { mode = "Grayscale", threshold = "160" } } } });
        if (unsupportedStep) json = JsonSerializer.Serialize(new { version = 1, name,
            steps = new[] { new { operation = "save" }, new { operation = "redact" } } });
        File.WriteAllText(macroPath, json);
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        _ = engine.GetType("KillerPdf.Engine.Documents.PdfMacro", true)!.GetMethod("FromJson")!.Invoke(null, [json]);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Wait(() => ((Button)owner.FindName("SaveMenuBtn")).IsEnabled, "watch setup source readiness");
        int phase = 0;
        bool saved = false, startReturned = false, pauseReturned = false, started = false, usefulError = false;
        string errorText = "";
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void Queue(Button button, Action? after = null) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() => { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); after?.Invoke(); }));
        Button Named(Window window, string key) => Descendants<Button>(window).Single(button => Equals(button.Content, owner.FindResource(key)));
        T Id<T>(Window window, string id) where T : DependencyObject => Descendants<T>(window).Single(item => AutomationProperties.GetAutomationId(item) == id);
        timer.Tick += (_, _) =>
        {
            Window? dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(window => window != owner && window.IsVisible);
            if (dialog is null) return;
            try
            {
                string type = dialog.GetType().Name;
                if (type == "MacroDialog" && phase == 0) { phase = 1; Queue(Named(dialog, "Str_Exchange_Import")); }
                else if (type == "FileDialog" && phase == 1)
                { ((TextBox)dialog.FindName("FileNameBox")).Text = macroPath; phase = 2; Queue((Button)dialog.FindName("AcceptButton")); }
                else if (type == "MacroDialog" && phase == 2)
                { phase = 3; Queue(Named(dialog, "Str_DocInfo_Save"), () => saved = true); }
                else if (type == "MacroDialog" && phase == 3 && saved)
                { phase = 4; Queue(Id<Button>(dialog, "OpenWatchedFolders")); }
                else if (type == "WatchedFolderDialog" && phase == 4)
                {
                    Id<TextBox>(dialog, "WatchInput").Text = input;
                    Id<TextBox>(dialog, "WatchOutput").Text = output;
                    Id<TextBox>(dialog, "WatchFilter").Text = "*.pdf";
                    ComboBox macros = Id<ComboBox>(dialog, "WatchMacro");
                    macros.SelectedIndex = Enumerable.Range(0, macros.Items.Count).Single(index =>
                        Equals(macros.Items[index].GetType().GetProperty("Label")!.GetValue(macros.Items[index]), name));
                    object macro = macros.SelectedItem.GetType().GetProperty("Macro")!.GetValue(macros.SelectedItem)!;
                    object[] steps = ((System.Collections.IEnumerable)macro.GetType().GetProperty("Steps")!.GetValue(macro)!).Cast<object>().ToArray();
                    var settings = (IReadOnlyDictionary<string, string>)steps[0].GetType().GetProperty("Settings")!.GetValue(steps[0])!;
                    if (unsupportedStep)
                    {
                        if (steps.Length != 2 || steps[1].GetType().GetProperty("Operation")!.GetValue(steps[1])!.ToString() != "Redact"
                            || Directory.GetFiles(input, "*.pdf").Length != 0)
                            throw new InvalidOperationException("Unsupported-step fixture must have Save then Redact and no matching inputs.");
                        Console.WriteLine("CONTROL saved macro has unsupported Redact in step 2, with no matching input PDFs.");
                    }
                    else if (failedJob)
                    {
                        if (steps.Length != 2 || settings["percent"] != "100" || Directory.GetFiles(input, "*.pdf").Length != 1)
                            throw new InvalidOperationException("Synthetic two-step watched macro fixture is invalid.");
                        Console.WriteLine("CONTROL saved watched macro has Resize then ConvertColor and one synthetic Lab-color PDF.");
                    }
                    else if (settings["language"] != "deu" || Directory.GetFiles(input, "*.pdf").Length != 0
                        || Directory.GetFiles(folder, "deu.*", SearchOption.AllDirectories).Length != 0
                        || Directory.GetDirectories(folder, "deu", SearchOption.AllDirectories).Length != 0)
                        throw new InvalidOperationException("Missing-language and empty-input fixture controls failed.");
                    if (!failedJob && !unsupportedStep) Console.WriteLine("CONTROL selected saved OCR macro requests deu, no German model exists in isolation, and the input has no matching PDFs.");
                    phase = 5;
                }
                else if (type == "WatchedFolderDialog" && phase == 5)
                {
                    Button start = Id<Button>(dialog, "WatchStart");
                    if (start.IsEnabled) { phase = 6; Queue(start, () => startReturned = true); }
                    else { phase = 6; startReturned = true; }
                }
                else if (type == "WatchedFolderDialog" && phase == 6 && startReturned)
                {
                    if (failedJob)
                    {
                        string[] rows = Id<ListBox>(dialog, "WatchResults").Items.Cast<object>().Select(item => item.ToString() ?? "").ToArray();
                        string? failed = rows.FirstOrDefault(row => row.StartsWith("watched-source.pdf\n", StringComparison.Ordinal)
                            && row.Contains((string)owner.FindResource("Str_Watch_Failed"), StringComparison.Ordinal));
                        if (failed is null) return;
                        errorText = failed;
                        usefulError = failed.Contains("Step 2: " + (string)owner.FindResource("Str_Tf_ColorMode"), StringComparison.Ordinal);
                        started = Id<Button>(dialog, "WatchPause").IsEnabled;
                        Console.WriteLine("CONTROL watched job failure row: " + failed);
                        phase = 7;
                        if (started) Queue(Id<Button>(dialog, "WatchPause"), () => pauseReturned = true);
                        else pauseReturned = true;
                        return;
                    }
                    errorText = Id<TextBlock>(dialog, "WatchError").Text;
                    started = Id<Button>(dialog, "WatchPause").IsEnabled;
                    usefulError = unsupportedStep
                        ? errorText.Contains("Step 2: " + (string)owner.FindResource("Str_Lbl_Redact"), StringComparison.Ordinal)
                        : errorText.Contains("deu", StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"CONTROL watch readiness: started={started}, error={errorText}");
                    phase = 7;
                    if (started) Queue(Id<Button>(dialog, "WatchPause"), () => pauseReturned = true);
                    else pauseReturned = true;
                }
                else if (type == "WatchedFolderDialog" && phase == 7 && pauseReturned && !Id<Button>(dialog, "WatchPause").IsEnabled)
                { phase = 8; Queue(Id<Button>(dialog, "WatchClose")); }
                else if (type == "MacroDialog" && phase == 8)
                { phase = 9; Queue(Descendants<Button>(dialog).Single(button => button.IsCancel)); }
                else if (type is not ("MacroDialog" or "FileDialog" or "WatchedFolderDialog"))
                    throw new InvalidOperationException("Unexpected watched setup dialog: " + type + ": "
                        + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
            }
            catch (Exception error)
            {
                failure = error;
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).Reverse().ToArray()) window.Close();
            }
        };
        Button macrosItem = DocumentToolRow(owner, "Str_DocumentTools_Macros");
        timer.Start();
        try
        {
            macrosItem.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => phase == 9 || failure is not null, "watched-folder missing-language setup");
            if (failure is not null) throw failure;
            Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible), "watched setup modal cleanup");
        }
        finally { timer.Stop(); }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes) || !File.ReadAllBytes(held).AsSpan().SequenceEqual(bytes)
            || (failedJob && !File.ReadAllBytes(watchedSource).AsSpan().SequenceEqual(bytes))
            || Directory.GetFiles(output).Length != 0) throw new InvalidOperationException("Watched setup changed synthetic sources or produced output.");
        File.WriteAllText(Path.Combine(folder, "readiness.txt"), $"started={started}\nerror={errorText}\n");
        if ((!failedJob && started) || !usefulError) throw new InvalidOperationException(unsupportedStep
            ? "UI-005: watched setup omitted the unsupported step and operation."
            : failedJob
            ? "UI-005: failed watched job omitted its failed step and operation."
            : "UI-005: watched setup started or failed to identify missing German OCR support before work.");
        string result = unsupportedStep ? "PASS verify-watch-unsupported-step: actual setup identifies unsupported step 2 and operation, stays paused and preserves source and output."
            : failedJob ? "PASS verify-watch-job-failure: actual failed synthetic job identifies step and operation, stays paused and preserves source and output."
            : "PASS verify-watch-readiness: missing German OCR support is identified before Start; empty input stays paused and source bytes/output remain intact.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result); Console.WriteLine(result);
        return 0;
    }

    private static byte[] LabFixture()
    {
        const string content = "/Lab cs 50 20 10 scn 10 10 50 50 re f\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ColorSpace << /Lab [/Lab << /WhitePoint [0.9642 1 0.8249] >>] >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
