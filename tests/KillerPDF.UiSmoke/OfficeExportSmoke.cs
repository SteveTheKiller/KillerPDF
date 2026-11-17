using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class OfficeExportSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(Window owner, Assembly assembly, string appPath, string folder)
    {
        Type type = assembly.GetType("KillerPDF.ExportDocumentDialog", true)!;
        var dialog = (Window)Activator.CreateInstance(type, Members, null, [owner], null)!;
        try
        {
            dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -10000; dialog.Top = -10000;
            dialog.Show(); Pump();
            ComboBox mode = Find<ComboBox>(dialog, "ExportDocumentMode");
            ComboBox format = Find<ComboBox>(dialog, "ExportDocumentFormat");
            if (mode.SelectedIndex != 0 || !mode.IsVisible) throw new InvalidOperationException("Word must start in editable mode.");
            mode.SelectedIndex = 1;
            format.SelectedIndex = 1; Pump();
            if (mode.IsVisible) throw new InvalidOperationException("Excel must not offer page-image mode.");
            format.SelectedIndex = 2; Pump();
            if (!mode.IsVisible) throw new InvalidOperationException("PowerPoint must offer Office layout choices.");
            mode.SelectedIndex = 1;
            Find<Button>(dialog, "ExportDocumentSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!(bool)type.GetProperty("Confirmed", Members)!.GetValue(dialog)!
                || type.GetProperty("OfficeMode", Members)!.GetValue(dialog)!.ToString() != "PageImages")
                throw new InvalidOperationException("The accepted layout did not reach the export workflow.");
            VerifyLayouts(owner, assembly, type, folder);
            string source = Path.Combine(folder, "source.pdf");
            File.WriteAllBytes(source, TableExportSmoke.Fixture());
            foreach (int selectedFormat in new[] { 0, 2 })
                foreach (int selectedMode in new[] { 0, 1 })
                    RunWorkflow(owner, source, Path.Combine(folder, $"workflow-{selectedMode}." + (selectedFormat == 0 ? "docx" : "pptx")), selectedFormat, selectedMode, false);
            RunWorkflow(owner, source, Path.Combine(folder, "source-switch.docx"), 0, 0, true);
            VerifyCli(appPath, source, folder);
            string result = "PASS Office layout selection, 14 themes at two scales, 20 locales, small-window layout, four saved exports, source-switch cancellation and CLI layout selection; app SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally { dialog.Close(); }
    }

    private static void VerifyCli(string appPath, string source, string folder)
    {
        foreach (string extension in new[] { "docx", "pptx", "xlsx" })
        {
            string destination = Path.Combine(folder, "cli-images." + extension);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { appPath, "--export", source, destination, "--layout", "images", "--pages", "1" })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("CLI export did not finish.");
            }
            if (extension == "xlsx")
            {
                if (process.ExitCode != 2 || File.Exists(destination))
                    throw new InvalidOperationException("CLI allowed page-image layout for Excel.");
                continue;
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("CLI export failed: " + extension);
            using var archive = ZipFile.OpenRead(destination);
            using Stream stream = archive.GetEntry(extension == "docx" ? "word/document.xml" : "ppt/slides/slide1.xml")!.Open();
            XDocument xml = XDocument.Load(stream);
            if (xml.Descendants().Any(element => element.Name.LocalName == "t")
                || xml.Descendants().Count(element => element.Name.LocalName == "blip") != 1)
                throw new InvalidOperationException("CLI ignored page-image layout: " + extension);
        }
    }

    private static void VerifyLayouts(Window owner, Assembly assembly, Type dialogType, string folder)
    {
        Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo themeApply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
        Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
        MethodInfo localeApply = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", Members)!;
        void Layout(string name, double scale, double width, double height)
        {
            owner.GetType().GetField("_appScale", Members)!.SetValue(owner, scale);
            var window = (Window)Activator.CreateInstance(dialogType, Members, null, [owner], null)!;
            try
            {
                window.ShowInTaskbar = false; window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
                window.Show(); window.SizeToContent = SizeToContent.Manual; window.Width = width; window.Height = height;
                Find<ComboBox>(window, "ExportDocumentMode").SelectedIndex = 1; Pump();
                foreach (FrameworkElement control in new FrameworkElement[] { Find<ComboBox>(window, "ExportDocumentFormat"),
                    Find<ComboBox>(window, "ExportDocumentMode"), Find<Button>(window, "ExportDocumentSave") })
                {
                    control.BringIntoView(); Pump();
                    Rect bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                    var available = new Rect(0, 0, window.ActualWidth, window.ActualHeight); available.Inflate(0.5, 0.5);
                    if (bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
                        throw new InvalidOperationException(name + ": control is not reachable: " + bounds);
                }
                var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using Stream file = File.Create(Path.Combine(folder, name + ".png")); png.Save(file);
            }
            finally { window.Close(); Pump(); }
        }
        foreach (string theme in Enum.GetNames(themeType))
        {
            themeApply.Invoke(null, [Enum.Parse(themeType, theme)]);
            foreach (double scale in new[] { 1d, 1.5 }) Layout(theme + "-" + scale, scale, 640, 480);
        }
        themeApply.Invoke(null, [Enum.Parse(themeType, "Dark")]);
        Layout("small-250", 2.5, 480, 360);
        foreach (string locale in Enum.GetNames(localeType))
        {
            localeApply.Invoke(null, [Enum.Parse(localeType, locale)]);
            Layout("locale-" + locale, 1.5, 640, 480);
        }
        localeApply.Invoke(null, [Enum.Parse(localeType, "EnUS")]);
        owner.GetType().GetField("_appScale", Members)!.SetValue(owner, 1d);
    }

    private static void RunWorkflow(Window owner, string source, string destination, int format, int mode, bool switchSource)
    {
        byte[] original = File.ReadAllBytes(source);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]); Pump();
        int stage = 0;
        Exception? failure = null;
        bool done = false;
        var seen = new HashSet<Window>();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 45)
                    throw new TimeoutException("Office export workflow timed out at " + stage);
                Window[] windows = Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray();
                if (stage == 0)
                {
                    stage = 1;
                    Children(owner).OfType<Button>().Single(button => button.Name == "SaveMenuBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return;
                }
                if (stage == 1)
                {
                    string label = (string)Application.Current.FindResource("Str_Menu_ExportDocument");
                    MenuItem? item = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                        .OfType<DependencyObject>().SelectMany(Children).OfType<MenuItem>().FirstOrDefault(item => Equals(item.Header, label));
                    if (item is null) return;
                    stage = 2;
                    if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
                    owner.Dispatcher.BeginInvoke(() => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                    return;
                }
                Window? review = windows.FirstOrDefault(window => AutomationProperties.GetAutomationId(window) == "ExportDocumentDialog");
                Window? picker = windows.FirstOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                if (stage == 2 && review is not null)
                {
                    if (switchSource)
                    {
                        string other = Path.Combine(Path.GetDirectoryName(source)!, "other.pdf"); File.WriteAllBytes(other, original);
                        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [other]);
                    }
                    Find<ComboBox>(review, "ExportDocumentFormat").SelectedIndex = format;
                    Find<ComboBox>(review, "ExportDocumentMode").SelectedIndex = mode;
                    stage = 3; Find<Button>(review, "ExportDocumentSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return;
                }
                if (stage == 3 && picker is not null)
                {
                    if (switchSource) throw new InvalidOperationException("Switching the source still opened the save picker.");
                    Children(picker).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = destination;
                    stage = 4;
                    Children(picker).OfType<Button>().Single(button => button.Name == "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return;
                }
                if (stage >= 3)
                {
                    foreach (Window message in windows.Where(window => window != review && window != picker))
                    {
                        if (!seen.Add(message)) continue;
                        Button? accept = Children(message).OfType<Button>().FirstOrDefault(button => button.IsDefault);
                        if (accept is not null) accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    if (review is null && picker is null && owner.IsEnabled && (switchSource || File.Exists(destination)))
                    { done = true; timer.Stop(); }
                }
            }
            catch (Exception error)
            {
                failure = error; done = true; timer.Stop();
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) window.Close();
            }
        };
        timer.Start();
        try
        {
            while (!done) { Pump(); if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("Office export workflow timed out at " + stage); Thread.Sleep(10); }
            if (failure is not null) throw failure;
            if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(source))) throw new InvalidOperationException("Source PDF changed.");
            if (switchSource)
            {
                if (File.Exists(destination)) throw new InvalidOperationException("Source switch created an output.");
                return;
            }
            using var archive = ZipFile.OpenRead(destination);
            using Stream stream = archive.GetEntry(format == 0 ? "word/document.xml" : "ppt/slides/slide1.xml")!.Open();
            XDocument xml = XDocument.Load(stream);
            if (mode == 0 && !xml.Descendants().Any(element => element.Name.LocalName == "tbl")) throw new InvalidOperationException("Saved export lost its editable table.");
            if (mode == 1 && (!xml.Descendants().Any(element => element.Name.LocalName == "blip") || xml.Descendants().Any(element => element.Name.LocalName == "t")))
                throw new InvalidOperationException("Saved export did not honor page-image mode.");
        }
        finally { timer.Stop(); }
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Children(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
