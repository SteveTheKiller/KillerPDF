using System.Diagnostics;
using System.Buffers.Binary;
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
        DispatcherTimer? pickerTimer = null;
        EventHandler? renderReady = null;
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
                if (stage is "import-profile" or "cancel-import" or "invalid-import")
                {
                    bool cancel = stage == "cancel-import";
                    bool invalid = stage == "invalid-import";
                    var import = controls.OfType<Button>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "ProofImportProfile");
                    Product(import is not null && import.IsVisible && import.IsEnabled, "The actual panel must offer Import profile.");
                    var prior = View()!.CaptureProof();
                    Window? picker = null;
                    bool submitted = false;
                    bool invalidFramePending = false;
                    bool invalidFrameReady = false;
                    pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    pickerTimer.Tick += (_, _) =>
                    {
                        try
                        {
                            if (!submitted)
                            {
                                picker = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.Owner == dialog
                                    && window.IsVisible && window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                                if (picker is null) return;
                                if (cancel)
                                {
                                    string caption = (string)Application.Current.FindResource("Str_Btn_Cancel");
                                    var cancelButton = Descendants(picker).OfType<Button>().Single(button => button.Content?.ToString() == caption);
                                    submitted = true;
                                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(cancelButton)));
                                    return;
                                }
                                var name = (TextBox)picker.FindName("FileNameBox");
                                var accept = (Button)picker.FindName("AcceptButton");
                                if (!name.IsVisible || !name.IsEnabled || !accept.IsVisible) return;
                                ((IValueProvider)new TextBoxAutomationPeer(name).GetPattern(PatternInterface.Value)!)
                                    .SetValue(Path.Combine(folder, invalid ? "invalid-paper.icc" : "imported-paper.icc"));
                                if (!accept.IsEnabled) return;
                                submitted = true;
                                owner.Dispatcher.BeginInvoke(new Action(() => Invoke(accept)));
                                return;
                            }
                            if (picker!.IsVisible) return;
                            var status = Control<TextBlock>("ProofImportStatus");
                            var failureText = status.Text;
                            var profile = Control<ComboBox>("ProofProfile");
                            if (!profile.IsEnabled || profile.SelectedItem?.ToString() != "imported-paper") return;
                            if (invalid)
                            {
                                if (string.IsNullOrEmpty(failureText)) return;
                                if (!invalidFramePending)
                                {
                                    invalidFramePending = true;
                                    int frames = 0;
                                    renderReady = (_, _) =>
                                    {
                                        if (++frames < 2) return;
                                        CompositionTarget.Rendering -= renderReady;
                                        renderReady = null;
                                        invalidFrameReady = true;
                                    };
                                    CompositionTarget.Rendering += renderReady;
                                    dialog.InvalidateVisual();
                                    return;
                                }
                                if (!invalidFrameReady) return;
                                Product(status.IsVisible && failureText == "The profile could not be imported. Check the file and try again.",
                                    "An invalid ICC must produce a readable diagnosis.");
                                Product(File.ReadAllBytes(Path.Combine(folder, "invalid-paper.icc")).SequenceEqual(InvalidProfile()),
                                    "Refusing an ICC must preserve its source bytes.");
                                dialog.UpdateLayout();
                                var invalidCapture = NativeReadingSmoke.Capture(dialog, new Rect(dialog.PointToScreen(new Point(4, 4)),
                                    dialog.PointToScreen(new Point(dialog.ActualWidth - 4, dialog.ActualHeight - 4))));
                                var invalidEncoder = new PngBitmapEncoder();
                                invalidEncoder.Frames.Add(BitmapFrame.Create(invalidCapture));
                                using (var output = File.Create(Path.Combine(folder, "panel-invalid-import.png"))) invalidEncoder.Save(output);
                            }
                            else if (!string.IsNullOrEmpty(failureText)) throw new InvalidOperationException("Actual profile import failed: " + failureText);
                            Product(profile.Items.Count == 3, "Import, cancellation and invalid input must preserve the expected profile list.");
                            Product(View()!.CaptureProof() == prior, "Import must not apply draft proof settings to the document.");
                            Product(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "imported-paper.icc"))))
                                == "C7E48BB35732B34F325CC92E08847232CA62A262F298840ECFBAC103C0577364", "Import must preserve the original ICC file.");
                            pickerTimer.Stop();
                            owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>(cancel || invalid ? "ColorManagementClose" : "ColorManagementApply"))));
                        }
                        catch (Exception error)
                        {
                            failure = error;
                            pickerTimer.Stop();
                            if (picker?.IsVisible == true) picker.Close();
                            dialog.Close();
                        }
                    };
                    pickerTimer.Start();
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(import!)));
                    return;
                }
                if (stage == "reopen-imported")
                {
                    Product(Control<ComboBox>("ProofProfile").SelectedItem?.ToString() == "imported-paper",
                        "Reopening must show the profile imported through the actual picker.");
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementClose"))));
                    return;
                }
                if (stage == "corrupt-library")
                {
                    Product(Control<TextBlock>("ProofProfileStatus").Text == "The profile library could not be read. Your current proof settings are unchanged.",
                        "A damaged library must produce a readable panel diagnosis.");
                    Product(!Control<ComboBox>("ProofProfile").IsEnabled && !Control<Button>("ColorManagementApply").IsEnabled,
                        "A damaged library must not allow accidental replacement of the requested profile.");
                    dialog.UpdateLayout();
                    var damagedCapture = NativeReadingSmoke.Capture(dialog, new Rect(
                        dialog.PointToScreen(new Point(4, 4)),
                        dialog.PointToScreen(new Point(dialog.ActualWidth - 4, dialog.ActualHeight - 4))));
                    var damagedEncoder = new PngBitmapEncoder();
                    damagedEncoder.Frames.Add(BitmapFrame.Create(damagedCapture));
                    using (var output = File.Create(Path.Combine(folder, "panel-library-error.png"))) damagedEncoder.Save(output);
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementClose"))));
                    return;
                }
                if (stage == "missing-profile")
                {
                    var warning = controls.OfType<TextBlock>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "ProofProfileStatus");
                    Product(warning is not null && warning.IsVisible
                        && warning.Text == "The selected profile is unavailable. Viewing uses normal color.",
                        "The actual panel must clearly diagnose the unavailable profile and ordinary viewing fallback.");
                    dialog.UpdateLayout();
                    var missingCapture = NativeReadingSmoke.Capture(dialog, new Rect(
                        dialog.PointToScreen(new Point(4, 4)),
                        dialog.PointToScreen(new Point(dialog.ActualWidth - 4, dialog.ActualHeight - 4))));
                    var missingEncoder = new PngBitmapEncoder();
                    missingEncoder.Frames.Add(BitmapFrame.Create(missingCapture));
                    using (var output = File.Create(Path.Combine(folder, "panel-missing.png"))) missingEncoder.Save(output);
                    owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementApply"))));
                    return;
                }
                if (stage is "select-profile" or "reopen-profile")
                {
                    var profile = Control<ComboBox>("ProofProfile");
                    Product(profile.Items.Cast<object>().Any(item => item.ToString() == "Gray paper"),
                        "The actual profile chooser must list the imported profile library entry.");
                    if (stage == "select-profile")
                    {
                        var peer = UIElementAutomationPeer.CreatePeerForElement(profile) ?? new ComboBoxAutomationPeer(profile);
                        var dropdown = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)!;
                        dropdown.Expand();
                        Wait(() => profile.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem { IsVisible: true });
                        var entry = peer.GetChildren().Single(item => item.GetName() == "Gray paper"
                            && item.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider);
                        ((ISelectionItemProvider)entry.GetPattern(PatternInterface.SelectionItem)!).Select();
                        dropdown.Collapse();
                        owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementApply"))));
                    }
                    else
                    {
                        Product(profile.SelectedItem?.ToString() == "Gray paper", "Reopening must retain the selected named profile.");
                        owner.Dispatcher.BeginInvoke(new Action(() => Invoke(Control<Button>("ColorManagementClose"))));
                    }
                    return;
                }
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
            foreach (string next in new[] { "apply", "reopen", "select-profile", "reopen-profile", "missing-profile", "import-profile", "reopen-imported", "cancel-import", "invalid-import", "corrupt-library" })
            {
                if (next == "select-profile")
                {
                    var type = owner.GetType().Assembly.GetType("KillerPDF.Services.ProofProfileLibrary", throwOnError: true)!;
                    var library = Activator.CreateInstance(type)!;
                    type.GetMethod("Import")!.Invoke(library, ["Gray paper", new ReadOnlyMemory<byte>(GrayProfile())]);
                }
                if (next == "missing-profile")
                {
                    string held = Path.GetFullPath(Path.Combine(folder, "held-profile.icc"));
                    string stored = Path.GetFullPath(Path.Combine(folder, "data", "color-management", "profiles",
                        "7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1.icc"));
                    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
                    if (!held.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        || !stored.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Profile fixture escaped owned evidence.");
                    File.Move(stored, held);
                }
                if (next == "import-profile")
                {
                    byte[] imported = GrayProfile();
                    imported[48] = 65;
                    File.WriteAllBytes(Path.Combine(folder, "imported-paper.icc"), imported);
                }
                if (next == "invalid-import") File.WriteAllBytes(Path.Combine(folder, "invalid-paper.icc"), InvalidProfile());
                if (next == "corrupt-library")
                {
                    string catalog = Path.GetFullPath(Path.Combine(folder, "data", "color-management", "profiles", "profiles.json"));
                    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
                    if (!catalog.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Library fixture escaped owned evidence.");
                    File.WriteAllText(catalog, "{invalid-library");
                }
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
                if (next is "import-profile" or "reopen-imported" or "cancel-import" or "invalid-import" or "corrupt-library")
                    Product(changed.PrinterProfile is not null
                        && changed.PrinterProfileSha256 == "C7E48BB35732B34F325CC92E08847232CA62A262F298840ECFBAC103C0577364",
                        "Apply must load the exact picker-imported ICC content and retain it through reopening and library damage.");
                else if (next == "missing-profile")
                    Product(changed.PrinterProfile is null
                        && changed.PrinterProfileSha256 == "7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1",
                        "Unavailable profile Apply must retain the requested content reference without substituting another profile.");
                else if (next is "select-profile" or "reopen-profile")
                    Product(changed.PrinterProfile is not null
                        && changed.PrinterProfileSha256 == "7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1",
                        "Selecting the named profile must load its exact validated content into the document session.");
                else
                    Product(changed.PrinterProfile is null && changed.PrinterProfileSha256 is null,
                        "Applying without a profile must retain the explicit unavailable request without inventing a profile.");
                if (next == "corrupt-library")
                    Product(File.ReadAllText(Path.Combine(folder, "data", "color-management", "profiles", "profiles.json")) == "{invalid-library",
                        "Opening and closing the diagnosis must preserve the damaged catalog for recovery.");
            }
            Product(File.ReadAllBytes(source).SequenceEqual(original), "Opening and closing viewing controls must preserve the PDF bytes.");
            stage = "complete";
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            timer.Stop();
            pickerTimer?.Stop();
            if (renderReady is not null) CompositionTarget.Rendering -= renderReady;
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
    private static byte[] InvalidProfile()
    {
        byte[] bytes = GrayProfile();
        bytes[36] = 0;
        return bytes;
    }
    private static byte[] GrayProfile()
    {
        byte[] data = new byte[156];
        Write(0, 156);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write(128, 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write(136, 144);
        Write(140, 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return data;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int index = 0; index < count; index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
