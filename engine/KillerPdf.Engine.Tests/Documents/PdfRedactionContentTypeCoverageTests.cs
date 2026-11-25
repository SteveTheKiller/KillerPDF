using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

/// <summary>
/// Content that reaches a page by a less common route still leaves the file when a region covers
/// where it shows: Type 3 glyphs, a tiling pattern's own text, text in a layer that is switched
/// off, a link's address, a note's popup copy, and text inside a soft mask (which needs an image).
/// </summary>
public sealed class PdfRedactionContentTypeCoverageTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private static readonly PdfRedactionRegion Box = new(0, new PdfContentBounds(60, 680, 360, 740));

    public static TheoryData<string, string> Cases => new()
    {
        { "TTHREE", "type3" },
        { "PATSECRET", "pattern" },
        { "LAYERSECRET", "layer" },
        { "LINKSECRET", "link" },
        { "POPSECRET", "popup" },
        { "MASKSECRET", "softmask" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void CoveredContentLeavesTheFileWhateverRouteItTakes(string secret, string route)
    {
        PdfDocument document = PdfDocument.Open(Build(route));
        Assert.True(PdfContentRedaction.FindSurvivingText(document.Source, [secret]) is { Succeeded: false },
            "The probe must start with the secret in the file.");
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [Box]);
        Assert.Equal(route == "softmask" ? PdfRedactionPageMethod.Raster : PdfRedactionPageMethod.Content,
            Assert.Single(plan.Pages).Method);

        byte[] output = PdfContentRedaction.Apply(document, [Box],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0] }).Document.ToArray();

        Assert.DoesNotContain(secret, Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        Assert.True(PdfContentRedaction.FindSurvivingText(output, [secret]).Succeeded);
    }

    private static byte[] Build(string route) => route switch
    {
        "type3" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /T 6 0 R /H 6 0 R /R 6 0 R /E 6 0 R >> /Encoding << /Type /Encoding /Differences [72 /H 69 /E 82 /R 84 /T] >> /FirstChar 69 /LastChar 84 /Widths [600 0 0 600 0 0 0 0 0 0 0 0 0 600 0 600] /Resources << >> >>",
            Stream("BT /T3 12 Tf 72 700 Td (TTHREE) Tj ET"),
            Stream("600 0 0 0 600 700 d1 0 0 600 700 re f"),
        ]),
        "pattern" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Pattern << /P1 4 0 R >> >> /Contents 5 0 R >>",
            Stream("BT /F1 12 Tf 10 20 Td (PATSECRET) Tj ET",
                "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 300 60] /XStep 300 /YStep 60 /Resources << /Font << /F1 6 0 R >> >>"),
            Stream("/Pattern cs /P1 scn 70 690 280 40 re f"),
            Helvetica,
        ]),
        "layer" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] /Order [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> /Properties << /oc1 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("/OC /oc1 BDC BT /F1 12 Tf 72 700 Td (LAYERSECRET) Tj ET EMC"),
            "<< /Type /OCG /Name (Hidden) >>",
        ]),
        "link" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (mail me) Tj ET"),
            "<< /Type /Annot /Subtype /Link /Rect [70 695 200 715] /Border [0 0 0] /A << /S /URI /URI (mailto:LINKSECRET@example.com) >> >>",
        ]),
        // The popup sits far from the region, yet carries its own copy of the note's text.
        "popup" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (note here) Tj ET"),
            "<< /Type /Annot /Subtype /Text /Rect [80 700 100 720] /Contents (POPSECRET) /T (Reviewer) /Popup 7 0 R >>",
            "<< /Type /Annot /Subtype /Popup /Rect [400 500 600 600] /Parent 6 0 R /Contents (POPSECRET) /Open true >>",
        ]),
        "softmask" => CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /ExtGState << /GS1 << /SMask << /S /Luminosity /G 4 0 R >> >> >> >> /Contents 5 0 R >>",
            Stream("1 g 0 0 612 792 re f 0 g BT /F1 24 Tf 72 700 Td (MASKSECRET) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 6 0 R >> >>"),
            Stream("q /GS1 gs 0 0 1 rg 60 680 300 60 re f Q"),
            Helvetica,
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
