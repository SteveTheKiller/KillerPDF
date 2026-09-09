using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageCropIntersectionTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 90, true)]
    [InlineData(true, 180, true)]
    [InlineData(true, 270, false)]
    public void OversizedCropRendersItsIntersectionWithoutChangingDeclaredBoxes(
        bool partial, int rotation, bool inherited)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 40, 80).Fill()
            .SetFillRgb(0, 0, 1).Rectangle(90, 100, 110, 200).Fill();
        var builder = new PdfDocumentBuilder().AddPage(200, 300, content).SetPageRotation(0, rotation);
        PdfDocument source = PdfDocument.Open(builder.Build());
        PdfPageTree tree = PdfPageTree.Read(source);
        PdfIndirectReference target = inherited ? tree.RootReference : tree.Pages[0].Reference;
        var dictionary = (PdfDictionary)source.Resolve(target);
        var rawCrop = new PdfArray([new PdfInteger(-10), new PdfInteger(partial ? 20 : -20),
            new PdfInteger(partial ? 180 : 220), new PdfInteger(330)]);
        var replacement = new PdfDictionary(dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(new PdfName("CropBox"u8), rawCrop)));
        PdfDocument actualDocument = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(target.ObjectNumber, replacement).Build());
        PdfDocument expectedDocument = PdfDocument.Open(builder
            .SetPageBox(0, PdfPageBox.Crop, 0, partial ? 20 : 0, partial ? 180 : 200, partial ? 280 : 300).Build());

        PdfPageInformation page = Assert.Single(PdfPageInformation.Read(actualDocument));
        Assert.Equal(0, page.Left);
        Assert.Equal(partial ? 20 : 0, page.Bottom);
        Assert.Equal(partial ? 180 : 200, page.Width);
        Assert.Equal(partial ? 280 : 300, page.Height);
        PdfPageBoxInformation declared = Assert.Single(PdfPageBoxInformation.Read(actualDocument));
        Assert.Equal(-10, declared.CropBox.Left);
        Assert.Equal(330, declared.CropBox.Top);
        var options = new PdfRenderOptions(200, 300);
        Assert.Equal(new PdfPageRenderer(expectedDocument).Render(0, options).Pixels.ToArray(),
            new PdfPageRenderer(actualDocument).Render(0, options).Pixels.ToArray());
    }
}
