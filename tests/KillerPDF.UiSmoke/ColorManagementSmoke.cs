using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;

internal static class ColorManagementSmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        if (Environment.MachineName is not ("SN01" or "SN02")) throw new InvalidOperationException("Color controls require an owned VM.");
        SelectedAssemblyIdentity.HashLoaded(typeof(PdfDocument).Assembly, appPath);
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "color-controls.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage(600, 750).Build();
        File.WriteAllBytes(source, original);
        int assertions = 0;
        string stage = "open";
        Exception? failure = null;
        bool handled = false;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(item => item != owner && item.IsVisible);
            if (dialog is null || handled) return;
            handled = true;
            try
            {
                Product(dialog.GetType().Name == "ColorManagementDialog", "The menu must open the Color Management panel.");
                Product(dialog.Title.Contains("Color Management", StringComparison.Ordinal), "The panel title must be readable English.");
                var controls = Descendants(dialog).OfType<FrameworkElement>().ToArray();
                T Control<T>(string id) where T : FrameworkElement => controls.OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);
                if (stage == "apply")
                {
                    var apply = controls.OfType<Button>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "ColorManagementApply");
                    Product(apply is not null && apply.IsVisible && apply.IsEnabled, "The panel must offer Apply for changed proof preferences.");
                    foreach (string id in new[] { "ProofEnabled", "ProofBpc", "ProofPaper", "ProofBlack", "ProofGamut" })
                        ((IToggleProvider)new CheckBoxAutomationPeer(Control<CheckBox>(id)).GetPattern(PatternInterface.Toggle)!).Toggle();
                    var intent = Control<ComboBox>("ProofIntent");
                    var peer = UIElementAutomationPeer.CreatePeerForElement(intent) ?? new ComboBoxAutomationPeer(intent);
                    var dropdown = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)!;
                    dropdown.Expand();
                    Wait(() => intent.ItemContainerGenerator.ContainerFromIndex(3) is ComboBoxItem { IsVisible: true });
                    var absolute = peer.GetChildren().Single(item => item.GetName() == "Absolute colorimetric"
                        && item.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider);
                    ((ISelectionItemProvider)absolute.GetPattern(PatternInterface.SelectionItem)!).Select();
                    dropdown.Collapse();
                    Product(intent.SelectedIndex == 3, "The actual combo box must select absolute colorimetric.");
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(apply!)));
                    return;
                }
                if (stage == "reopen")
                {
                    Product(Control<CheckBox>("ProofEnabled").IsChecked == true, "Reopening must show the applied proof request.");
                    Product(Control<ComboBox>("ProofIntent").SelectedIndex == 3, "Reopening must show the applied intent.");
                    Product(Control<CheckBox>("ProofBpc").IsChecked == false, "Reopening must show applied BPC off.");
                    foreach (string id in new[] { "ProofPaper", "ProofBlack", "ProofGamut" })
                        Product(Control<CheckBox>(id).IsChecked == true, "Reopening must show applied " + id + ".");
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementClose"))));
                    return;
                }
                var enabled = Control<CheckBox>("ProofEnabled");
                Product(enabled.IsVisible && enabled.IsEnabled && enabled.IsChecked == false, "A newly opened document must start with proof off.");
                Product(Control<CheckBox>("ProofBpc").IsChecked == true, "The initial intent must use black-point compensation.");
                Product(Control<ComboBox>("ProofIntent").SelectedIndex == 1, "Relative colorimetric must be initially selected.");
                foreach (string id in new[] { "ProofPaper", "ProofBlack", "ProofGamut" })
                {
                    var toggle = Control<CheckBox>(id);
                    Product(toggle.IsVisible && toggle.IsChecked == false, id + " must initially be off.");
                    Product(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle)), id + " needs an accessible name.");
                }
                Product(Control<ComboBox>("ProofProfile").SelectedIndex == 0, "The initial profile choice must explicitly represent no selection.");
                var close = Control<Button>("ColorManagementClose");
                Product(close.IsVisible && close.IsEnabled, "Close must remain reachable.");
                dialog.UpdateLayout();
                Point top = dialog.PointToScreen(new Point(4, 4));
                Point bottom = dialog.PointToScreen(new Point(dialog.ActualWidth - 4, dialog.ActualHeight - 4));
                var capture = NativeReadingSmoke.Capture(dialog, new Rect(top, bottom));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(capture));
                using (var output = File.Create(Path.Combine(folder, "panel.png"))) encoder.Save(output);
                owner.Dispatcher.BeginInvoke(new Action(() => Invoke(close)));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        try
        {
            owner.WindowState = WindowState.Normal;
            owner.Width = Math.Min(1200, SystemParameters.WorkArea.Width - 48);
            owner.Height = Math.Min(850, SystemParameters.WorkArea.Height - 48);
            owner.GetType().GetMethod("OpenFromExternal")!.Invoke(owner, [source]);
            Wait(() => View() is not null && ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(source));
            var before = View()!.CaptureProof().Settings;
            stage = "document-tools-menu";
            ShortcutHintsSmoke.Key(owner, 0x44, 0x11, 0x10);
            ContextMenu? menu = null;
            Wait(() => (menu = PresentationSource.CurrentSources.Cast<PresentationSource>().SelectMany(item => Descendants(item.RootVisual))
                .OfType<ContextMenu>().FirstOrDefault(item => item.IsOpen)) is not null);
            var item = menu!.Items.OfType<MenuItem>().SingleOrDefault(entry => entry.Header?.ToString() == "Color Management");
            Product(item is not null && item.IsEnabled, "Document tools must offer Color Management for an open PDF.");
            stage = "panel";
            timer.Start();
            ((IInvokeProvider)new MenuItemAutomationPeer(item!).GetPattern(PatternInterface.Invoke)!).Invoke();
            Wait(() => handled && !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible));
            if (failure is not null) throw failure;
            Product(handled, "The actual panel must have been observed and closed.");
            Product(View()!.CaptureProof().Settings == before, "Closing the initial panel must preserve document proof preferences.");
            foreach (string next in new[] { "apply", "reopen" })
            {
                stage = next;
                handled = false;
                ShortcutHintsSmoke.Key(owner, 0x44, 0x11, 0x10);
                Wait(() => (menu = PresentationSource.CurrentSources.Cast<PresentationSource>().SelectMany(source => Descendants(source.RootVisual))
                    .OfType<ContextMenu>().FirstOrDefault(candidate => candidate.IsOpen)) is not null);
                item = menu!.Items.OfType<MenuItem>().Single(entry => entry.Header?.ToString() == "Color Management");
                ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)!).Invoke();
                Wait(() => handled && !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible));
                var changed = View()!.CaptureProof().Settings;
                Product(changed.Enabled && changed.Options.RenderingIntent == PdfRenderingIntent.AbsoluteColorimetric
                    && !changed.Options.BlackPointCompensation && changed.Options.SimulatePaper
                    && changed.Options.SimulateBlack && changed.GamutWarnings,
                    "Actual controls must apply and retain every requested preference on the public document session.");
                Product(changed.PrinterProfile is null && changed.PrinterProfileSha256 is null,
                    "Applying without a profile must retain the explicit unavailable request without inventing a profile.");
            }
            Product(File.ReadAllBytes(source).SequenceEqual(original), "Opening and closing viewing controls must preserve the PDF bytes.");
            stage = "complete";
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            timer.Stop();
            foreach (var modal in Application.Current.Windows.Cast<Window>().Where(item => item != owner && item.IsVisible).ToArray()) modal.Close();
            if (owner.IsVisible) owner.Close();
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, stage, assertions, handled, error = failure?.ToString(),
                sourcePreserved = File.ReadAllBytes(source).SequenceEqual(original), closed = !owner.IsVisible,
                appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failure is null ? 0 : 1;

        PdfDocumentProofView? View()
        {
            var viewer = owner.GetType().GetProperty("ActiveViewer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
            return (PdfDocumentProofView?)viewer.GetType().GetProperty("DocumentProofView")!.GetValue(viewer);
        }
        void Product(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        void Wait(Func<bool> ready)
        {
            var local = Stopwatch.StartNew();
            while (!ready())
            {
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (failure is not null) throw failure;
                if (local.Elapsed > TimeSpan.FromSeconds(20) || clock.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Color control readiness expired at " + stage);
                Thread.Sleep(25);
            }
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int index = 0; index < count; index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
