using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class MacroProfileSmoke
{
    internal static int RunCase(Window owner, string folder, string scenario)
    {
        string selection = scenario["--verify-validate-profile-case-".Length..];
        string kind = selection.Split('-')[0] switch
        {
            "general" => "General", "print" => "PrintProduction", "attachments" => "Attachments",
            "custom" => "Custom", "invalid" => "Invalid", _ => throw new ArgumentException("Unknown profile case.")
        };
        Run(owner, folder, kind, selection.EndsWith("-cancel", StringComparison.Ordinal), extended: true);
        string result = "PASS verify-validate-profile-case: " + selection + " preserved profile, settings, outcome and source.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    internal static int Run(Window owner, string folder, string kind = "PrintProduction", bool cancel = false, bool extended = false)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type profiles = engine.GetType("KillerPdf.Engine.Diagnostics.PdfPreflightProfile", true)!;
        bool invalid = kind == "Invalid";
        object? expected = invalid ? null : kind == "Custom"
            ? profiles.GetMethod("FromJson")!.Invoke(null, [JsonSerializer.Serialize(new { version = 1, name = "Custom roundtrip",
                checks = new[] { "structuralIntegrity", "outputIntent", "fontEmbedding" }, minimumImageDpi = 177.5, maximumInkCoveragePercent = 227.0 })])
            : profiles.GetProperty(kind)!.GetValue(null)!;
        string expectedJson = invalid ? "{" : (string)profiles.GetMethod("ToJson")!.Invoke(expected, [false])!;
        var settings = new Dictionary<string, string> { ["profileJson"] = expectedJson };
        if (extended) settings["retained-option"] = "retained-value";
        string input = Path.Combine(folder, "validate-input.json"), output = Path.Combine(folder, "validate-output.json");
        File.WriteAllText(input, JsonSerializer.Serialize(new { version = 1, name = "PROFILE_ROUNDTRIP",
            steps = new[] { new { operation = "validate", settings } } }));
        _ = engine.GetType("KillerPdf.Engine.Documents.PdfMacro", true)!.GetMethod("FromJson")!.Invoke(null, [File.ReadAllText(input)]);
        string source = Path.Combine(folder, "profile-source.pdf");
        byte[] bytes = TableExportSmoke.Fixture();
        File.WriteAllBytes(source, bytes);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Wait(() => ((Button)owner.FindName("SaveMenuBtn")).IsEnabled, "macro source readiness");
        int phase = 0;
        bool invalidErrorShown = false;
        Window? invalidDialog = null;
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void Queue(Button button) => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        Button Named(Window window, string key) => Descendants<Button>(window).Single(button => Equals(button.Content, owner.FindResource(key))
            || System.Windows.Automation.AutomationProperties.GetAutomationId(button) == key);
        timer.Tick += (_, _) =>
        {
            Window? dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(window => window != owner && window.IsVisible);
            if (dialog is null) return;
            try
            {
                string type = dialog.GetType().Name;
                if (type == "MacroDialog" && phase == 0) { phase = 1; Queue(Named(dialog, "Str_Exchange_Import")); }
                else if (type == "FileDialog" && phase == 1)
                { phase = 2; ((TextBox)dialog.FindName("FileNameBox")).Text = input; Queue((Button)dialog.FindName("AcceptButton")); }
                else if (type == "MacroDialog" && phase == 2)
                {
                    ListBox list = Descendants<ListBox>(dialog).Single(item => item.Items.Cast<object>().Any(value => value.GetType().Name == "StepChoice"));
                    list.SelectedIndex = 0; phase = 3; Queue(Named(dialog, "Str_Redact_Properties"));
                }
                else if (type == "DocumentListDialog" && phase == 3)
                {
                    if (invalid) throw new InvalidOperationException("Invalid profile JSON silently opened a default profile selector.");
                    ListBox list = Descendants<ListBox>(dialog).Single();
                    Console.WriteLine("CONTROL Validate profile selection before no-op Apply: " + list.SelectedIndex);
                    if (cancel) list.SelectedIndex = (list.SelectedIndex + 1) % list.Items.Count;
                    phase = 4; Queue(Descendants<Button>(dialog).Single(button => cancel ? button.IsCancel : button.IsDefault));
                }
                else if (invalid && phase == 3 && Descendants<TextBlock>(dialog).Any(text => text.Text.StartsWith(
                    (string)owner.FindResource("Str_Macro_Invalid"), StringComparison.Ordinal)))
                { invalidErrorShown = true; invalidDialog = dialog; phase = 4; Queue(Named(dialog, "Str_Btn_OK")); }
                else if (type == "MacroDialog" && phase == 4) { phase = 5; Queue(Named(dialog, "Str_ExportImg_Export")); }
                else if (type == "FileDialog" && phase == 5)
                { phase = 6; ((TextBox)dialog.FindName("FileNameBox")).Text = output; Queue((Button)dialog.FindName("AcceptButton")); }
                else if (type == "MacroDialog" && phase == 6 && File.Exists(output))
                { phase = 7; Queue(Descendants<Button>(dialog).Single(button => button.IsCancel)); }
                else if (!ReferenceEquals(dialog, invalidDialog) && type is not ("MacroDialog" or "FileDialog" or "DocumentListDialog"))
                    throw new InvalidOperationException("Unexpected profile dialog: " + type + ": "
                        + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
            }
            catch (Exception error)
            {
                failure = error;
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).Reverse().ToArray()) window.Close();
            }
        };
        Button macros = DocumentToolRow(owner, "Str_DocumentTools_Macros");
        timer.Start();
        try
        {
            macros.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => phase == 7 || failure is not null, "macro profile edit/export");
            if (failure is not null) throw failure;
            Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible),
                "profile dialog fade completion");
        }
        finally { timer.Stop(); }
        using JsonDocument exported = JsonDocument.Parse(File.ReadAllText(output));
        string actualJson = exported.RootElement.GetProperty("steps")[0].GetProperty("settings").GetProperty("profileJson").GetString()!;
        if (extended && exported.RootElement.GetProperty("steps")[0].GetProperty("settings").GetProperty("retained-option").GetString() != "retained-value")
            throw new InvalidOperationException("Validate profile editing lost an unrelated setting.");
        if (invalid)
        {
            if (!invalidErrorShown || actualJson != expectedJson || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Invalid profile feedback replaced the existing step or changed the source.");
            Console.WriteLine("CONTROL invalid Validate JSON showed its error and preserved the exported step.");
            return 0;
        }
        object actual = profiles.GetMethod("FromJson")!.Invoke(null, [actualJson])!;
        Type runner = engine.GetType("KillerPdf.Engine.Diagnostics.PdfPreflightRunner", true)!;
        string Report(object profile)
        {
            object report = runner.GetMethod("Run", [typeof(ReadOnlyMemory<byte>), profile.GetType(), typeof(string)])!
                .Invoke(null, [new ReadOnlyMemory<byte>(bytes), profile, null])!;
            return (string)report.GetType().GetMethod("ToJson")!.Invoke(report, [false])!;
        }
        string before = Report(expected!), after = Report(actual);
        File.WriteAllText(Path.Combine(folder, "preflight-before.json"), before);
        File.WriteAllText(Path.Combine(folder, "preflight-after.json"), after);
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Profile editing changed the source PDF.");
        if ((string)profiles.GetMethod("ToJson")!.Invoke(actual, [false])! != expectedJson || after != before)
            throw new InvalidOperationException("UI-001: no-op Validate editing changed the existing PrintProduction profile or preflight outcome.");
        const string result = "PASS verify-validate-profile: imported Validate profile survives real no-op Properties/Apply/export with identical preflight outcome.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        if (extended) Console.WriteLine("CONTROL profile preserved: " + kind + (cancel ? " Cancel" : " Apply"));
        return 0;
    }
}
