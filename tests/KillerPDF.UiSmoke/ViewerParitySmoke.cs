using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class ViewerParitySmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private sealed record Input(string Path, string Sha256, string Category);
    private sealed record Manifest(string Root, string AppXaml, string AppXamlSha256, Input[] Documents);
    private sealed record Result(string Path, string Category, string InputSha256,
        int Width, int Height, string ViewerSha256, string HeadlessSha256, string? Error);

    internal static int Run(string appPath, string manifestPath, string output)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke"))
            throw new InvalidOperationException("Startup smoke isolation is required.");
        if (Directory.Exists(output)) throw new IOException("Use a fresh evidence directory.");
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing parity manifest.");
        if (manifest.Documents.Length == 0) throw new InvalidDataException("No parity inputs.");
        if (!Hash(File.ReadAllBytes(manifest.AppXaml)).Equals(manifest.AppXamlSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Application resource source hash changed.");
        string root = Path.GetFullPath(manifest.Root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(output);
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string dll = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(dll) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(output, "data"));
        AccessibilityReviewSmoke.ConfigureOffscreen(assembly);
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        TypeBuilder host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ViewerParityHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder startup = host.DefineMethod("OnStartup",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(host.CreateType()!)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument sourceXaml = XDocument.Load(manifest.AppXaml);
        XElement resources = sourceXaml.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (XAttribute declaration in sourceXaml.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        string appName = assembly.GetName().Name!;
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=" + appName + "\"",
                StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri($"pack://application:,,,/{appName};component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        appType.GetMethod("SetSetting", Members)!.Invoke(null, ["RememberChoiceLocked", "1"]);
        appType.GetMethod("SetSetting", Members)!.Invoke(null, ["CloseToTray", "0"]);
        // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
        assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
        foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
            assembly.GetType("KillerPDF.Services." + service, true)!
                .GetMethod("Initialize", Members)!.Invoke(null, null);
        Console.WriteLine("PARITY application resources initialized.");
        var results = new List<Result>();
        try
        {
            for (int index = 0; index < manifest.Documents.Length; index++)
            {
                Input input = manifest.Documents[index];
                string source = Path.GetFullPath(Path.Combine(root, input.Path));
                string folder = Path.Combine(output, $"case-{index + 1:D2}");
                Directory.CreateDirectory(folder);
                Window? window = null;
                int width = 0, height = 0;
                string actualHash = "", expectedHash = "";
                try
                {
                    if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Parity input escaped its corpus root.");
                    if (!Hash(File.ReadAllBytes(source)).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Parity input hash changed.");
                    Console.WriteLine($"PARITY case {index + 1}: constructing window for {input.Path}");
                    window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -10000; window.Top = -10000;
                    window.ShowInTaskbar = false; window.ShowActivated = false;
                    window.Width = 1000; window.Height = 650;
                    window.Show();
                    Console.WriteLine($"PARITY case {index + 1}: opening document.");
                    window.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(window, [source]);
                    Wait(() => window.GetType().GetProperty("_doc", Members)!.GetValue(window) is not null,
                        "loaded viewer document");
                    object viewer = Member(window, "ActiveViewer");
                    object state = Member(viewer, "State");
                    var single = new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F6)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    window.RaiseEvent(single);
                    try
                    {
                        Wait(() => Member(state, "Mode").ToString() == "Single"
                            && (int)Member(state, "RenderedPrimaryPage") == 0
                            && Member(viewer, "PrimaryRenderTask") is Task { IsCompletedSuccessfully: true }
                            && ((Image)Member(state, "PageImage")).Source is BitmapSource,
                            "completed primary viewer render");
                    }
                    catch (TimeoutException error)
                    {
                        throw new TimeoutException("Completed primary viewer render: mode=" + Member(state, "Mode")
                            + ", page=" + Member(state, "RenderedPrimaryPage")
                            + ", task=" + ((Task)Member(viewer, "PrimaryRenderTask")).Status
                            + ", image=" + ((Image)Member(state, "PageImage")).Source?.GetType().Name
                            + ", modifiers=" + Keyboard.Modifiers, error);
                    }
                    var bitmap = (BitmapSource)((Image)Member(state, "PageImage")).Source;
                    width = bitmap.PixelWidth; height = bitmap.PixelHeight;
                    Console.WriteLine($"PARITY case {index + 1}: viewer bitmap {bitmap.PixelWidth}x{bitmap.PixelHeight}.");
                    byte[] actual = Pixels(bitmap);
                    actualHash = Hash(actual);
                    Save(bitmap, Path.Combine(folder, "viewer.png"));
                    string inputFolder = Path.Combine(folder, "input");
                    string headlessFolder = Path.Combine(folder, "headless");
                    Directory.CreateDirectory(inputFolder);
                    Directory.CreateDirectory(headlessFolder);
                    File.Copy(source, Path.Combine(inputFolder, "source.pdf"));
                    var start = new ProcessStartInfo(Path.ChangeExtension(appPath, ".exe"))
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    foreach (string arg in new[] { "--batch-render", inputFolder, headlessFolder,
                        "--size", Math.Max(bitmap.PixelWidth, bitmap.PixelHeight).ToString(),
                        "--pages", "1", "--parallel", "1", "--quiet",
                        "--log", Path.Combine(folder, "headless.csv") }) start.ArgumentList.Add(arg);
                    string profile = Path.Combine(folder, "cli-profile");
                    Directory.CreateDirectory(profile);
                    string marker = Path.Combine(profile, "launcher.marker");
                    File.WriteAllText(marker, "");
                    start.Environment["KILLERPDF_LAUNCHER_PATH"] = marker;
                    using var process = Process.Start(start) ?? throw new IOException("Headless process did not start.");
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    try { Wait(() => process.HasExited, "headless render", 60000); }
                    finally { if (!process.HasExited) process.Kill(true); }
                    File.WriteAllText(Path.Combine(folder, "headless.stdout.txt"), stdout.GetAwaiter().GetResult());
                    File.WriteAllText(Path.Combine(folder, "headless.stderr.txt"), stderr.GetAwaiter().GetResult());
                    if (process.ExitCode != 0) throw new InvalidDataException("Headless render exit " + process.ExitCode);
                    string png = Directory.EnumerateFiles(headlessFolder, "*.png", SearchOption.AllDirectories).Single();
                    BitmapFrame expected = BitmapFrame.Create(new Uri(png), BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (expected.PixelWidth != bitmap.PixelWidth || expected.PixelHeight != bitmap.PixelHeight)
                        throw new InvalidDataException($"Viewer {width}x{height} and headless {expected.PixelWidth}x{expected.PixelHeight} dimensions differ.");
                    byte[] expectedPixels = Pixels(expected);
                    expectedHash = Hash(expectedPixels);
                    if (!actual.AsSpan().SequenceEqual(expectedPixels))
                        throw new InvalidDataException($"Viewer pixels differ: {actualHash} versus {expectedHash}.");
                    if (!Hash(File.ReadAllBytes(source)).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Parity workflow changed its input.");
                    results.Add(new(input.Path, input.Category, input.Sha256, bitmap.PixelWidth,
                        bitmap.PixelHeight, actualHash, expectedHash, null));
                    Console.WriteLine("PASS " + input.Path + " " + bitmap.PixelWidth + "x" + bitmap.PixelHeight);
                }
                catch (Exception error)
                {
                    results.Add(new(input.Path, input.Category, input.Sha256, width, height, actualHash, expectedHash, error.ToString()));
                    Console.Error.WriteLine("FAIL " + input.Path + ": " + error.Message);
                }
                finally
                {
                    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                    {
                        appPath, appSha256 = Hash(File.ReadAllBytes(appPath)),
                        manifestSha256 = Hash(File.ReadAllBytes(manifestPath)),
                        appXamlSha256 = Hash(File.ReadAllBytes(manifest.AppXaml)),
                        expectedDocuments = manifest.Documents.Length, results,
                        scope = "Primary viewer bitmap versus independent headless CLI at the same pixel size; no overlay or physical-display acceptance."
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"PARITY case {index + 1}: closing owned window.");
                    window?.Close();
                }
            }
            return results.All(result => result.Error is null) ? 0 : 1;
        }
        finally { app.Shutdown(); }
    }

    private static object Member(object target, string name) =>
        target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new MissingMemberException(target.GetType().FullName, name);

    private static void Wait(Func<bool> predicate, string stage, int milliseconds = 30000)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException(stage);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static byte[] Pixels(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }
}
