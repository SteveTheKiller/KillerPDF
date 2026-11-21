using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// #505: redacting text in a signed PDF invalidates the signature, but the signature box keeps
// showing who signed it, the way other readers show it.
public sealed class PdfRedactionSignatureAppearanceTests
{
    private const double SignatureLeft = 36, SignatureBottom = 36, SignatureWidth = 220, SignatureHeight = 72;

    [Fact]
    public void RedactionKeepsTheSignatureAppearanceInTheFile()
    {
        byte[] signed = SignedWithText();
        byte[] before = AppearanceBytes(PdfDocument.Open(signed));

        byte[] redacted = Redact(signed);

        Assert.Equal(before, AppearanceBytes(PdfDocument.Open(redacted)));
    }

    [Fact]
    public void ViewerRenderStillDrawsTheSignatureAfterRedaction()
    {
        byte[] signed = SignedWithText();
        byte[] redacted = Redact(signed);

        Assert.True(InkInsideSignature(signed), "The signed box should draw before redaction.");
        Assert.True(InkInsideSignature(redacted), "The signature box rendered blank after redaction.");
    }

    [Fact]
    public void ACorruptAppearanceOnAnUnsignedSignatureFieldDoesNotFailThePage()
    {
        byte[] garbage = [0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x13, 0x37];
        var parts = new List<byte[]>();
        var offsets = new List<int>();
        int length = 0;
        void Add(string text) { byte[] b = System.Text.Encoding.Latin1.GetBytes(text); parts.Add(b); length += b.Length; }
        void AddBytes(byte[] b) { parts.Add(b); length += b.Length; }
        Add("%PDF-1.7\n");
        offsets.Add(length); Add("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");
        offsets.Add(length); Add("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets.Add(length); Add("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>\nendobj\n");
        offsets.Add(length); Add("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [10 10 110 60] /P 3 0 R /AP << /N 5 0 R >> >>\nendobj\n");
        offsets.Add(length); Add($"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Filter /FlateDecode /Length {garbage.Length} >>\nstream\n");
        AddBytes(garbage); Add("\nendstream\nendobj\n");
        int xref = length;
        var table = new System.Text.StringBuilder("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) table.Append($"{offset:D10} 00000 n \n");
        table.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Add(table.ToString());
        byte[] pdf = [.. parts.SelectMany(part => part)];

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(pdf)).RenderFrame(0,
            new PdfRenderOptions(200, 200, includeAnnotations: true, includeFormFields: false)
            {
                IncludeSignedSignatureFields = true
            });

        Assert.Equal(200, frame.Width);
    }

    private static byte[] Redact(byte[] signed) =>
        PdfContentRedaction.Apply(PdfDocument.Open(signed),
            [new PdfRedactionRegion(0, new PdfContentBounds(40, 240, 300, 280))],
            new PdfContentRedactionOptions { AllowSignatureInvalidation = true }).Document.ToArray();

    private static byte[] SignedWithText()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(400, 400, new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 14).MoveText(50, 250).ShowLatin1Text("Secret account number")
            .EndText()).Build();
        return PdfDetachedSignatureWriter.Sign(PdfDocument.Open(source), _ => [0x30, 0x00],
            new PdfSignatureOptions
            {
                SignerName = "Ada Lovelace",
                VisibleAppearance = new PdfSignatureAppearance
                {
                    Left = SignatureLeft, Bottom = SignatureBottom,
                    Width = SignatureWidth, Height = SignatureHeight,
                    Text = "Signed by Ada Lovelace", FontSize = 14
                }
            });
    }

    private static byte[] AppearanceBytes(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var annotations = (PdfArray)Resolve(document, tree.Pages[0].Dictionary[Name("Annots")]);
        foreach (PdfObject item in annotations)
        {
            var widget = (PdfDictionary)Resolve(document, item);
            if (!widget.TryGetValue(Name("AP"), out PdfObject? appearances)) continue;
            var normal = (PdfStream)Resolve(document, ((PdfDictionary)Resolve(document, appearances))[Name("N")]);
            return document.DecodeStream(normal, 1 << 20);
        }
        throw new InvalidOperationException("The page has no widget with an appearance.");
    }

    private static bool InkInsideSignature(byte[] bytes)
    {
        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(bytes)).RenderFrame(0,
            new PdfRenderOptions(400, 400, includeAnnotations: true, includeFormFields: false)
            {
                IncludeSignedSignatureFields = true,
                IncludePushButtonFields = true
            });
        for (int y = (int)(400 - SignatureBottom - SignatureHeight); y < 400 - SignatureBottom; y++)
            for (int x = (int)SignatureLeft; x < SignatureLeft + SignatureWidth; x++)
                if (frame.Pixels[(y * frame.Width + x) * 4] < 0.5f) return true;
        return false;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
