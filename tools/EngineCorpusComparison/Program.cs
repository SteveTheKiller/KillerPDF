using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object Value(object target, string name) => target.GetType().GetProperty(name, Flags)!.GetValue(target)!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("Usage: EngineCorpusComparison APP_DLL INPUT OUTPUT SIZE PAGES [BASELINE_PNGS] [regions] [--passwords MANIFEST_JSON]"); return 2; }
        string app = Path.GetFullPath(args[0]), input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(output);
        int size = int.Parse(args[3]), pages = int.Parse(args[4]);
        bool regions = args.Contains("regions");
        string? baseline = args.Length > 5 && args[5] != "regions" && args[5] != "--passwords" ? args[5] : null;
        int passwordIndex = Array.IndexOf(args, "--passwords");
        string? passwordManifest = passwordIndex >= 0 ? Path.GetFullPath(args[passwordIndex + 1]) : null;
        var passwords = passwordManifest is null ? new Dictionary<string, string[]>()
            : JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(passwordManifest))!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string dll = Path.Combine(Path.GetDirectoryName(app)!, name.Name + ".dll");
            return File.Exists(dll) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll) : null;
        };
        Assembly assembly = Assembly.LoadFrom(app);
        Type type = assembly.GetType("KillerPDF.Services.PdfPageRenderSession", true)!;
        // Builds since 1.9.2 take the protection of the tab whose file it is; a corpus file is no tab's.
        Type? protection = assembly.GetType("KillerPDF.Services.OpenedProtection");
        var open = type.GetMethod("OpenEngineFirst", Flags, [typeof(string), typeof(int), typeof(int)])
            ?? type.GetMethod("OpenEngineFirst", Flags, [typeof(string), typeof(int), typeof(int), typeof(bool)])
            ?? (protection is null ? null
                : type.GetMethod("OpenEngineFirst", Flags, [typeof(string), typeof(int), typeof(int), protection, typeof(bool)]))
            ?? throw new MissingMethodException(type.FullName, "OpenEngineFirst");
        var authenticatedOpen = type.GetMethod("OpenEngineFirstWithPassword", Flags,
                [typeof(string), typeof(int), typeof(int), typeof(string)])
            ?? type.GetMethod("OpenEngineFirst", Flags, [typeof(string), typeof(int), typeof(int), typeof(string)]);
        var render = type.GetMethod("RenderFittedPage", Flags)!;
        var regionRender = type.GetMethod("RenderFittedRegion", Flags)!;
        var records = new List<object>();
        int inputFiles = 0, rendered = 0, failures = 0, regionFailures = 0;
        double parseMs = 0, rasterMs = 0;
        long allocated = GC.GetTotalAllocatedBytes(true);
        foreach (string file in Directory.EnumerateFiles(input, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            inputFiles++;
            string relative = Path.GetRelativePath(input, file);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            try
            {
                var timer = Stopwatch.StartNew();
                using var session = Open();
                parseMs += timer.Elapsed.TotalMilliseconds;
                int count = Math.Min(pages, (int)Value(session, "PageCount"));
                if (count == 0) throw new InvalidOperationException("The document has no renderable pages.");
                for (int p = 0; p < count; p++)
                {
                    timer.Restart();
                    object full = render.Invoke(session, [p, size, size, false, CancellationToken.None])!;
                    double elapsed = timer.Elapsed.TotalMilliseconds;
                    rasterMs += elapsed;
                    int w = (int)Value(full, "Width"), h = (int)Value(full, "Height");
                    var pixels = (byte[])Value(full, "Pixels");
                    string name = relative.Replace(Path.DirectorySeparatorChar, '_') + $"-page-{p + 1:D3}.png";
                    Save(Path.Combine(output, name), w, h, pixels);
                    object? comparison = null;
                    if (baseline is not null)
                    {
                        string old = Path.Combine(baseline, name);
                        if (!File.Exists(old)) comparison = new { status = "missing-baseline" };
                        else
                        {
                            var image = new BitmapImage();
                            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                            image.UriSource = new Uri(Path.GetFullPath(old)); image.EndInit();
                            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                            if (converted.PixelWidth != w || converted.PixelHeight != h)
                                comparison = new { status = "dimension-difference", baselineWidth = converted.PixelWidth, baselineHeight = converted.PixelHeight };
                            else
                            {
                                byte[] other = new byte[pixels.Length]; converted.CopyPixels(other, w * 4, 0);
                                int different = 0, significant = 0, maximumDelta = 0;
                                long absolute = 0;
                                byte[] difference = new byte[pixels.Length];
                                for (int i = 0; i < pixels.Length; i += 4)
                                {
                                    int delta = 0;
                                    for (int c = 0; c < 3; c++)
                                    {
                                        int d = Math.Abs(pixels[i + c] - other[i + c]);
                                        absolute += d; delta = Math.Max(delta, d);
                                        difference[i + c] = (byte)Math.Min(255, d * 8);
                                    }
                                    difference[i + 3] = 255;
                                    if (!pixels.AsSpan(i, 4).SequenceEqual(other.AsSpan(i, 4))) different++;
                                    if (delta > 8) significant++;
                                    maximumDelta = Math.Max(maximumDelta, delta);
                                }
                                comparison = new { status = different == 0 ? "exact" : "pixel-difference", different, significant,
                                    maximumDelta, meanAbsoluteChannelDelta = absolute / (w * (double)h * 3) };
                                if (different > 0) Save(Path.Combine(output, "diff-" + name), w, h, difference);
                            }
                        }
                    }
                    int cropDifferences = 0;
                    int cropMaximumDelta = 0;
                    var cropSamples = new List<object>();
                    if (regions)
                    {
                        foreach (var (x, y, rw, rh) in new[] { (0, 0, Math.Min(64, w), Math.Min(64, h)),
                            (w / 4, h / 4, Math.Max(1, w / 3), Math.Max(1, h / 3)),
                            (Math.Max(0, w - 64), Math.Max(0, h - 64), Math.Min(64, w), Math.Min(64, h)) })
                        {
                            object crop = regionRender.Invoke(session, [p, size, size, x, y, rw, rh, CancellationToken.None])!;
                            var bytes = (byte[])Value(crop, "Pixels");
                            for (int row = 0; row < rh; row++)
                            for (int column = 0; column < rw; column++)
                                if (!pixels.AsSpan(((y + row) * w + x + column) * 4, 4)
                                    .SequenceEqual(bytes.AsSpan((row * rw + column) * 4, 4)))
                                {
                                    cropDifferences++;
                                    var expected = pixels.AsSpan(((y + row) * w + x + column) * 4, 4);
                                    var actual = bytes.AsSpan((row * rw + column) * 4, 4);
                                    for (int c = 0; c < 4; c++) cropMaximumDelta = Math.Max(cropMaximumDelta, Math.Abs(expected[c] - actual[c]));
                                    if (cropSamples.Count < 20) cropSamples.Add(new { x = x + column, y = y + row,
                                        expected = Convert.ToHexString(expected), actual = Convert.ToHexString(actual) });
                                }
                        }
                        if (cropDifferences > 0) regionFailures++;
                    }
                    records.Add(new { file = relative, hash, page = p, name, width = w, height = h,
                        backend = Value(full, "Backend").ToString(), pixelsSha256 = Convert.ToHexString(SHA256.HashData(pixels)),
                        rasterMilliseconds = elapsed, diagnostics = Value(full, "EngineFailure"),
                        comparison, cropDifferences, cropMaximumDelta, cropSamples });
                    rendered++;
                }

                IDisposable Open()
                {
                    if (!passwords.TryGetValue(relative.Replace('\\', '/'), out string[]? candidates))
                        return (IDisposable)open.Invoke(null, open.GetParameters().Length switch
                        {
                            3 => [file, size, size],
                            4 => [file, size, size, false],
                            _ => [file, size, size, null, false]
                        })!;
                    if (authenticatedOpen is null) throw new NotSupportedException("The app has no authenticated engine boundary.");
                    Exception? last = null;
                    foreach (string password in candidates)
                    {
                        try { return (IDisposable)authenticatedOpen.Invoke(null, [file, size, size, password])!; }
                        catch (TargetInvocationException error) { last = error.InnerException ?? error; }
                    }
                    throw new InvalidOperationException("Published fixture credentials did not open the document.", last);
                }
            }
            catch (Exception ex)
            {
                failures++; records.Add(new { file = relative, hash, status = "failed", error = (ex.InnerException ?? ex).ToString() });
            }
        }
        var manifest = new { appPath = app, inputPath = input, baselinePath = baseline,
            appSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app))),
            passwordManifest, passwordManifestSha256 = passwordManifest is null ? null
                : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(passwordManifest))),
            size, inputFiles, rendered, failures,
            regions, regionFailures, parseMs, rasterMs, managedAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, records };
        File.WriteAllText(Path.Combine(output, "corpus.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Pages {rendered}, failed files {failures}, region failures {regionFailures}; parse {parseMs:F1}ms, raster {rasterMs:F1}ms");
        return rendered > 0 && failures == 0 && regionFailures == 0 ? 0 : 1;
    }

    private static void Save(string path, int width, int height, byte[] pixels)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
