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
        if (args.Length != 5) { Console.Error.WriteLine("Usage: RenderRegionCorpus APP_DLL INPUT_DIRECTORY OUTPUT_DIRECTORY SIZE PAGES"); return 2; }
        string app = Path.GetFullPath(args[0]), input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory to preserve earlier evidence.");
        Directory.CreateDirectory(output);
        int size = int.Parse(args[3]), pages = int.Parse(args[4]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string dll = Path.Combine(Path.GetDirectoryName(app)!, name.Name + ".dll");
            return File.Exists(dll) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll) : null;
        };
        Assembly assembly = Assembly.LoadFrom(app);
        string appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app)));
        Type type = assembly.GetType("KillerPDF.Services.PdfPageRenderSession", true)!;
        // Builds since 1.9.2 take the protection of the tab whose file it is; a corpus file is no tab's.
        Type protection = assembly.GetType("KillerPDF.Services.OpenedProtection", true)!;
        var open = type.GetMethod("OpenEngineFirst", Flags, [typeof(string), typeof(int), typeof(int), protection, typeof(bool)])!;
        var render = type.GetMethod("RenderFittedPage", Flags)!;
        var regionRender = type.GetMethod("RenderFittedRegion", Flags)!;
        var records = new List<object>();
        int inputFiles = 0, compared = 0, regions = 0, failures = 0, skips = 0;
        foreach (string file in Directory.EnumerateFiles(input, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            inputFiles++;
            string relative = Path.GetRelativePath(input, file);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            bool fullRendered = false;
            try
            {
                using var session = (IDisposable)open.Invoke(null, [file, size, size, null, false])!;
                int count = Math.Min(pages, (int)Value(session, "PageCount"));
                if (count == 0) throw new InvalidOperationException("The document has no renderable pages.");
                for (int p = 0; p < count; p++)
                {
                    object full = render.Invoke(session, [p, size, size, false, CancellationToken.None])!;
                    fullRendered = true;
                    int w = (int)Value(full, "Width"), h = (int)Value(full, "Height");
                    var pixels = (byte[])Value(full, "Pixels");
                    var rectangles = new[] { (0, 0, Math.Min(64, w), Math.Min(64, h)),
                        (w / 4, h / 4, Math.Max(1, w / 3), Math.Max(1, h / 3)),
                        (Math.Max(0, w - 64), Math.Max(0, h - 64), Math.Min(64, w), Math.Min(64, h)) };
                    foreach (var (x, y, rw, rh) in rectangles)
                    {
                        object crop = regionRender.Invoke(session, [p, size, size, x, y, rw, rh, CancellationToken.None])!;
                        var bytes = (byte[])Value(crop, "Pixels");
                        int different = 0;
                        var samples = new List<object>();
                        for (int row = 0; row < rh; row++)
                        for (int column = 0; column < rw; column++)
                            if (!pixels.AsSpan(((y + row) * w + x + column) * 4, 4)
                                .SequenceEqual(bytes.AsSpan((row * rw + column) * 4, 4)))
                            {
                                different++;
                                if (samples.Count < 20) samples.Add(new { x = x + column, y = y + row,
                                    full = Convert.ToHexString(pixels.AsSpan(((y + row) * w + x + column) * 4, 4)),
                                    region = Convert.ToHexString(bytes.AsSpan((row * rw + column) * 4, 4)) });
                            }
                        if (different != 0) failures++;
                        records.Add(new { file = relative, hash, page = p, w, h, x, y, rw, rh, differentPixels = different,
                            fullSha256 = Convert.ToHexString(SHA256.HashData(pixels)), regionSha256 = Convert.ToHexString(SHA256.HashData(bytes)), samples });
                        if (different > 0)
                        {
                            Save(Path.Combine(output, hash[..8] + $"-{p}-{x}-{y}-full.png"), w, h, pixels);
                            Save(Path.Combine(output, hash[..8] + $"-{p}-{x}-{y}-region.png"), rw, rh, bytes);
                        }
                        regions++;
                    }
                    compared++;
                }
            }
            catch (Exception ex)
            {
                if (fullRendered) failures++;
                else skips++;
                records.Add(new { file = relative, hash, status = fullRendered ? "region-error" : "unrenderable", error = (ex.InnerException ?? ex).ToString() });
            }
            string manifest = Path.Combine(output, "regions.json");
            if (File.Exists(manifest)) File.ReadAllText(manifest);
            File.WriteAllText(manifest, JsonSerializer.Serialize(new { appPath = app, inputPath = input, appSha256 = appHash,
                size, inputFiles, compared, regions, failures, skips, records }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Pages {compared}, regions {regions}, differing regions {failures}, unrenderable files {skips}");
        return failures == 0 && skips == 0 && compared > 0 ? 0 : 1;
    }

    private static void Save(string path, int width, int height, byte[] pixels)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
