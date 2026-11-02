using System.IO;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ImageExportWorkflowSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(Window owner, string folder)
        => FormWorkflowFailureSmoke.WithCleanup(owner, () => VerifyConsent(owner, folder), dismissDuringRun: false);

    internal static int RunForms(Window owner, string folder, bool ocr = false)
        => FormWorkflowFailureSmoke.WithCleanup(owner, () => VerifyForms(owner, folder, ocr), dismissDuringRun: false);

    private static int VerifyForms(Window owner, string folder, bool ocr)
    {
        foreach (bool password in new[] { false, true })
        foreach (int rotation in new[] { 0, 90 })
        {
            string label = (password ? "password" : "text") + rotation;
            string source = Path.Combine(folder, label + ".pdf");
            byte[] original = FormWorkflowFailureSmoke.Fixture(password: password);
            File.WriteAllBytes(source, original);
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
            owner.Opacity = 1; owner.Activate();
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "pending forms source rendered");
            object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
                ?? target.GetType().GetField(name, Members)!.GetValue(target)!;
            object viewer = Get(owner, "ActiveViewer");
            string[] names = ["_formTextValues", "_formChoiceValues", "_formMultiChoiceValues", "_formCheckValues", "_formRadioValues", "_formFontSizes"];
            ((IDictionary)Get(viewer, names[0]))["answer"] = "Updated form value";
            ((IDictionary)Get(viewer, names[1]))["choice"] = "B";
            ((IDictionary)Get(viewer, names[2]))["multi"] = new List<string> { "A", "B" };
            ((IDictionary)Get(viewer, names[3]))["check"] = true;
            ((IDictionary)Get(viewer, names[4]))["radio"] = "B";
            ((IDictionary)Get(viewer, names[5]))["answer"] = 18d;
            ((IDictionary)Get(owner, "_pageRotations"))[0] = rotation;
            owner.GetType().GetMethod("MarkDirty", Members)!.Invoke(owner, [true]);
            string State(string name) { object value = Get(viewer, name); return JsonSerializer.Serialize(value, value.GetType()); }
            string[] pending = names.Select(State).ToArray();
            object document = Get(owner, "_doc");
            string current = (string)Get(owner, "_currentFile");
            CurrentOutput(owner, Path.Combine(folder, label + "-pending.png"), ocr);
            if (!ReferenceEquals(document, Get(owner, "_doc")) || current != (string)Get(owner, "_currentFile")
                || names.Where((name, index) => State(name) != pending[index]).Any() || !Dirty(owner)
                || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                throw new InvalidOperationException("Preparing image output changed the live document or pending forms.");
            Save(owner);
            string reference = Path.Combine(folder, label + "-reference.pdf");
            File.Copy(source, reference);
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [reference]);
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "saved form reference rendered");
            CurrentOutput(owner, Path.Combine(folder, label + "-reference.png"), ocr);
            if (!File.ReadAllBytes(Path.Combine(folder, label + "-pending-page-001.png")).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(folder, label + "-reference-page-001.png"))))
                throw new InvalidOperationException("Pending form image differs from the saved and reopened reference: " + label);
            Console.WriteLine("PASS " + (ocr ? "OCR snapshot" : "image export") + " includes every pending field kind, font size and single rotation: " + label);
        }
        return 0;
    }

    private static void CurrentOutput(Window owner, string chosen, bool ocr)
    {
        if (!ocr) { Export(owner, chosen, true, false); return; }
        string snapshot = Path.ChangeExtension(chosen, ".pdf");
        Type host = owner.GetType().Assembly.GetType("KillerPDF.Features.IOcrHost", true)!;
        host.GetMethod("SaveDocumentTo")!.Invoke(owner, [snapshot]);
        if (CliOutputSmoke.Command(Path.ChangeExtension(owner.GetType().Assembly.Location, ".exe"),
                "--to-image", snapshot, Path.GetDirectoryName(snapshot)!, "--dpi", "72") != 0)
            throw new InvalidOperationException("The actual OCR snapshot could not render.");
    }

    private static int VerifyConsent(Window owner, string folder)
    {
        string source = Path.Combine(folder, "source.pdf");
        byte[] original = TableExportSmoke.Fixture();
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "image export source rendered");
        string chosen = Path.Combine(folder, "export.png"), actual = Path.Combine(folder, "export-page-001.png");
        byte[] sentinel = Encoding.ASCII.GetBytes("existing output");
        File.WriteAllBytes(actual, sentinel);
        Export(owner, chosen, approve: false, expectConfirmation: true);
        if (!sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(actual)))
            throw new InvalidOperationException("Declining the actual output overwrite changed the file.");
        Export(owner, chosen, approve: true, expectConfirmation: true);
        if (!File.ReadAllBytes(actual).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Approved export failed to publish a complete image or changed its input.");
        VerifyCancellation(owner.GetType().Assembly, folder);
        Console.WriteLine("PASS live image export asks for the actual numbered output, preserves refusal and publishes approval; cancellation counts only complete images.");
        return 0;
    }

    internal static void Export(Window owner, string chosen, bool approve, bool expectConfirmation)
    {
        int confirmations = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        void Queue(Button button) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    if (dialog.GetType().Name == "ExportImagesDialog")
                    {
                        ((TextBox)dialog.GetType().GetField("_dpi", Members)!.GetValue(dialog)!).Text = "72";
                        Queue(Descendants<Button>(dialog).Single(button => button.IsDefault));
                    }
                    else if (dialog.GetType().Name == "FileDialog")
                    {
                        ((TextBox)dialog.FindName("FileNameBox")).Text = chosen;
                        Queue((Button)dialog.FindName("AcceptButton"));
                    }
                    else
                    {
                        string text = string.Join("\n", Descendants<TextBlock>(dialog).Select(block => block.Text));
                        string actualName = Path.GetFileNameWithoutExtension(chosen) + "-page-001.png";
                        if (!text.Contains(actualName, StringComparison.Ordinal))
                            throw new InvalidOperationException("Unexpected export dialog: " + text);
                        confirmations++;
                        Queue(Descendants<Button>(dialog).Single(button => Equals(button.Content,
                            owner.FindResource(approve ? "Str_Btn_Yes" : "Str_Btn_No"))));
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray()) dialog.Close();
            }
        };
        timer.Start();
        try
        {
            var export = (Task)owner.GetType().GetMethod("ExportImagesFlow", Members)!.Invoke(owner, [""])!;
            Wait(() => export.IsCompleted || failure is not null, "image export completed");
            if (failure is not null) throw failure;
            export.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); }
        if (confirmations != (expectConfirmation ? 1 : 0))
            throw new InvalidOperationException("Image export did not obtain exactly the required actual-output consent.");
    }

    private static void VerifyCancellation(Assembly app, string folder)
    {
        Type builderType = Assembly.Load("KillerPdf.Engine").GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        string source = Path.Combine(folder, "cancel.pdf");
        byte[] original = builder.AddBlankPage(200d, 300d).AddBlankPage(200d, 300d).Build();
        File.WriteAllBytes(source, original);
        using var cancel = new CancellationTokenSource();
        Action<int, int> progress = (_, _) => cancel.Cancel();
        int written = (int)app.GetType("KillerPDF.Services.PdfRasterize", true)!.GetMethod("ExportPageImages", Members)!
            .Invoke(null, [source, new[] { 0, 1 }, new int[2], 72d, false, folder, "canceled", 3, progress, cancel.Token, null, null, null, null])!;
        if (written != 1 || !File.Exists(Path.Combine(folder, "canceled-page-001.png"))
            || File.Exists(Path.Combine(folder, "canceled-page-002.png"))
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Canceled export misreported or damaged its partial batch.");
    }
}
