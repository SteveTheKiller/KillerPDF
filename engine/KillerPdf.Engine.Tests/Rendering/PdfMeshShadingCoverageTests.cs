using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// ISO 32000-2 8.5.4: a clip path limits painting the same way for every paint, so mesh
// shadings (8.7.4.5.5 to 8.7.4.5.8) composite through the fractional clip coverage that
// fills, images and the other shading types use. A pixel the circle covers by a fraction c
// shows the red shading blended into the white page by c, within two levels.
public sealed class PdfMeshShadingCoverageTests
{
    private const int Size = 40;
    private const double CenterX = 20, CenterY = 20, Radius = 15;
    // The circle is written as a 64-sided polygon, which stays within 0.02 pixel of the true
    // circle, so the expected coverage is the polygon's exact area and owes nothing to how
    // the renderer flattens curves.
    private const int Sides = 64;

    public static TheoryData<int, string> ClipRoutes => new()
    {
        { 6, "sh" }, { 7, "sh" }, { 4, "sh" }, { 5, "sh" },
        { 6, "pattern" }, { 7, "pattern" },
        { 6, "softmask" }, { 7, "softmask" }
    };

    [Theory]
    [MemberData(nameof(ClipRoutes))]
    public void CircleClipEdgePixelsBlendTheShadingByTheirCoverage(int shadingType, string route)
    {
        double maskLevel = route == "softmask" ? 0.5 : 1;
        PdfRenderedPage page = Render(shadingType, route);
        double[,] coverage = CircleCoverage();

        int interior = (int)Math.Round(255 * (1 - maskLevel));
        AssertPixel(page, 20, 20, 255, interior, interior, tolerance: 1);
        AssertPixel(page, 1, 1, 255, 255, 255, tolerance: 0);
        // Worked values at three rim pixels, computed separately by clipping the polygon to each
        // pixel square (Sutherland-Hodgman) and taking the shoelace area. Pixel (9, 9) spans
        // x 9 to 10 and page y 30 to 31; the polygon covers 0.67448 of it, so green and blue
        // are 255 * (1 - 0.67448) = 83.0. Pixel (16, 5) is 0.56809 (110.1); pixel (15, 5) is
        // 0.29523 (179.7).
        Assert.InRange(coverage[9, 9], 0.6740, 0.6750);
        Assert.InRange(coverage[5, 16], 0.5676, 0.5686);
        Assert.InRange(coverage[5, 15], 0.2947, 0.2957);
        if (route == "sh" && shadingType is 6 or 7)
        {
            AssertPixel(page, 9, 9, 255, 83, 83, tolerance: 2);
            AssertPixel(page, 16, 5, 255, 110, 110, tolerance: 2);
            AssertPixel(page, 15, 5, 255, 180, 180, tolerance: 2);
        }
        int edgePixels = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double c = coverage[y, x];
                double expected = 255 * (1 - maskLevel * c);
                byte[] pixel = Pixel(page, x, y);
                Assert.True(pixel[2] >= 253,
                    $"Pixel ({x}, {y}) red is {pixel[2]}; expected 255.");
                Assert.True(Math.Abs(pixel[0] - expected) <= 2 && Math.Abs(pixel[1] - expected) <= 2,
                    $"Pixel ({x}, {y}) with clip coverage {c:F4} is RGB {pixel[2]},{pixel[1]},{pixel[0]}; "
                    + $"expected 255,{expected:F1},{expected:F1}.");
                if (c is > 0.2 and < 0.8) edgePixels++;
            }
        // The rim crosses about 2 * pi * 15 pixels, many of them well inside the coverage range.
        Assert.True(edgePixels >= 30, $"Only {edgePixels} fractional edge pixels were checked.");
    }

    private static void AssertPixel(PdfRenderedPage page, int x, int y, int r, int g, int b, int tolerance)
    {
        byte[] pixel = Pixel(page, x, y);
        Assert.True(Math.Abs(pixel[2] - r) <= tolerance && Math.Abs(pixel[1] - g) <= tolerance
            && Math.Abs(pixel[0] - b) <= tolerance,
            $"Pixel ({x}, {y}) is RGB {pixel[2]},{pixel[1]},{pixel[0]}; expected {r},{g},{b}.");
    }

    // Rendered pages are BGRA.
    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static (double X, double Y)[] CircleVertices() =>
        [.. Enumerable.Range(0, Sides).Select(index =>
        {
            double angle = 2 * Math.PI * index / Sides;
            return (Math.Round(CenterX + Radius * Math.Cos(angle), 6),
                Math.Round(CenterY + Radius * Math.Sin(angle), 6));
        })];

    private static string CirclePath()
    {
        static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        (double X, double Y)[] vertices = CircleVertices();
        var path = new StringBuilder($"{F(vertices[0].X)} {F(vertices[0].Y)} m");
        foreach ((double x, double y) in vertices.Skip(1)) path.Append($" {F(x)} {F(y)} l");
        return path.Append(" h").ToString();
    }

    // Independent oracle: the polygon's area inside each device pixel, integrated over 1024
    // scanlines per pixel row with the exact span length along each scanline.
    private static double[,] CircleCoverage() => PolygonCoverage(CircleVertices());

    private static double[,] PolygonCoverage((double X, double Y)[] polygon)
    {
        var coverage = new double[Size, Size];
        const int scanlines = 1024;
        var crossings = new List<double>();
        for (int row = 0; row < Size; row++)
            for (int line = 0; line < scanlines; line++)
            {
                double pageY = Size - row - (line + 0.5) / scanlines;
                crossings.Clear();
                for (int index = 0; index < polygon.Length; index++)
                {
                    var from = polygon[index];
                    var to = polygon[(index + 1) % polygon.Length];
                    if ((from.Y <= pageY) == (to.Y <= pageY)) continue;
                    crossings.Add(from.X + (pageY - from.Y) / (to.Y - from.Y) * (to.X - from.X));
                }
                crossings.Sort();
                for (int span = 0; span + 1 < crossings.Count; span += 2)
                    for (int column = 0; column < Size; column++)
                    {
                        double inside = Math.Min(column + 1, crossings[span + 1]) - Math.Max(column, crossings[span]);
                        if (inside > 0) coverage[row, column] += inside / scanlines;
                    }
            }
        return coverage;
    }

    // A mesh is often filled as a pattern through its own outline, or clipped to it. The clip
    // and the mesh then have the same edge, and a pixel that edge half covers is half painted,
    // as a plain fill of that path is, not a quarter painted by stacking the two coverages.
    [Theory]
    [InlineData("pattern")]
    [InlineData("clip")]
    public void MeshInsideItsOwnOutlineBlendsEdgePixelsByThatOutlineOnce(string route)
    {
        (double X, double Y)[] triangle = [(4.5, 4.5), (35.5, 4.5), (4.5, 35.5)];
        const string outline = "4.5 4.5 m 35.5 4.5 l 4.5 35.5 l h";
        static byte[] Coordinate(double value)
        {
            int units = (int)Math.Round(value * 1000);
            return [(byte)(units >> 8), (byte)units];
        }
        var data = new List<byte>();
        foreach ((double x, double y) in triangle)
            data.AddRange([0, .. Coordinate(x), .. Coordinate(y), 255, 0, 0]);
        string shading = Stream(Encoding.Latin1.GetString([.. data]),
            "/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 16 /BitsPerComponent 8 "
            + "/BitsPerFlag 8 /Decode [0 65.535 0 65.535 0 1 0 1 0 1]");
        PdfRenderedPage page = route == "pattern"
            ? RenderPage($"/Pattern cs /P1 scn {outline} f",
                "<< /Pattern << /P1 << /PatternType 2 /Shading 5 0 R >> >> >>", shading)
            : RenderPage($"q {outline} W n /Sh1 sh Q", "<< /Shading << /Sh1 5 0 R >> >>", shading);
        double[,] coverage = PolygonCoverage(triangle);

        // Worked value: pixel (4, 20) spans x 4 to 5 and the edge x = 4.5 halves it, so green
        // and blue are 255 * (1 - 0.5) = 127.5.
        Assert.InRange(coverage[20, 4], 0.499, 0.501);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double expected = 255 * (1 - coverage[y, x]);
                byte[] pixel = Pixel(page, x, y);
                Assert.True(pixel[2] >= 253 && Math.Abs(pixel[0] - expected) <= 2
                    && Math.Abs(pixel[1] - expected) <= 2,
                    $"Pixel ({x}, {y}) with coverage {coverage[y, x]:F3} is RGB {pixel[2]},{pixel[1]},{pixel[0]}; "
                    + $"expected 255,{expected:F1},{expected:F1}.");
            }
    }

    // Two Coons patches share the edge x = 20.25 and together cover the square 4.5 to 35.5.
    // The shared edge is interior to the shape, so pixels along it are fully covered and show
    // the plain shading color: no gap and no double paint, even at half opacity. The square's
    // own outer edges cross pixel centers, so those pixels are half covered and blend by half.
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void AdjacentPatchesShowNoSeamAndBlendTheirOuterEdges(double opacity)
    {
        static byte[] Coordinate(double value)
        {
            // 16-bit coordinates over Decode 0 to 65.535 count thousandths of a point.
            int units = (int)Math.Round(value * 1000);
            return [(byte)(units >> 8), (byte)units];
        }
        static IEnumerable<byte> Patch(double left, double right, double bottom, double top)
        {
            double third = (right - left) / 3, height = (top - bottom) / 3;
            (double X, double Y)[] boundary =
            [
                (left, bottom), (left + third, bottom), (right - third, bottom), (right, bottom),
                (right, bottom + height), (right, top - height), (right, top),
                (right - third, top), (left + third, top), (left, top),
                (left, top - height), (left, bottom + height)
            ];
            var bytes = new List<byte> { 0 };
            foreach ((double x, double y) in boundary) bytes.AddRange([.. Coordinate(x), .. Coordinate(y)]);
            for (int corner = 0; corner < 4; corner++) bytes.AddRange([255, 0, 0]);
            return bytes;
        }
        byte[] data = [.. Patch(4.5, 20.25, 4.5, 35.5), .. Patch(20.25, 35.5, 4.5, 35.5)];
        string shading = Stream(Encoding.Latin1.GetString(data),
            "/ShadingType 6 /ColorSpace /DeviceRGB /BitsPerCoordinate 16 /BitsPerComponent 8 "
            + "/BitsPerFlag 8 /Decode [0 65.535 0 65.535 0 1 0 1 0 1]");
        string alpha = opacity.ToString("0.###", CultureInfo.InvariantCulture);
        PdfRenderedPage page = RenderPage("/GS1 gs /Sh1 sh",
            $"<< /Shading << /Sh1 5 0 R >> /ExtGState << /GS1 << /ca {alpha} >> >> >>", shading);

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                // Device row y covers page y from 39 - y to 40 - y.
                double width = Math.Max(0, Math.Min(x + 1, 35.5) - Math.Max(x, 4.5));
                double height = Math.Max(0, Math.Min(Size - y, 35.5) - Math.Max(Size - y - 1, 4.5));
                double c = width * height;
                double expected = 255 * (1 - opacity * c);
                byte[] pixel = Pixel(page, x, y);
                Assert.True(pixel[2] >= 253 && Math.Abs(pixel[0] - expected) <= 2
                    && Math.Abs(pixel[1] - expected) <= 2,
                    $"Pixel ({x}, {y}) with coverage {c:F3} is RGB {pixel[2]},{pixel[1]},{pixel[0]}; "
                    + $"expected 255,{expected:F1},{expected:F1}.");
            }
    }

    private static PdfRenderedPage RenderPage(string content, string resources, string shading)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Size} {Size}] /Resources {resources} /Contents 4 0 R >>",
            Stream(content),
            shading
        };
        PdfDocument document = PdfDocument.Open(RawPdf(objects));
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(Size, Size, includeAnnotations: false, includeFormFields: false));
        Assert.Equal(Size, page.Width);
        Assert.DoesNotContain(page.Diagnostics, message => message.Contains("shading", StringComparison.OrdinalIgnoreCase));
        return page;
    }

    private static PdfRenderedPage Render(int shadingType, string route)
    {
        string shading = RedShading(shadingType);
        string circle = CirclePath();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
        };
        string pageContent;
        string resources;
        switch (route)
        {
            case "sh":
                pageContent = $"q {circle} W n /Sh1 sh Q";
                resources = "<< /Shading << /Sh1 5 0 R >> >>";
                break;
            case "pattern":
                pageContent = $"/Pattern cs /P1 scn {circle} f";
                resources = "<< /Pattern << /P1 << /PatternType 2 /Shading 5 0 R >> >> >>";
                break;
            default:
                // A transparency group drawn through a constant 50% luminosity soft mask.
                pageContent = "/GS1 gs /Fm1 Do";
                resources = "<< /XObject << /Fm1 6 0 R >> /ExtGState << /GS1 << /SMask << /S /Luminosity /G 7 0 R >> >> >> >>";
                break;
        }
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Size} {Size}] /Resources {resources} /Contents 4 0 R >>");
        objects.Add(Stream(pageContent));
        objects.Add(shading);
        string groupContent = $"q {circle} W n /Sh1 sh Q";
        objects.Add(Stream(groupContent,
            $"/Type /XObject /Subtype /Form /BBox [0 0 {Size} {Size}] /Group << /S /Transparency >> /Resources << /Shading << /Sh1 5 0 R >> >>"));
        objects.Add(Stream($"0.5 g 0 0 {Size} {Size} re f",
            $"/Type /XObject /Subtype /Form /BBox [0 0 {Size} {Size}] /Group << /S /Transparency /CS /DeviceGray >>"));
        PdfDocument document = PdfDocument.Open(RawPdf(objects));
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(Size, Size, includeAnnotations: false, includeFormFields: false));
        Assert.Equal(Size, page.Width);
        Assert.Equal(Size, page.Height);
        Assert.DoesNotContain(page.Diagnostics, message => message.Contains("shading", StringComparison.OrdinalIgnoreCase));
        return page;
    }

    // A mesh that covers the whole page in DeviceRGB red. Coordinates use 8 bits over 0 to 40.
    private static string RedShading(int shadingType)
    {
        byte[] boundary =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85
        ];
        byte[] interior = [85, 85, 170, 85, 170, 170, 85, 170];
        byte[] red = [255, 0, 0];
        var data = new List<byte>();
        string entries;
        switch (shadingType)
        {
            case 4:
                // Two triangles: (0,0) (40,0) (40,40), then flag 2 adds (0,40) on edge (0,0)-(40,40).
                data.AddRange([0, 0, 0, .. red, 0, 255, 0, .. red, 0, 255, 255, .. red, 2, 0, 255, .. red]);
                entries = "/BitsPerFlag 8";
                break;
            case 5:
                data.AddRange([0, 0, .. red, 255, 0, .. red, 0, 255, .. red, 255, 255, .. red]);
                entries = "/VerticesPerRow 2";
                break;
            default:
                data.Add(0);
                data.AddRange(boundary);
                if (shadingType == 7) data.AddRange(interior);
                for (int corner = 0; corner < 4; corner++) data.AddRange(red);
                entries = "/BitsPerFlag 8";
                break;
        }
        return Stream(Encoding.Latin1.GetString([.. data]),
            $"/ShadingType {shadingType} /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 {entries} "
            + $"/Decode [0 {Size} 0 {Size} 0 1 0 1 0 1]");
    }

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] RawPdf(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
