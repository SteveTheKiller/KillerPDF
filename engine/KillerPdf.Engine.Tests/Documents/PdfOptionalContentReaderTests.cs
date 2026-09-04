using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOptionalContentReaderTests
{
    [Fact]
    public void ReadReturnsNamedGroupsAndDefaultVisibility()
    {
        var hidden = new PdfOptionalContentGroup("Measurements", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup(
            "Artwork", visibleWhenPrinting: false, visibleWhenExporting: true);
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(hidden).Rectangle(0, 0, 10, 10).Stroke().EndMarkedContent()
            .BeginOptionalContent(visible).Rectangle(20, 20, 10, 10).Fill().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());

        PdfOptionalContentInfo result = PdfOptionalContentReader.Read(document);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("Artwork", result.Groups[0].Name);
        Assert.False(result.Groups[0].IsVisibleWhenPrinting);
        Assert.True(result.Groups[0].IsVisibleWhenExporting);
        Assert.True(result.Groups[0].IsInitiallyVisible);
        Assert.Equal("Measurements", result.Groups[1].Name);
        Assert.False(result.Groups[1].IsInitiallyVisible);
        PdfOptionalContentConfigurationInfo configuration = Assert.Single(result.Configurations);
        Assert.True(configuration.IsDefault);
        Assert.Equal(PdfOptionalContentBaseState.On, configuration.BaseState);
        Assert.Contains(result.Groups[0].ObjectNumber, configuration.VisibleGroupObjectNumbers);
        Assert.DoesNotContain(result.Groups[1].ObjectNumber, configuration.VisibleGroupObjectNumbers);
        Assert.Equal(result.Groups.Select(group => group.ObjectNumber),
            configuration.DisplayOrderGroupObjectNumbers);
    }

    [Fact]
    public void ReadReturnsEmptyModelWhenDocumentHasNoLayers()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfOptionalContentInfo result = PdfOptionalContentReader.Read(document);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Configurations);
    }

    [Fact]
    public void RenameGroupPreservesLayerStateAndOriginalDocument()
    {
        var layer = new PdfOptionalContentGroup("Original", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        PdfOptionalContentGroupInfo originalGroup = Assert.Single(
            PdfOptionalContentReader.Read(original).Groups);

        PdfDocument renamed = PdfDocument.Open(PdfOptionalContentEditor.RenameGroup(
            original, originalGroup.ObjectNumber, "Résumé"));
        PdfOptionalContentGroupInfo renamedGroup = Assert.Single(
            PdfOptionalContentReader.Read(renamed).Groups);

        Assert.Equal("Résumé", renamedGroup.Name);
        Assert.False(renamedGroup.IsInitiallyVisible);
        Assert.True(renamedGroup.IsVisibleWhenPrinting);
        Assert.False(renamedGroup.IsVisibleWhenExporting);
        Assert.Equal("Original", Assert.Single(PdfOptionalContentReader.Read(original).Groups).Name);
    }

    [Fact]
    public void UsageVisibilityCanBeChangedAndClearedIndependently()
    {
        var layer = new PdfOptionalContentGroup("Artwork",
            visibleWhenPrinting: false, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetPrintVisibility(original, objectNumber, true));
        PdfOptionalContentGroupInfo changedGroup = Assert.Single(
            PdfOptionalContentReader.Read(changed).Groups);
        Assert.True(changedGroup.IsVisibleWhenPrinting);
        Assert.False(changedGroup.IsVisibleWhenExporting);

        PdfDocument cleared = PdfDocument.Open(
            PdfOptionalContentEditor.SetPrintVisibility(changed, objectNumber, null));
        PdfOptionalContentGroupInfo clearedGroup = Assert.Single(
            PdfOptionalContentReader.Read(cleared).Groups);
        Assert.Null(clearedGroup.IsVisibleWhenPrinting);
        Assert.False(clearedGroup.IsVisibleWhenExporting);
    }

    [Fact]
    public void InitialVisibilityCanBeChangedWithoutChangingUsageState()
    {
        var layer = new PdfOptionalContentGroup("Artwork", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument visible = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, objectNumber, true));
        PdfOptionalContentGroupInfo visibleGroup = Assert.Single(
            PdfOptionalContentReader.Read(visible).Groups);
        Assert.True(visibleGroup.IsInitiallyVisible);
        Assert.True(visibleGroup.IsVisibleWhenPrinting);
        Assert.False(visibleGroup.IsVisibleWhenExporting);

        PdfDocument hidden = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(visible, objectNumber, false));
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(hidden).Groups)
            .IsInitiallyVisible);
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(original).Groups)
            .IsInitiallyVisible);
    }

    [Fact]
    public void LayerLockCanBeEnabledAndDisabled()
    {
        var layer = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument locked = PdfDocument.Open(
            PdfOptionalContentEditor.SetLocked(original, objectNumber, true));
        Assert.True(Assert.Single(PdfOptionalContentReader.Read(locked).Groups).IsLocked);

        PdfDocument unlocked = PdfDocument.Open(
            PdfOptionalContentEditor.SetLocked(locked, objectNumber, false));
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(unlocked).Groups).IsLocked);
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(original).Groups).IsLocked);
    }

    [Fact]
    public void DisplayOrderCanBeReplaced()
    {
        var first = new PdfOptionalContentGroup("First");
        var second = new PdfOptionalContentGroup("Second");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(first).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(second).Rectangle(20, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        int[] reversed = [.. before.Configurations.Single()
            .DisplayOrderGroupObjectNumbers.Reverse()];

        PdfDocument reordered = PdfDocument.Open(
            PdfOptionalContentEditor.SetDisplayOrder(original, reversed));

        Assert.Equal(reversed, PdfOptionalContentReader.Read(reordered)
            .Configurations.Single().DisplayOrderGroupObjectNumbers);
        Assert.Throws<ArgumentException>(() =>
            PdfOptionalContentEditor.SetDisplayOrder(original, [reversed[0]]));
    }

    [Fact]
    public void DefaultConfigurationMetadataCanBeSetAndCleared()
    {
        var layer = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());

        PdfDocument named = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultConfigurationMetadata(
                original, "Press review", "KillerPDF"));
        PdfOptionalContentConfigurationInfo configuration =
            Assert.Single(PdfOptionalContentReader.Read(named).Configurations);

        Assert.Equal("Press review", configuration.Name);
        Assert.Equal("KillerPDF", configuration.Creator);
        PdfDocument cleared = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultConfigurationMetadata(named, null, null));
        PdfOptionalContentConfigurationInfo clearedConfiguration =
            Assert.Single(PdfOptionalContentReader.Read(cleared).Configurations);
        Assert.Null(clearedConfiguration.Name);
        Assert.Null(clearedConfiguration.Creator);
    }
}
