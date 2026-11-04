using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLogicalDocumentTests
{
    [Fact]
    public void IndependentCopyInheritsProofPreferencesAndCanThenDiverge()
    {
        var original = new PdfLogicalDocument
        {
            Proof = new PdfDocumentProofSettings(enabled: true,
                options: new PdfProofOptions { SimulatePaper = true }, gamutWarnings: true)
        };

        var copy = original.CreateIndependentCopy();

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Proof, copy.Proof);
        Assert.Equal(0, copy.ProofGeneration);
        copy.Proof = new PdfDocumentProofSettings();
        Assert.True(original.Proof.Enabled);
        Assert.True(original.Proof.Options.SimulatePaper);
        Assert.True(original.Proof.GamutWarnings);
        Assert.False(copy.Proof.Enabled);
        Assert.Equal(1, copy.ProofGeneration);
        Assert.Equal(1, original.ProofGeneration);
    }

    [Fact]
    public void ReselectingTheSameProfileContentDoesNotInvalidateTheDocument()
    {
        var document = new PdfLogicalDocument();
        // Profile selection can precede enabling proof. This forward-only source profile
        // exercises content identity without claiming a usable printer proof transform.
        document.Proof = new PdfDocumentProofSettings(printerProfile: PdfCmykViewingDefault.LoadProfile());
        long generation = document.ProofGeneration;

        document.Proof = new PdfDocumentProofSettings(printerProfile: PdfCmykViewingDefault.LoadProfile());

        Assert.Equal(generation, document.ProofGeneration);
        Assert.False(document.Proof.Enabled);
    }

    [Fact]
    public void ChangingProofInvalidatesOnlyItsLogicalDocument()
    {
        var first = new PdfLogicalDocument();
        var second = new PdfLogicalDocument();
        Guid identity = first.Id;
        first.Proof = new PdfDocumentProofSettings(enabled: true, gamutWarnings: true);

        Assert.True(first.Proof.Enabled);
        Assert.True(first.Proof.GamutWarnings);
        Assert.Equal(1, first.ProofGeneration);
        Assert.False(second.Proof.Enabled);
        Assert.Equal(0, second.ProofGeneration);
        Assert.Equal(identity, first.Id);
        Assert.Null(first.ReadSnapshot);
    }

    [Fact]
    public void NewDocumentStartsWithProofAndWarningsOffAndApprovedConversionDefaults()
    {
        var document = new PdfLogicalDocument();

        Assert.False(document.Proof.Enabled);
        Assert.Null(document.Proof.PrinterProfile);
        Assert.False(document.Proof.GamutWarnings);
        Assert.Equal(PdfRenderingIntent.RelativeColorimetric, document.Proof.Options.RenderingIntent);
        Assert.True(document.Proof.Options.BlackPointCompensation);
        Assert.False(document.Proof.Options.SimulatePaper);
        Assert.False(document.Proof.Options.SimulateBlack);
    }

    [Fact]
    public void IdentityOnlyOwnersHaveStableDistinctIdsWithoutReadSnapshots()
    {
        var first = new PdfLogicalDocument();
        var second = new PdfLogicalDocument();

        Guid firstId = first.Id;
        Guid secondId = second.Id;

        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, secondId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, first.Id);
        Assert.Null(first.ReadSnapshot);
        Assert.Null(second.ReadSnapshot);
    }
}
