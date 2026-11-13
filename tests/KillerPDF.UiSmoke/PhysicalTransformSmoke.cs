using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class PhysicalTransformSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Window owner, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type builderType = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic crop = Enum.Parse(engine.GetType("KillerPdf.Engine.Authoring.PdfPageBox", true)!, "Crop");
        foreach (bool partial in new[] { false, true })
        foreach (double unit in new[] { 2d, 0.5, 1d })
        foreach (int rotation in new[] { 0, 90 })
        {
            string name = "unit-" + unit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + rotation + (partial ? "-partial" : "");
            string source = Path.Combine(folder, name + ".pdf");
            dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
            builder.AddBlankPage(120d, 240d).SetPageUserUnit(0, unit)
                .SetPageBox(0, crop, 10d, 20d, 100d, 200d).SetPageRotation(0, rotation);
            if (partial) builder.AddBlankPage(180d, 260d);
            File.WriteAllBytes(source, (byte[])builder.Build());
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
            owner.Opacity = 1; owner.Activate();
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "physical Transform source rendered");
            bool applied = false, closeQueued = false;
            Window? transformWindow = null;
            Button? applyButton = null;
            var seen = new HashSet<Window>();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                // Apply keeps the Transform window open, so it is closed once Apply has settled and changed the document.
                if (!closeQueued && transformWindow is { IsVisible: true } && applyButton is { IsEnabled: true } && Dirty(owner))
                {
                    closeQueued = true;
                    Button close = Descendants<Button>(transformWindow).Single(button => button.IsCancel);
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    if (dialog.GetType().Name == "TransformWindow")
                    {
                        ((ComboBox)dialog.GetType().GetField("_colorMode", Members)!.GetValue(dialog)!).SelectedIndex = 1;
                        ((Slider)dialog.GetType().GetField("_dpiSlider", Members)!.GetValue(dialog)!).Value = 72;
                        ((CheckBox)dialog.GetType().GetField("_setDpi", Members)!.GetValue(dialog)!).IsChecked = true;
                        applied = true;
                    }
                    Button? accept = Descendants<Button>(dialog).FirstOrDefault(button => button.IsDefault);
                    if (dialog.GetType().Name == "TransformWindow") { transformWindow = dialog; applyButton = accept; }
                    if (accept is not null) accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            timer.Start();
            try { Click(owner, "ToolRotateBtn"); Wait(() => applied && Dirty(owner), "physical Transform applied"); }
            finally { timer.Stop(); }
            Save(owner);
            object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
            dynamic pages = engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!.GetMethod("Read")!.Invoke(null, [document])!;
            double width = (rotation == 90 ? 200 : 100) * unit, height = (rotation == 90 ? 100 : 200) * unit;
            Console.WriteLine($"CONTROL {name}: transformed physical {pages[0].PhysicalWidth}x{pages[0].PhysicalHeight}, expected {width}x{height} points.");
            if (pages[0].PhysicalWidth != width || pages[0].PhysicalHeight != height)
                throw new InvalidOperationException("A grayscale Transform changed the physical page size.");
            if (((System.Collections.IEnumerable)pages).Cast<object>().Count() != (partial ? 2 : 1)
                || partial && (pages[1].PhysicalWidth != 180d || pages[1].PhysicalHeight != 260d))
                throw new InvalidOperationException("Partial Transform changed an unselected page.");
            var sizes = (System.Collections.IEnumerable)owner.GetType().Assembly
                .GetType("KillerPDF.Services.PdfEngineIntegration", true)!.GetMethod("ReadScanSizes", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [source])!;
            object size = sizes.Cast<object>().First();
            if ((int)size.GetType().GetField("Item1")!.GetValue(size)! != width
                || (int)size.GetType().GetField("Item2")!.GetValue(size)! != height)
                throw new InvalidOperationException("Transform did not store the requested 72-DPI image dimensions.");
            Click(owner, "UndoBtn"); Pump();
            Click(owner, "RedoBtn"); Pump();
            if (Dirty(owner)) throw new InvalidOperationException("Transform Undo/Redo did not return to the saved checkpoint.");
            string reopened = Path.Combine(folder, "reopened-" + name + ".pdf");
            File.Copy(source, reopened);
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [reopened]);
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "physical Transform saved copy reopened");
        }
        Console.WriteLine("PASS actual grayscale Transform keeps physical sizes across UserUnit, crop, rotation, Save/reopen and Undo/Redo.");
        return 0;
    }, dismissDuringRun: false);
}
