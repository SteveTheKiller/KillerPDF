using System.Collections;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using System.Runtime.Loader;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    const BindingFlags M = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, M)!.Invoke(target, args);
    static object? Get(object target, string name) => target.GetType().GetField(name, M)?.GetValue(target) ?? target.GetType().GetProperty(name, M)?.GetValue(target);
    static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var element in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var child in Tree(element)) yield return child;
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            bool native = args.Contains("--native-drag") || args.Contains("--native-click");
            if (native && !new[] { "SN01", "SN02" }.Contains(Environment.MachineName))
                throw new InvalidOperationException("Native signature acceptance requires an owned lab VM.");
            string appPath = Path.GetFullPath(args[0]);
            AssemblyLoadContext.Default.Resolving += (_, name) => {
                string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var assembly = Assembly.LoadFrom(appPath);
            typeof(Application).GetField("_resourceAssembly", M)!.SetValue(null, assembly);
            assembly.GetType("KillerPDF.Services.AppDataPaths")!.GetProperty("StartupSmokeRoot", M)!.SetValue(null, args[1]);
            Type appType = assembly.GetType("KillerPDF.App")!;
            var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SignaturePlacementHost"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Host").DefineType("IsolatedApplication", TypeAttributes.Public, appType);
            host.DefineDefaultConstructor(MethodAttributes.Public);
            var startup = host.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                typeof(void), [typeof(StartupEventArgs)]);
            startup.GetILGenerator().Emit(OpCodes.Ret);
            host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", M)!);
            var app = (Application)Activator.CreateInstance(host.CreateType()!)!;
            var xamlSource = XDocument.Load(Path.Combine(Path.GetDirectoryName(appPath)!, "App.xaml"));
            var resources = xamlSource.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
            foreach (var declaration in xamlSource.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                resources.SetAttributeValue(declaration.Name, declaration.Value);
            string xaml = resources.ToString();
            foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
                xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=" + assembly.GetName().Name + "\"", StringComparison.Ordinal);
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
                new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/" + assembly.GetName().Name + ";component/") });
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            assembly.GetType("KillerPDF.Services.AppTheme")!.GetMethod("Configure", M)!.Invoke(null, null);
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service)!.GetMethod("Initialize", M)!.Invoke(null, null);
            var owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow")!)!;
            if (native)
            {
                owner.Width = 1100; owner.Height = 800;
                owner.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                owner.Show(); owner.Activate(); Pump(500);
            }
            Directory.CreateDirectory(args[1]);
            string pdf = Path.Combine(args[1], "blank.pdf");
            File.WriteAllBytes(pdf, Fixture());
            Call(owner, "OpenFile", pdf);
            owner.Measure(new Size(1100, 800)); owner.Arrange(new Rect(0, 0, 1100, 800));
            var signature = Activator.CreateInstance(assembly.GetType("KillerPDF.SavedSignature")!)!;
            var strokes = (IList)signature.GetType().GetProperty("Strokes")!.GetValue(signature)!;
            var stroke = (IList)Activator.CreateInstance(strokes.GetType().GetGenericArguments()[0])!;
            foreach (var xy in new[] { (20d, 20d), (100d, 60d), (180d, 30d) })
            {
                var point = Activator.CreateInstance(assembly.GetType("KillerPDF.SerializablePoint")!)!;
                point.GetType().GetProperty("X")!.SetValue(point, xy.Item1);
                point.GetType().GetProperty("Y")!.SetValue(point, xy.Item2);
                stroke.Add(point);
            }
            strokes.Add(stroke);
            var store = Get(owner, "_signatureStore")!;
            Call(store, "Add", signature);
            Call(owner, "ToolSignature_Click", owner, new RoutedEventArgs());
            var popup = (DependencyObject)Get(owner, "_signaturePopup")!;
            var item = Tree(popup).OfType<Border>().Single(b => b.Height == 60 && b.Cursor == Cursors.Hand);
            var canvas = (Canvas)Get(owner, "_annotationCanvas")!;
            if (native)
            {
                Pump(500);
                var pageDeadline = DateTime.UtcNow.AddSeconds(10);
                do
                {
                    canvas = ((IEnumerable)Call(owner.FindName("Viewer"), "AllPageCanvasesExt")!).Cast<Canvas>()
                        .FirstOrDefault(c => c.IsVisible && c.ActualWidth > 0 && c.ActualHeight > 0 && c.Tag is int)!;
                    if (canvas is not null) break;
                    Pump(50);
                } while (DateTime.UtcNow < pageDeadline);
                if (canvas is null) throw new InvalidOperationException("No visible page overlay became ready.");
                NativeGesture(item, canvas, args.Contains("--native-drag"), args[1]);
            }
            else item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Console.WriteLine("Picker closed=" + (Get(owner, "_signaturePopup") is null));
            Console.WriteLine("Pending signature=" + ReferenceEquals(signature, Get(owner, "_pendingSignature")));
            Console.WriteLine("Tool=" + Get(owner, "_currentTool"));
            Console.WriteLine("Hint=" + ((TextBlock)owner.FindName("StatusText")).Text);
            var viewer = (FrameworkElement)owner.FindName("Viewer");
            bool drag = args.Contains("--drag");
            bool rejected = args.Contains("--outside") || args.Contains("--cancel") || args.Contains("--stale");
            if (args.Contains("--cancel"))
                ((Button)owner.FindName("ToolSelectBtn")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (args.Contains("--stale"))
            {
                string other = Path.Combine(args[1], "other.pdf"); File.WriteAllBytes(other, Fixture());
                Call(owner, "OpenFile", other);
            }
            if (native) { }
            else if (args.Contains("--drop") || rejected)
            {
                var data = new DataObject("KillerPDF.SignaturePlacement", signature);
                var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), M, null,
                    [data, DragDropKeyStates.None, DragDropEffects.Copy, canvas,
                        args.Contains("--outside") ? new Point(-80, -80) : new Point(80, 80)], null)!;
                drop.RoutedEvent = UIElement.PreviewDropEvent;
                canvas.Measure(new Size(600, 800)); canvas.Arrange(new Rect(0, 0, 600, 800));
                canvas.RaiseEvent(drop);
                Console.WriteLine("Drop handled=" + drop.Handled + " effects=" + drop.Effects);
            }
            else if (drag)
            {
                canvas.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseMoveEvent });
                canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            }
            else canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            var annotations = (IDictionary)Get(owner, "_annotations")!;
            int count = annotations.Values.Cast<IList>().Sum(list => list.Count);
            Console.WriteLine("Placed annotation count=" + count);
            int ink = native
                ? ((IEnumerable)Call(viewer, "AllPageCanvasesExt")!).Cast<Canvas>().Where(c => c.IsVisible)
                    .SelectMany(Tree).OfType<System.Windows.Shapes.Polyline>().Count()
                : Tree(canvas).OfType<System.Windows.Shapes.Polyline>().Count();
            Console.WriteLine("Gesture=" + args.Last());
            Console.WriteLine("Rendered ink shapes=" + ink);
            Console.WriteLine("Native handle=" + new System.Windows.Interop.WindowInteropHelper(owner).Handle);
            if (native)
            {
                var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(owner);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(args[1], "placed.png")); encoder.Save(file);
            }
            bool hintRemoved = !Tree(owner).OfType<Border>().Any(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "SignaturePlacementHint");
            Console.WriteLine("Placement hint removed=" + hintRemoved);
            return rejected ? (count == 0 && ink == 0 ? 0 : 1) : (count == 1 && ink > 0 && hintRemoved ? 0 : 1);
        }
        catch (Exception ex) { Console.WriteLine(ex); return 2; }
    }
    static void Pump(int milliseconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do { Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(10); }
        while (watch.ElapsedMilliseconds < milliseconds);
    }
    static void NativeGesture(FrameworkElement item, Canvas canvas, bool drag, string folder)
    {
        Point start = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
        Point finish = canvas.PointToScreen(new Point(canvas.ActualWidth * .25, canvas.ActualHeight * .6));
        var owner = Window.GetWindow(item)!;
        var hit = owner.InputHitTest(owner.PointFromScreen(new Point((int)start.X, (int)start.Y))) as DependencyObject;
        if (hit is null || (!ReferenceEquals(hit, item) && !item.IsAncestorOf(hit)))
            throw new InvalidOperationException("The signature picker is not under the intended native press.");
        bool hintSeen = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var hint = Tree(owner).OfType<Border>().FirstOrDefault(b => b.IsVisible && b.ActualWidth > 0
                && System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "SignaturePlacementHint");
            if (hint is null || hintSeen) return;
            hintSeen = true;
            var bitmap = new RenderTargetBitmap((int)owner.ActualWidth, (int)owner.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(owner);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, "placement-hint.png")); encoder.Save(stream);
        };
        timer.Start();
        var input = Task.Run(() =>
        {
            SetCursorPos((int)start.X, (int)start.Y); Thread.Sleep(100);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(100);
            if (!drag) { mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(200); }
            for (int step = 1; step <= 15; step++)
            {
                SetCursorPos((int)(start.X + (finish.X - start.X) * step / 15), (int)(start.Y + (finish.Y - start.Y) * step / 15));
                Thread.Sleep(40);
            }
            if (!drag) mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!input.IsCompleted && DateTime.UtcNow < deadline) Pump(20);
        if (!input.IsCompleted) throw new TimeoutException("Native gesture did not finish.");
        input.GetAwaiter().GetResult(); Pump(500);
        timer.Stop();
        if (!hintSeen) throw new InvalidOperationException("Placement guidance never appeared beside the page.");
    }
    static byte[] Fixture()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << >> >>"];
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
