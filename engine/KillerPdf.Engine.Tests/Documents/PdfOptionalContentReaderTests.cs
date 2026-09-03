using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
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
    }

    [Fact]
    public void ReadReturnsEmptyModelWhenDocumentHasNoLayers()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfOptionalContentInfo result = PdfOptionalContentReader.Read(document);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Configurations);
    }
}
