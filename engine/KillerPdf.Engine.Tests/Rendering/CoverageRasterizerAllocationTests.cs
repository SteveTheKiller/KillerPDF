using System.Reflection;
using KillerPdf.Engine.Rendering;
using Xunit;
using P = KillerPdf.Engine.Rendering.PdfPageRenderer.Point;
using Mask = KillerPdf.Engine.Rendering.PdfPageRenderer.CoverageMask;

namespace KillerPdf.Engine.Tests.Rendering;

// Once warm, a rasterization allocates nothing apart from its output mask.
public sealed class CoverageRasterizerAllocationTests
{
    private static Func<Mask> Sweeper(List<P[]> polygons, int width, int height)
    {
        Type type = typeof(PdfPageRenderer).GetNestedType("CoverageRasterizer", BindingFlags.NonPublic)!;
        const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Instance;
        object instance = Activator.CreateInstance(type, f, null, [width, height], null)!;
        var reset = (Action)type.GetMethod("Reset", f, Type.EmptyTypes)!.CreateDelegate(typeof(Action), instance);
        var add = (Action<IEnumerable<P[]>>)type.GetMethod("AddPolygons", f)!.CreateDelegate(typeof(Action<IEnumerable<P[]>>), instance);
        var sweep = (Func<bool, bool, (int, int, int, int)?, Mask>)type.GetMethod("Sweep", f)!
            .CreateDelegate(typeof(Func<bool, bool, (int, int, int, int)?, Mask>), instance);
        return () => { reset(); add(polygons); return sweep(false, false, null); };
    }

    [Fact]
    public void OwnedPageMaskWithTrimmedEdgesAllocatesOnlyTheMask()
    {
        // A 2000-point star with seeded inner radii. Its thin spike tips leave edge rows and
        // columns at zero coverage, so the swept area is trimmed to the nonzero bounds.
        var random = new Random(3);
        var star = new P[2000];
        for (int i = 0; i < star.Length; i++)
        {
            double angle = 0.1 + 2 * Math.PI * i / star.Length, radius = 900;
            if ((i & 1) == 1) radius *= 0.35 + 0.3 * random.NextDouble();
            star[i] = new P(1000 + radius * Math.Cos(angle), 1000 + radius * Math.Sin(angle));
        }
        Func<Mask> run = Sweeper([star], 2048, 2048);
        for (int i = 0; i < 3; i++) run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Mask mask = run();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long maskBytes = mask.Coverage!.LongLength;
        Assert.True(allocated <= maskBytes + 4096,
            $"allocated {allocated} bytes for a {maskBytes}-byte mask ({mask.Width}x{mask.Height})");
    }
}
