using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SignaturePanelStatusTests
{
    [Fact]
    public void ANoChangesCertificationWithALaterRevisionShowsAsBroken()
    {
        string[] keys = SignatureStatus.Keys(Entry(PdfSignatureCertificationPermission.NoChanges, changeAfter: true));

        Assert.Equal(["Str_Panel_SigCertBroken"], keys);
    }

    [Fact]
    public void AFormFillingCertificationWithALaterRevisionOnlyShowsChanged()
    {
        string[] keys = SignatureStatus.Keys(Entry(PdfSignatureCertificationPermission.FormFillingAndSignatures, changeAfter: true));

        Assert.DoesNotContain("Str_Panel_SigCertBroken", keys);
        Assert.Equal("Str_Panel_SigChanged", keys[^1]);
    }

    [Fact]
    public void ANoChangesCertificationLeftAloneIsNotBroken()
    {
        string[] keys = SignatureStatus.Keys(Entry(PdfSignatureCertificationPermission.NoChanges, changeAfter: false));

        Assert.DoesNotContain("Str_Panel_SigCertBroken", keys);
        Assert.DoesNotContain("Str_Panel_SigChanged", keys);
    }

    private static PdfSignatureInspectionEntry Entry(PdfSignatureCertificationPermission permission, bool changeAfter)
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [0x30, 0x00], new PdfSignatureOptions
            {
                CertificationPermission = permission,
                ReservedSignatureSize = 8
            });
        if (changeAfter)
        {
            var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(signed));
            update.AddObject(new PdfInteger(1));
            signed = update.Build();
        }
        return Assert.Single(PdfSignatureInspection.Inspect(PdfDocument.Open(signed)).Entries);
    }
}
