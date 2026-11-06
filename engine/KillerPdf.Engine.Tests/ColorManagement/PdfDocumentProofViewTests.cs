using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfDocumentProofViewTests
{
    [Fact]
    public void IndependentDocumentCopiesRetainPreferencesAndThenDiverge()
    {
        var original = new PdfLogicalDocument
        {
            Proof = new PdfDocumentProofSettings(enabled: true, gamutWarnings: true)
        };
        var first = new PdfDocumentProofView(original);
        var copy = new PdfDocumentProofView(original.CreateIndependentCopy());

        Assert.NotEqual(first.Id, copy.Id);
        Assert.NotEqual(original.Id, copy.Document.Id);
        Assert.Equal(new PdfDocumentProofViewStamp(copy.Id, copy.Document.Id, 0, 0, false), copy.CaptureProof().Stamp);
        Assert.True(copy.CaptureProof().Settings.Enabled);
        Assert.True(copy.CaptureProof().Settings.GamutWarnings);
        copy.Document.Proof = new PdfDocumentProofSettings();

        Assert.Equal(new PdfDocumentProofViewStamp(copy.Id, copy.Document.Id, 1, 0, false), copy.CaptureProof().Stamp);
        Assert.False(copy.CaptureProof().Settings.Enabled);
        Assert.Equal(new PdfDocumentProofViewStamp(first.Id, original.Id, 1, 0, false), first.CaptureProof().Stamp);
        Assert.True(first.CaptureProof().Settings.Enabled);
        Assert.True(first.CaptureProof().Settings.GamutWarnings);
    }

    [Fact]
    public void ViewRequiresALogicalOwner()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new PdfDocumentProofView(null!));
        Assert.Equal("document", exception.ParamName);
    }

    [Fact]
    public void OnlySemanticOverrideChangesAdvanceItsGeneration()
    {
        var view = new PdfDocumentProofView(new PdfLogicalDocument());
        var firstProfile = PdfCmykViewingDefault.LoadProfile();
        var reloadedProfile = PdfCmykViewingDefault.LoadProfile();
        Assert.NotSame(firstProfile, reloadedProfile);
        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: firstProfile);
        Assert.Equal(1, view.CaptureProof().Stamp.OverrideGeneration);

        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: reloadedProfile);
        Assert.Equal(1, view.CaptureProof().Stamp.OverrideGeneration);
        var options = new PdfProofOptions { RenderingIntent = PdfRenderingIntent.AbsoluteColorimetric };
        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: reloadedProfile, options: options);
        Assert.Equal(2, view.CaptureProof().Stamp.OverrideGeneration);
        options = options with { BlackPointCompensation = false };
        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: reloadedProfile, options: options);
        Assert.Equal(3, view.CaptureProof().Stamp.OverrideGeneration);
        options = options with { SimulatePaper = true };
        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: reloadedProfile, options: options);
        Assert.Equal(4, view.CaptureProof().Stamp.OverrideGeneration);
        options = options with { SimulateBlack = true };
        view.ComparisonOverride = new PdfDocumentProofSettings(printerProfile: reloadedProfile, options: options);
        Assert.Equal(5, view.CaptureProof().Stamp.OverrideGeneration);
        view.ComparisonOverride = new PdfDocumentProofSettings(enabled: true, printerProfile: reloadedProfile, options: options);
        Assert.Equal(6, view.CaptureProof().Stamp.OverrideGeneration);
        view.ComparisonOverride = new PdfDocumentProofSettings(enabled: true, printerProfile: reloadedProfile,
            options: options, gamutWarnings: true);
        Assert.Equal(7, view.CaptureProof().Stamp.OverrideGeneration);
        view.ComparisonOverride = new PdfDocumentProofSettings(enabled: true, options: options, gamutWarnings: true);
        Assert.Equal(8, view.CaptureProof().Stamp.OverrideGeneration);
        view.ComparisonOverride = null;
        Assert.Equal(9, view.CaptureProof().Stamp.OverrideGeneration);
        view.ComparisonOverride = null;
        Assert.Equal(9, view.CaptureProof().Stamp.OverrideGeneration);
        Assert.Equal(0, view.Document.ProofGeneration);
    }

    [Fact]
    public void ComparisonRemainsLocalUntilClearedAndThenReadsTheLatestOwner()
    {
        var owner = new PdfLogicalDocument();
        var normal = new PdfDocumentProofView(owner);
        var comparison = new PdfDocumentProofView(owner);
        comparison.ComparisonOverride = new PdfDocumentProofSettings(enabled: true,
            options: new PdfProofOptions { SimulatePaper = true });

        var captured = comparison.CaptureProof();
        Assert.Equal(new PdfDocumentProofViewStamp(comparison.Id, owner.Id, 0, 1, true), captured.Stamp);
        Assert.True(captured.Settings.Enabled);
        Assert.True(captured.Settings.Options.SimulatePaper);
        Assert.False(owner.Proof.Enabled);
        Assert.False(normal.CaptureProof().Settings.Enabled);

        owner.Proof = new PdfDocumentProofSettings(gamutWarnings: true);
        Assert.Equal(captured, comparison.CaptureProof());
        Assert.True(normal.CaptureProof().Settings.GamutWarnings);
        comparison.ComparisonOverride = null;

        var restored = comparison.CaptureProof();
        Assert.Equal(new PdfDocumentProofViewStamp(comparison.Id, owner.Id, 1, 2, false), restored.Stamp);
        Assert.False(restored.Settings.Enabled);
        Assert.True(restored.Settings.GamutWarnings);
        Assert.False(restored.Settings.Options.SimulatePaper);
        Assert.True(captured.Settings.Enabled);
        Assert.True(captured.Settings.Options.SimulatePaper);
        Assert.False(captured.Settings.GamutWarnings);
        Assert.Equal(new PdfDocumentProofViewStamp(comparison.Id, owner.Id, 0, 1, true), captured.Stamp);
    }

    [Fact]
    public void SharedViewsFollowTheirOwnerAndLeaveOtherDocumentsAlone()
    {
        var owner = new PdfLogicalDocument();
        var first = new PdfDocumentProofView(owner);
        var second = new PdfDocumentProofView(owner);
        var other = new PdfDocumentProofView(new PdfLogicalDocument());

        var initial = first.CaptureProof();
        Assert.Same(owner, first.Document);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.ComparisonOverride);
        Assert.Equal(new PdfDocumentProofViewStamp(first.Id, owner.Id, 0, 0, false), initial.Stamp);
        Assert.False(initial.Settings.Enabled);

        owner.Proof = new PdfDocumentProofSettings(enabled: true, gamutWarnings: true);

        Assert.Equal(new PdfDocumentProofViewStamp(first.Id, owner.Id, 1, 0, false), first.CaptureProof().Stamp);
        Assert.Equal(new PdfDocumentProofViewStamp(second.Id, owner.Id, 1, 0, false), second.CaptureProof().Stamp);
        Assert.True(first.CaptureProof().Settings.Enabled);
        Assert.True(second.CaptureProof().Settings.GamutWarnings);
        Assert.Null(first.CaptureProof().Settings.PrinterProfile);
        Assert.False(initial.Settings.Enabled);
        Assert.False(initial.Settings.GamutWarnings);
        Assert.Equal(new PdfDocumentProofViewStamp(first.Id, owner.Id, 0, 0, false), initial.Stamp);
        Assert.False(other.CaptureProof().Settings.Enabled);
        Assert.Equal(new PdfDocumentProofViewStamp(other.Id, other.Document.Id, 0, 0, false), other.CaptureProof().Stamp);
    }
}
