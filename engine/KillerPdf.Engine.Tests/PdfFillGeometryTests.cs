using System.Collections;
using System.Reflection;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests;

public sealed class PdfFillGeometryTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(257)]
    public void PageFills_MatchPixelPolygonCoverage(int points)
    {
        const BindingFlags members = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type renderer = typeof(PdfPageRenderer);
        Type pointType = renderer.GetNestedType("Point", BindingFlags.NonPublic)!;
        Type frameType = renderer.GetNestedType("RasterFrame", BindingFlags.NonPublic)!;
        Type listType = typeof(List<>).MakeGenericType(pointType);
        Type pathsType = typeof(List<>).MakeGenericType(listType);
        Type pathsInterface = typeof(IReadOnlyList<>).MakeGenericType(listType);
        MethodInfo convert = frameType.GetMethod("ToPixels", members, null,
            [pathsInterface, typeof(int)], null)!;
        MethodInfo fill = renderer.GetMethod("RasterizeFill", members, null,
            [pathsInterface, typeof(bool), frameType, typeof(bool)], null)!;
        MethodInfo reference = renderer.GetMethod("RasterizePolygons", members)!;
        object frame = Activator.CreateInstance(frameType, 96, 80, 0.75, 1.25)!;

        foreach (bool evenOdd in new[] { false, true })
        for (int variant = 0; variant < 6; variant++)
        {
            var paths = (IList)Activator.CreateInstance(pathsType)!;
            paths.Add(CreatePolygon(points, variant >= 3 ? 75 : 22, reverse: false));
            if (variant % 3 == 1)
                paths.Add(CreatePolygon(17, 10, reverse: !evenOdd));
            if (variant % 3 == 2)
            {
                paths.Insert(0, CreatePolygon(1, 1, reverse: false));
                paths.Add(CreatePolygon(2, 2, reverse: false));
            }
            object pixelPolygons = convert.Invoke(frame, [paths, 3])!;
            var expected = (PdfPageRenderer.CoverageMask)reference.Invoke(null,
                [pixelPolygons, evenOdd, 96, 80, false, null])!;
            var actual = (PdfPageRenderer.CoverageMask)fill.Invoke(null,
                [paths, evenOdd, frame, false])!;
            byte[] expectedPixels = new byte[96 * 80], actualPixels = new byte[96 * 80];
            for (int y = 0; y < 80; y++)
            for (int x = 0; x < 96; x++)
            {
                expectedPixels[y * 96 + x] = expected.At(x, y);
                actualPixels[y * 96 + x] = actual.At(x, y);
            }
            Assert.True(expectedPixels.AsSpan().SequenceEqual(actualPixels),
                $"Coverage differs for {points} points, variant {variant}, evenOdd {evenOdd}.");
        }

        IList CreatePolygon(int count, double radius, bool reverse)
        {
            var polygon = (IList)Activator.CreateInstance(listType)!;
            if (count is 4 or 5)
            {
                (double X, double Y)[] corners = [(64.125 - radius, 32.375 - radius),
                    (64.125 + radius, 32.375 - radius), (64.125 + radius, 32.375 + radius),
                    (64.125 - radius, 32.375 + radius)];
                for (int index = 0; index < count; index++)
                {
                    var corner = corners[index % 4];
                    polygon.Add(Activator.CreateInstance(pointType, corner.X, corner.Y)!);
                }
                return polygon;
            }
            for (int index = 0; index < count; index++)
            {
                int position = reverse ? count - 1 - index : index;
                double angle = position * Math.PI * 2 / count;
                double distance = radius * (position % 2 == 0 ? 1 : 0.65);
                double x = 64.125 + Math.Cos(angle) * distance;
                double y = 32.375 + Math.Sin(angle) * distance;
                polygon.Add(Activator.CreateInstance(pointType, x, y)!);
            }
            return polygon;
        }
    }
}
