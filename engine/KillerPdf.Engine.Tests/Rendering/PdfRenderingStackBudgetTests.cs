using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRenderingStackBudgetTests(ITestOutputHelper output)
{
    private const string Blue = "0 0 1 rg 8 20 12 14 re f";
    private const string Green = "0 1 0 rg 40 42 8 6 re f";

    [Theory]
    [InlineData("Form")]
    [InlineData("Type3")]
    [InlineData("Pattern")]
    [InlineData("Mask")]
    [InlineData("Annotation")]
    public void HealthyNestedRenderingHasIndependentTargetRemovalControls(string kind)
    {
        PdfRenderedPage present = new PdfPageRenderer(PdfDocument.Open(Pdf(kind, 2, true))).Render(0, Options());
        PdfRenderedPage removed = new PdfPageRenderer(PdfDocument.Open(Pdf(kind, 2, false))).Render(0, Options());
        Assert.Empty(present.Diagnostics);
        Assert.Empty(removed.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(present, 12, 39));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(removed, 12, 39));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(present, 44, 19));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(removed, 44, 19));
    }

    [Theory]
    [InlineData("Form")]
    [InlineData("Type3")]
    [InlineData("Pattern")]
    [InlineData("Mask")]
    [InlineData("Annotation")]
    public async Task SmallStackRefusesExcessiveRecursionAndKeepsTheSessionUsable(string kind)
    {
        byte[] bytes = Pdf(kind, 40, true);
        var completion = new TaskCompletionSource<(Exception? Error, byte[] Blue, byte[] Green)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(bytes);
                var renderer = new PdfPageRenderer(document);
                Exception? error = Record.Exception(() => renderer.Render(0, Options()));
                PdfRenderedPage sibling = renderer.Render(1, Options());
                completion.SetResult((error, Pixel(sibling, 12, 39), Pixel(sibling, 44, 19)));
            }
            catch (Exception error) { completion.SetException(error); }
        }, 512 * 1024) { IsBackground = true };
        thread.Start();
        // The render thread signals when it is done; the limit is only a hang guard, since a render that starts cold on a
        // starved machine (first use of the Type 3, pattern and mask code) takes longer than any short limit.
        var result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
        output.WriteLine($"kind={kind}; stackBytes=524288; refusalType={result.Error?.GetType().FullName ?? "none"}");
        Assert.True(result.Error is InsufficientExecutionStackException or FormatException,
            $"Expected managed refusal, got {result.Error?.GetType().Name ?? "a rendered result"}.");
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.Blue);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, result.Green);
    }

    private static PdfRenderOptions Options() => new(64, 64) { CacheResult = false };
    private static byte[] Pixel(PdfRenderedPage page, int x, int y)
        => page.Pixels.Span.Slice((y * 64 + x) * 4, 4).ToArray();
    private static string Stream(string content, string extra = "")
        => $"<< /Length {content.Length} {extra} >>\nstream\n{content}\nendstream";

    private static byte[] Pdf(string kind, int depth, bool drawTarget)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "", "" };
        string resources;
        string contents;
        string annotations = "";
        if (kind == "Type3")
        {
            resources = "/Font << /F 5 0 R >>";
            contents = "BT /F 20 Tf 1 0 0 1 8 20 Tm (A) Tj ET";
            for (int level = 0; level < depth; level++)
            {
                int glyph = 6 + level * 2;
                bool leaf = level + 1 == depth;
                objects.Add("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] "
                    + "/FontMatrix [.001 0 0 .001 0 0] /Encoding << /Differences [65 /A] >> "
                    + "/FirstChar 65 /LastChar 65 /Widths [600] "
                    + $"/CharProcs << /A {glyph} 0 R >> /Resources << "
                    + (leaf ? "" : $"/Font << /F {glyph + 1} 0 R >> ") + ">> >>");
                objects.Add(Stream("600 0 d0\n" + (leaf
                    ? drawTarget ? "0 0 1 rg 0 0 600 700 re f" : ""
                    : "BT /F 1000 Tf (A) Tj ET")));
            }
        }
        else
        {
            int first = kind == "Annotation" ? 6 : 5;
            resources = kind switch
            {
                "Pattern" => "/Pattern << /P 5 0 R >>",
                "Mask" => "/ExtGState << /GS << /SMask << /S /Luminosity /G 5 0 R >> >> >>",
                _ => $"/XObject << /Fm {first} 0 R >>"
            };
            contents = kind switch
            {
                "Pattern" => "/Pattern cs /P scn 0 0 64 64 re f",
                "Mask" => "q /GS gs " + (drawTarget ? Blue : "") + " Q",
                "Annotation" => "",
                _ => "/Fm Do"
            };
            if (kind == "Annotation")
            {
                annotations = "/Annots [5 0 R]";
                objects.Add("<< /Type /Annot /Subtype /Stamp /Rect [0 0 64 64] /F 4 /P 3 0 R /AP << /N 6 0 R >> >>");
            }
            for (int level = 0; level < depth; level++)
            {
                bool leaf = level + 1 == depth;
                int next = first + level + 1;
                string nested = leaf ? "" : kind switch
                {
                    "Pattern" => $"/Pattern << /P {next} 0 R >>",
                    "Mask" => $"/ExtGState << /GS << /SMask << /S /Luminosity /G {next} 0 R >> >> >>",
                    _ => $"/XObject << /Fm {next} 0 R >>"
                };
                string body = kind == "Mask" ? (leaf ? "" : "/GS gs ") + "1 g 0 0 64 64 re f"
                    : leaf ? drawTarget ? Blue : ""
                    : kind == "Pattern" ? "/Pattern cs /P scn 0 0 64 64 re f" : "/Fm Do";
                string type = kind == "Pattern" ? "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /XStep 64 /YStep 64"
                    : "/Type /XObject /Subtype /Form";
                if (kind == "Mask") type += " /Group << /S /Transparency /I true /CS /DeviceGray >>";
                objects.Add(Stream(body, type + $" /BBox [0 0 64 64] /Resources << {nested} >>"));
            }
        }
        int sibling = objects.Count + 1;
        objects[1] = $"<< /Type /Pages /Kids [3 0 R {sibling} 0 R] /Count 2 >>";
        objects[2] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 64 64] /Resources << {resources} >> /Contents 4 0 R {annotations} >>";
        objects[3] = Stream(contents + "\n" + Green);
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 64 64] /Resources << >> /Contents {sibling + 1} 0 R >>");
        objects.Add(Stream(Blue + "\n" + Green));
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
