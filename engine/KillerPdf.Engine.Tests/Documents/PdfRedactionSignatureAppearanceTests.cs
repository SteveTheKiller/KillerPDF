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
