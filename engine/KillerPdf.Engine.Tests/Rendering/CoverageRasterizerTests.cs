using System.Reflection;
using KillerPdf.Engine.Rendering;
using Xunit;
using Xunit.Abstractions;
using Point = KillerPdf.Engine.Rendering.PdfPageRenderer.Point;
using Mask = KillerPdf.Engine.Rendering.PdfPageRenderer.CoverageMask;

namespace KillerPdf.Engine.Tests.Rendering;

// Checks the coverage rasterizer against an independent analytic reference. The reference
// quantizes every vertex to 1/256 pixel, clips each polygon to each pixel square with
// Sutherland-Hodgman and sums the shoelace areas, which is the exact integral of the winding
// number over the pixel. It then applies the fill rule and the byte map min(255, floor(256c)).
public sealed class CoverageRasterizerTests(ITestOutputHelper output)
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public
        | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type RasterizerType =
        typeof(PdfPageRenderer).GetNestedType("CoverageRasterizer", BindingFlags.NonPublic)!;

    [Fact]
    public void CoverageMatchesTheExactWindingIntegralWithinOneLevel()
    {
        int maximum = 0, edgePixels = 0, interiorMisses = 0, cases = 0;
        double totalError = 0;
        var failures = new List<string>();
        foreach ((string name, int width, int height, Point[][] polygons) in AnalyticCases())
        foreach (bool evenOdd in new[] { false, true })
        {
            cases++;
            byte[] actual = Pixels(Rasterize(width, height, polygons, evenOdd), width, height);
            (byte[] expected, bool[] crossed) = Model(width, height, polygons, evenOdd);
            for (int index = 0; index < actual.Length; index++)
            {
                int error = Math.Abs(actual[index] - expected[index]);
                if (!crossed[index])
                {
                    if (error != 0)
                    {
                        interiorMisses++;
                        if (failures.Count < 20)
                            failures.Add($"{name} evenOdd={evenOdd} interior pixel ({index % width}, {index / width}): {actual[index]} vs {expected[index]}");
                    }
                    continue;
                }
                edgePixels++;
                totalError += error;
                maximum = Math.Max(maximum, error);
                if (error > 1 && failures.Count < 20)
                    failures.Add($"{name} evenOdd={evenOdd} pixel ({index % width}, {index / width}): {actual[index]} vs model {expected[index]}");
            }
        }
        output.WriteLine($"cases={cases} edgePixels={edgePixels} maxError={maximum} meanError={totalError / Math.Max(1, edgePixels):F4} interiorMisses={interiorMisses}");
        Assert.True(cases > 100);
        Assert.True(edgePixels > 1000);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Small rasters accumulate into a full cell grid and large ones sweep stored edges row by
    // row. A raster made taller than the grid limit must give the same bytes in the rows both
    // rasters share, since rows below the raster never affect the rows above.
    [Fact]
    public void SmallAndLargeRastersGiveTheSameRows()
    {
        int compared = 0;
        foreach ((string name, int width, int height, Point[][] polygons) in AnalyticCases())
        foreach (bool evenOdd in new[] { false, true })
        {
            Mask small = Rasterize(width, height, polygons, evenOdd);
            Mask large = Rasterize(width, height + 5000, polygons, evenOdd);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.True(small.At(x, y) == large.At(x, y),
                    $"{name} evenOdd={evenOdd} ({x}, {y}): small {small.At(x, y)} vs large {large.At(x, y)}");
            compared++;
        }
        Assert.True(compared > 200);
    }

    // Geometry abandoned by Reset, or left behind by an earlier sweep, never reaches a later mask.
    [Theory]
    [InlineData(40, 30)]
    [InlineData(300, 200)]
    public void ResetDiscardsEarlierGeometry(int width, int height)
    {
        Point[] star = Star(20.3, 14.6, 13.1, 7, 3), circle = Circle(17.7, 13.2, 9.4, 40);
        Mask fresh = Rasterize(width, height, [circle], false);
        object reused = Create(width, height);
        Invoke(reused, "AddPolygons", new object?[] { new[] { star } });
        Invoke(reused, "Reset", []);
        Invoke(reused, "AddPolygons", new object?[] { new[] { circle } });
        AssertSameMask(fresh, Sweep(reused, false), 0, 0, "after reset");
        Invoke(reused, "AddPolygons", new object?[] { new[] { star } });
        Sweep(reused, true, false, (2, 2, 9, 9));
        Invoke(reused, "Reset", [width, height]);
        Invoke(reused, "AddPolygons", new object?[] { new[] { circle } });
        AssertSameMask(fresh, Sweep(reused, false), 0, 0, "after a bounded sweep and a sized reset");
        Assert.True(Sweep(reused, false).IsEmpty);
    }

    // Bounds limit only the output: every pixel inside them keeps its unbounded value.
    [Fact]
    public void BoundedSweepsKeepTheFullRasterCoverageInsideTheBounds()
    {
        var random = new Random(1108);
        int compared = 0;
        foreach ((string name, int width, int height, Point[][] polygons) in AnalyticCases().Take(120))
        foreach (bool evenOdd in new[] { false, true })
        foreach (bool rent in new[] { false, true })
        {
            byte[] full = Pixels(Rasterize(width, height, polygons, evenOdd), width, height);
            int left = random.Next(-3, width), top = random.Next(-3, height);
            (int, int, int, int) bounds = (left, top, left + random.Next(1, width), top + random.Next(1, height));
            Mask mask = Rasterize(width, height, polygons, evenOdd, rent, bounds);
            if (!mask.IsEmpty)
            {
                Assert.True(mask.Left >= Math.Max(0, bounds.Item1) && mask.Top >= Math.Max(0, bounds.Item2)
                    && mask.Right <= Math.Min(width, bounds.Item3) && mask.Bottom <= Math.Min(height, bounds.Item4),
                    $"{name}: mask bounds leave the requested bounds.");
            }
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool inside = x >= bounds.Item1 && x < bounds.Item3 && y >= bounds.Item2 && y < bounds.Item4;
                byte expected = inside ? full[y * width + x] : (byte)0;
                if (inside) compared++;
                Assert.True(expected == mask.At(x, y),
                    $"{name} evenOdd={evenOdd} rent={rent} bounds={bounds} pixel ({x}, {y}): {mask.At(x, y)} vs {expected}");
            }
            mask.Return();
        }
        Assert.True(compared > 10000);
    }

    // Reversal, whole-pixel translation and collinear subdivision of an axis-aligned edge leave
    // every byte and the bounds unchanged.
    [Fact]
    public void ReversedTranslatedAndSubdividedPolygonsGiveIdenticalMasks()
    {
        int checkedCases = 0, translated = 0;
        foreach ((string name, int width, int height, Point[][] polygons) in AnalyticCases())
        foreach (bool evenOdd in new[] { false, true })
        {
            Mask original = Rasterize(width, height, polygons, evenOdd);
            Point[][] reversed = [.. polygons.Select(polygon => polygon.Reverse().ToArray())];
            AssertSameMask(original, Rasterize(width, height, reversed, evenOdd), 0, 0, $"{name} reversed");
            checkedCases++;
            // Translation is exact away from the raster edge, where clipping takes over.
            if (!polygons.All(polygon => polygon.All(p => p.X >= 0 && p.X <= width && p.Y >= 0 && p.Y <= height)))
                continue;
            Point[][] moved = [.. polygons.Select(polygon => polygon.Select(p => new Point(p.X + 7, p.Y + 5)).ToArray())];
            AssertSameMask(original, Rasterize(width + 20, height + 20, moved, evenOdd), 7, 5, $"{name} translated");
            translated++;
        }
        // A rectangle with its edges split at quantized midpoints, in both directions.
        Point[] rectangle = [new(3.125, 2.375), new(27.5 + 1 / 256.0, 2.375), new(27.5 + 1 / 256.0, 19.8), new(3.125, 19.8)];
        var subdivided = new List<Point>();
        for (int index = 0; index < 4; index++)
        {
            Point a = rectangle[index], b = rectangle[(index + 1) % 4];
            for (int step = 0; step < 7; step++)
                subdivided.Add(new(a.X + (b.X - a.X) * step / 7, a.Y + (b.Y - a.Y) * step / 7));
        }
        foreach (bool evenOdd in new[] { false, true })
        {
            Mask plain = Rasterize(40, 30, [rectangle], evenOdd);
            AssertSameMask(plain, Rasterize(40, 30, [subdivided.ToArray()], evenOdd), 0, 0, "subdivided");
            AssertSameMask(plain, Rasterize(40, 30, [[.. Enumerable.Reverse(subdivided)]], evenOdd), 0, 0, "subdivided reversed");
        }
        Assert.True(checkedCases > 100);
        Assert.True(translated > 40);
    }

    // A polygon with a NaN or infinite coordinate anywhere adds nothing; the rest still render.
    [Fact]
    public void NonFinitePolygonsAddNothingWhileTheOthersRender()
    {
        Point[] kept = [new(3.3, 2.1), new(30.7, 9.4), new(12.2, 25.8)];
        int checkedCases = 0;
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        foreach (int position in new[] { 0, 2, 4 })
        foreach (bool offRasterFirst in new[] { false, true })
        foreach (bool evenOdd in new[] { false, true })
        {
            Point[] broken = [new(offRasterFirst ? -900.5 : 4.5, 3.5), new(36.25, 1.75),
                new(38.5, 27.5), new(20.1, 29.4), new(1.5, 28.2)];
            broken[position] = position == 2 ? new(bad, 14.5) : new(17.5, bad);
            Mask expected = Rasterize(40, 30, [kept], evenOdd);
            AssertSameMask(expected, Rasterize(40, 30, [broken, kept], evenOdd), 0, 0,
                $"bad={bad} position={position} offRasterFirst={offRasterFirst}");
            AssertSameMask(expected, Rasterize(40, 30, [kept, broken], evenOdd), 0, 0,
                $"bad={bad} position={position} offRasterFirst={offRasterFirst} last");
            checkedCases++;
        }
        Assert.Equal(36, checkedCases);
    }

    // Far-off vertices keep the on-raster coverage. A band from y = 2.3 to 7.6 quantizes to
    // 589 and 1946 units: row 2 covers 768 - 589 = 179 units, row 7 covers 1946 - 1792 = 154.
    [Theory]
    [InlineData(1e6)]
    [InlineData(1e12)]
    [InlineData(1e15)]
    [InlineData(1e300)]
    public void HugeCoordinatesKeepTheVisibleCoverage(double far)
    {
        byte[] expectedRows = [0, 0, 179, 255, 255, 255, 255, 154, 0, 0, 0, 0];
        foreach (bool evenOdd in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        {
            Point[] band = [new(-far, 2.3), new(far, 2.3), new(far, 7.6), new(-far, 7.6)];
            if (reverse) Array.Reverse(band);
            byte[] pixels = Pixels(Rasterize(20, 12, [band], evenOdd), 20, 12);
            for (int y = 0; y < 12; y++)
            for (int x = 0; x < 20; x++)
                Assert.True(expectedRows[y] == pixels[y * 20 + x], $"far={far} ({x}, {y}): {pixels[y * 20 + x]} vs {expectedRows[y]}");
        }
        // A wedge whose far vertices are out of reach still paints nothing below its diagonal.
        foreach (bool evenOdd in new[] { false, true })
        {
            byte[] wedge = Pixels(Rasterize(20, 12, [[new(0, 0), new(far, far), new(0, far)]], evenOdd), 20, 12);
            for (int y = 0; y < 12; y++)
            for (int x = y + 2; x < 20; x++)
                Assert.True(wedge[y * 20 + x] == 0, $"far={far} wedge paints ({x}, {y}).");
            Assert.True(far > 1e15 || wedge[5 * 20 + 1] == 255, $"far={far} wedge interior missing.");
        }
    }

    // The scalar glyph entry point quantizes ((X*a + Y*c) + offsetX - shiftX) * 256 with ties to
    // even; the integer entry point takes those integers directly, plus fixed-unit offsets.
    [Fact]
    public void GlyphEntryPointsAgreeForTheSameIntegerVertices()
    {
        Point[][] glyph = [Circle(500, 480, 410, 40), [.. Circle(500, 480, 190, 24).Reverse()],
            Star(520, 300, 260, 5, 2)];
        int compared = 0;
        foreach ((double a, double b, double c, double d) in new[] {
            (0.0213, 0.0, 0.0, -0.0213), (0.0151, 0.0042, -0.0037, -0.0166), (0.009, 0.0, 0.0, -0.011) })
        for (int phase = 0; phase < 16; phase++)
        {
            double offsetX = (phase % 4) / 4.0, offsetY = (phase / 4) / 4.0;
            const int shiftX = -2, shiftY = -24, width = 30, height = 30;
            var integer = new PdfPageRenderer.GlyphPoint[glyph.Length][];
            var pixels = new Point[glyph.Length][];
            for (int path = 0; path < glyph.Length; path++)
            {
                integer[path] = new PdfPageRenderer.GlyphPoint[glyph[path].Length];
                pixels[path] = new Point[glyph[path].Length];
                for (int index = 0; index < glyph[path].Length; index++)
                {
                    Point p = glyph[path][index];
                    double dx = p.X * a + p.Y * c + offsetX, dy = p.X * b + p.Y * d + offsetY;
                    int ix = (int)Math.Round((dx - shiftX) * 256), iy = (int)Math.Round((dy - shiftY) * 256);
                    // Integer vertices minus the offsets the integer entry point adds back.
                    integer[path][index] = new(ix - 64 * (phase % 4) - 256, iy - 64 * (phase / 4) + 512);
                    pixels[path][index] = new(ix / 256.0, iy / 256.0);
                }
            }
            object scalarRasterizer = Create(width, height);
            Invoke(scalarRasterizer, "Reset", [width, height]);
            Invoke(scalarRasterizer, "AddGlyphPolygons", [glyph, a, b, c, d, offsetX, offsetY, shiftX, shiftY]);
            Mask scalar = Sweep(scalarRasterizer, false);
            object integerRasterizer = Create(4, 4);
            Invoke(integerRasterizer, "Reset", [width, height]);
            Invoke(integerRasterizer, "AddDeviceGlyphPolygons", [integer, 64 * (phase % 4) + 256, 64 * (phase / 4) - 512]);
            Mask device = Sweep(integerRasterizer, false);
            Assert.False(scalar.IsEmpty);
            AssertSameMask(scalar, device, 0, 0, $"matrix {a} phase {phase} integer");
            AssertSameMask(scalar, Rasterize(width, height, pixels, false), 0, 0, $"matrix {a} phase {phase} pixels");
            compared++;
        }
        Assert.Equal(48, compared);
    }

    // Page-space polygons map through the frame exactly as RasterFrame.ToPixels does.
    [Fact]
    public void PagePolygonsMatchTheirPixelConversion()
    {
        Type frameType = typeof(PdfPageRenderer).GetNestedType("RasterFrame", BindingFlags.NonPublic)!;
        object frame = Activator.CreateInstance(frameType, 50, 40, 0.75, 1.25)!;
        MethodInfo toPixels = frameType.GetMethod("ToPixels", Members, null,
            [typeof(IReadOnlyList<List<Point>>), typeof(int)], null)!;
        List<List<Point>> paths =
        [
            [new(1.3, 2.2)],
            [.. Circle(30.1, 15.3, 11.7, 33)],
            [new(4.4, 4.4), new(9.1, 2.0)],
            [.. Star(35.7, 17.1, 9.3, 7, 3)],
            [new(-20, -3), new(80, 5.5), new(10, 50.25)],
        ];
        foreach (bool evenOdd in new[] { false, true })
        {
            object page = Create(50, 40);
            Invoke(page, "Reset", []);
            Invoke(page, "AddPagePolygons", [paths, frame]);
            Mask fromPage = Sweep(page, evenOdd);
            var pixels = (IEnumerable<Point[]>)toPixels.Invoke(frame, [paths, 3])!;
            Assert.False(fromPage.IsEmpty);
            AssertSameMask(Rasterize(50, 40, pixels, evenOdd), fromPage, 0, 0, $"page evenOdd={evenOdd}");
        }
    }

    // The rectangle fast path returns the null-coverage form for pixel-aligned rectangles, the
    // three-row form for tall fractional ones, and otherwise matches the general path exactly.
    [Fact]
    public void RectangleFastPathMatchesTheGeneralPath()
    {
        const int width = 40, height = 30;
        var random = new Random(4092);
        int fullForms = 0, compactForms = 0, compared = 0;
        var cases = new List<Point[]>
        {
            Box(3, 4, 21, 17), Box(-5, -6, 60, 70), Box(10, 10, 10, 20), Box(2.5, 3.25, 30.75, 25.5),
            Box(-1e9, 2.3, 1e9, 7.6), Box(0.001, 1.999, 0.002, 28.001), Box(4.25, 5.5, 9.75, 6.75),
        };
        for (int index = 0; index < 120; index++)
        {
            double step = index % 3 == 0 ? 0.25 : index % 3 == 1 ? 1 / 256.0 : 1 / 512.0;
            double x0 = Math.Round(random.Next(-20, 50) / step) * step + (index % 5 == 0 ? random.NextDouble() : 0);
            double y0 = Math.Round(random.Next(-20, 40) / step) * step;
            cases.Add(Box(x0, y0, x0 + random.Next(0, 30) + random.NextDouble() * (index % 2),
                y0 + random.Next(0, 25) + random.NextDouble() * (index % 4 == 0 ? 0 : 1)));
        }
        foreach (Point[] box in cases)
        foreach (int variant in new[] { 0, 1, 2 })
        {
            Point[] polygon = variant switch
            {
                0 => box,
                1 => [.. box.Reverse()],
                _ => [.. box, box[0]],
            };
            var fast = (Mask?)Invoke(Create(1, 1), "TryRectangle", [polygon, width, height]);
            bool aligned = box.All(p => Math.Round(p.X * 256) % 256 == 0 && Math.Round(p.Y * 256) % 256 == 0);
            Mask general = Rasterize(width, height, [polygon], false);
            if (aligned)
            {
                Assert.NotNull(fast);
                Assert.True(fast!.IsEmpty || fast.Coverage is null, "Aligned rectangles use the null-coverage form.");
                fullForms++;
            }
            if (fast is null) continue;
            if (fast.Coverage is not null && fast.Height > 3)
            {
                Assert.Equal(3 * fast.Width, fast.Coverage.Length);
                compactForms++;
            }
            AssertSameMask(general, fast, 0, 0, $"rectangle ({box[0]}, {box[2]}) variant {variant}");
            compared++;
        }
        foreach (Point[] bad in new Point[][] {
            [new(double.NaN, 1), new(5, 1), new(5, 6), new(1, 6)],
            [new(1, 1), new(double.PositiveInfinity, 1), new(5, 6), new(1, 6)] })
            Assert.Null(Invoke(Create(1, 1), "TryRectangle", [bad, width, height]));
        Assert.True(fullForms > 6 && compactForms > 20 && compared > 200,
            $"full={fullForms} compact={compactForms} compared={compared}");

        static Point[] Box(double left, double top, double right, double bottom) =>
            [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
    }

    // The renderer's fill entry points route through the coverage rasterizer.
    [Fact]
    public void RendererFillEntryPointsMatchTheCoverageRasterizer()
    {
        MethodInfo polygonsMethod = typeof(PdfPageRenderer).GetMethod("RasterizePolygons", Members)!;
        Type frameType = typeof(PdfPageRenderer).GetNestedType("RasterFrame", BindingFlags.NonPublic)!;
        MethodInfo fillMethod = typeof(PdfPageRenderer).GetMethod("RasterizeFill", Members, null,
            [typeof(IReadOnlyList<List<Point>>), typeof(bool), frameType, typeof(bool), typeof((int, int, int, int)?)], null)!;
        object frame = Activator.CreateInstance(frameType, 64, 48, 1.0, 1.0)!;
        foreach ((string name, int width, int height, Point[][] polygons) in AnalyticCases())
        {
            if (width != 64 || height != 48) continue;
            foreach (bool evenOdd in new[] { false, true })
            {
                Mask direct = Rasterize(width, height, polygons, evenOdd);
                var viaPolygons = (Mask)polygonsMethod.Invoke(null, [polygons.ToList(), evenOdd, width, height, false, null])!;
                AssertSameMask(direct, viaPolygons, 0, 0, $"{name} RasterizePolygons");
                List<List<Point>> page = [.. polygons.Select(polygon => polygon.Select(p => new Point(p.X, 48 - p.Y)).ToList())];
                var viaFill = (Mask)fillMethod.Invoke(null, [page, evenOdd, frame, false, null])!;
                AssertSameMask(direct, viaFill, 0, 0, $"{name} RasterizeFill");
            }
        }
    }

    internal static void AssertSameMask(Mask expected, Mask actual, int dx, int dy, string label)
    {
        Assert.True(expected.IsEmpty == actual.IsEmpty, $"{label}: emptiness differs.");
        if (expected.IsEmpty) return;
        Assert.True(expected.Left + dx == actual.Left && expected.Top + dy == actual.Top
            && expected.Right + dx == actual.Right && expected.Bottom + dy == actual.Bottom,
            $"{label}: bounds ({expected.Left}, {expected.Top}, {expected.Right}, {expected.Bottom}) vs ({actual.Left}, {actual.Top}, {actual.Right}, {actual.Bottom})");
        for (int y = expected.Top; y < expected.Bottom; y++)
        for (int x = expected.Left; x < expected.Right; x++)
            Assert.True(expected.At(x, y) == actual.At(x + dx, y + dy),
                $"{label}: pixel ({x}, {y}) {expected.At(x, y)} vs {actual.At(x + dx, y + dy)}");
    }

    // Shapes for the analytic comparison: triangles, rectangles, polygonal circles, stars that
    // cross themselves, nested and overlapping subpaths, slivers and subpixel offsets.
    private static IEnumerable<(string, int, int, Point[][])> AnalyticCases()
    {
        var random = new Random(26408);
        double Next(double low, double high) => low + random.NextDouble() * (high - low);
        for (int index = 0; index < 60; index++)
            yield return ($"triangle {index}", 64, 48, [[new(Next(-8, 72), Next(-8, 56)),
                new(Next(-8, 72), Next(-8, 56)), new(Next(-8, 72), Next(-8, 56))]]);
        for (int index = 0; index < 30; index++)
        {
            double left = Next(-4, 40), top = Next(-4, 30), right = left + Next(0.01, 30), bottom = top + Next(0.01, 20);
            Point[] rectangle = [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];
            yield return ($"rectangle {index}", 64, 48, [index % 2 == 0 ? rectangle : [.. rectangle.Reverse()]]);
        }
        yield return ("circle", 64, 48, [Circle(31.37, 22.81, 17.3, 96)]);
        yield return ("small circle", 7, 5, [Circle(3.4, 2.6, 0.7, 24)]);
        yield return ("circle with hole", 64, 48, [Circle(30.2, 24.1, 20.4, 128),
            [.. Circle(30.2, 24.1, 9.85, 64).Reverse()]]);
        yield return ("circle with same direction inner", 64, 48, [Circle(30.2, 24.1, 20.4, 128),
            Circle(33.7, 21.9, 9.85, 64)]);
        yield return ("star", 64, 48, [Star(32.3, 24.6, 22.1, 5, 2)]);
        yield return ("seven star", 64, 48, [Star(31.9, 23.2, 22.7, 7, 3)]);
        yield return ("overlapping squares", 64, 48, [
            [new(5.3, 6.1), new(40.7, 6.1), new(40.7, 30.2), new(5.3, 30.2)],
            [new(20.1, 15.9), new(55.6, 15.9), new(55.6, 44.4), new(20.1, 44.4)]]);
        yield return ("diagonal sliver", 64, 48, [[new(2.1, 3.3), new(60.4, 41.9), new(60.403, 41.9)]]);
        yield return ("vertical sliver", 64, 48, [[new(20.5, 2), new(20.501, 2), new(20.501, 40), new(20.5, 40)]]);
        yield return ("near horizontal", 64, 48, [[new(-3, 10.25), new(70, 10.25 + 73e-6), new(70, 30.6), new(-3, 30.6)]]);
        yield return ("near vertical", 64, 48, [[new(12.75, -3), new(12.75 + 51e-6, 51), new(50.2, 51), new(50.2, -3)]]);
        for (int step = 0; step < 8; step++)
        {
            double offset = step / 8.0;
            yield return ($"offset triangle {step}", 32, 24, [[new(3.1 + offset, 2.2 + offset),
                new(27.6 + offset, 9.4 + offset * 0.5), new(11.3 + offset * 0.25, 20.9 + offset)]]);
        }
        foreach (double offset in new[] { 1 / 256.0, 1 / 512.0, 3 / 512.0, 1e-5 })
            yield return ($"tiny offset {offset}", 32, 24, [[new(4 + offset, 3 + offset),
                new(20 + offset, 3 + offset), new(12 + offset, 19.5 + offset)]]);
        Point[] tangle = new Point[2000];
        for (int index = 0; index < tangle.Length; index++)
            tangle[index] = new(Next(1, 63), Next(1, 47));
        yield return ("2000 vertex tangle", 64, 48, [tangle]);
        Point[] spiral = new Point[600];
        for (int index = 0; index < spiral.Length; index++)
        {
            double angle = index * 0.173, radius = 2 + index * 0.035;
            spiral[index] = new(32.2 + radius * Math.Cos(angle), 24.3 + radius * Math.Sin(angle));
        }
        yield return ("spiral", 64, 48, [spiral]);
        yield return ("off raster", 40, 30, [[new(-500.3, -20.7), new(25.4, 12.6), new(-80.2, 400.1)],
            [new(1e4, 5.5), new(30.3, 28.9), new(39.2, -1e3)]]);
    }

    internal static Point[] Circle(double centerX, double centerY, double radius, int count)
    {
        var points = new Point[count];
        for (int index = 0; index < count; index++)
        {
            double angle = 2 * Math.PI * index / count;
            points[index] = new(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle));
        }
        return points;
    }

    internal static Point[] Star(double centerX, double centerY, double radius, int count, int skip)
    {
        var points = new Point[count];
        for (int index = 0; index < count; index++)
        {
            double angle = 2 * Math.PI * (index * skip % count) / count + 0.31;
            points[index] = new(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle));
        }
        return points;
    }

    internal static object Create(int width, int height) =>
        Activator.CreateInstance(RasterizerType, Members, null, [width, height], null)!;

    internal static Mask Rasterize(int width, int height, IEnumerable<Point[]> polygons, bool evenOdd,
        bool rent = false, (int, int, int, int)? bounds = null)
    {
        object rasterizer = Create(width, height);
        Invoke(rasterizer, "Reset", []);
        Invoke(rasterizer, "AddPolygons", [polygons]);
        return Sweep(rasterizer, evenOdd, rent, bounds);
    }

    internal static Mask Sweep(object rasterizer, bool evenOdd, bool rent = false,
        (int, int, int, int)? bounds = null) =>
        (Mask)Invoke(rasterizer, "Sweep", [evenOdd, rent, bounds])!;

    internal static object? Invoke(object rasterizer, string name, object?[] arguments)
    {
        MethodInfo method = RasterizerType.GetMethods(Members)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        return method.Invoke(method.IsStatic ? null : rasterizer, arguments);
    }

    internal static byte[] Pixels(Mask mask, int width, int height)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * width + x] = mask.At(x, y);
        return pixels;
    }

    // Exact reference: per-pixel integral of the winding number from vertices on the 1/256 grid.
    internal static (byte[] Bytes, bool[] Crossed) Model(int width, int height, Point[][] polygons, bool evenOdd)
    {
        var area = new double[width * height];
        var crossed = new bool[width * height];
        foreach (Point[] source in polygons)
        {
            if (source.Length < 3) continue;
            var polygon = new (double X, double Y)[source.Length];
            bool finite = true;
            for (int index = 0; index < source.Length; index++)
            {
                double x = Math.Round(source[index].X * 256), y = Math.Round(source[index].Y * 256);
                if (!double.IsFinite(x) || !double.IsFinite(y)) finite = false;
                polygon[index] = (x / 256, y / 256);
            }
            if (!finite) continue;
            double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
            double minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
            int left = (int)Math.Max(0, Math.Floor(minX)), right = (int)Math.Min(width, Math.Ceiling(maxX));
            int top = (int)Math.Max(0, Math.Floor(minY)), bottom = (int)Math.Min(height, Math.Ceiling(maxY));
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                area[y * width + x] += ClippedArea(polygon, x, y);
                if (!crossed[y * width + x]) crossed[y * width + x] = Crosses(polygon, x, y);
            }
        }
        var bytes = new byte[width * height];
        for (int index = 0; index < bytes.Length; index++)
        {
            double a = area[index];
            double coverage = evenOdd ? Math.Abs(a - 2 * Math.Round(a / 2)) : Math.Min(1, Math.Abs(a));
            bytes[index] = (byte)Math.Min(255, Math.Floor(256 * coverage));
        }
        return (bytes, crossed);
    }

    private static double ClippedArea((double X, double Y)[] polygon, int pixelX, int pixelY)
    {
        List<(double X, double Y)> points = [.. polygon];
        points = Clip(points, p => p.X - pixelX, (a, b, t) => (pixelX, a.Y + (b.Y - a.Y) * t));
        points = Clip(points, p => pixelX + 1 - p.X, (a, b, t) => (pixelX + 1, a.Y + (b.Y - a.Y) * t));
        points = Clip(points, p => p.Y - pixelY, (a, b, t) => (a.X + (b.X - a.X) * t, pixelY));
        points = Clip(points, p => pixelY + 1 - p.Y, (a, b, t) => (a.X + (b.X - a.X) * t, pixelY + 1));
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            (double X, double Y) a = points[index], b = points[(index + 1) % points.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    private static List<(double X, double Y)> Clip(List<(double X, double Y)> input,
        Func<(double X, double Y), double> inside,
        Func<(double X, double Y), (double X, double Y), double, (double X, double Y)> cut)
    {
        var result = new List<(double X, double Y)>(input.Count + 4);
        for (int index = 0; index < input.Count; index++)
        {
            (double X, double Y) current = input[index], next = input[(index + 1) % input.Count];
            double a = inside(current), b = inside(next);
            if (a >= 0) result.Add(current);
            if ((a >= 0) != (b >= 0))
            {
                double t = a / (a - b);
                result.Add(cut(current, next, t));
            }
        }
        return result;
    }

    // Whether any edge passes through the open pixel square, rather than missing or touching it.
    private static bool Crosses((double X, double Y)[] polygon, int pixelX, int pixelY)
    {
        for (int index = 0; index < polygon.Length; index++)
        {
            (double X, double Y) a = polygon[index], b = polygon[(index + 1) % polygon.Length];
            double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
            if (!Limit(-dx, a.X - pixelX, ref t0, ref t1) || !Limit(dx, pixelX + 1 - a.X, ref t0, ref t1)
                || !Limit(-dy, a.Y - pixelY, ref t0, ref t1) || !Limit(dy, pixelY + 1 - a.Y, ref t0, ref t1))
                continue;
            if (t1 <= t0) continue;
            double middle = (t0 + t1) / 2, x = a.X + dx * middle, y = a.Y + dy * middle;
            if (x > pixelX && x < pixelX + 1 && y > pixelY && y < pixelY + 1) return true;
        }
        return false;

        static bool Limit(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
    }
}
