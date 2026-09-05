using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FormOcrPolicyTests
{
    [Fact]
    public void MapRegions_MapsPdfCoordinatesAndTextConstraints()
    {
        PdfFormWidgetInfo widget = Widget("totalAmount", PdfFormFieldKind.Text,
            left: 10, bottom: 20, right: 60, top: 40, maximumLength: 8);

        PdfOcrFormRegion region = Assert.Single(
            PdfOcrFormLayout.MapRegions([widget], 200, 100));

        Assert.Equal((20, 60, 120, 80),
            (region.Left, region.Top, region.Right, region.Bottom));
        Assert.Equal(PdfOcrFormLayout.NumericWhitelist, region.CharacterWhitelist);
        Assert.Equal(8, region.MaximumLength);
    }

    [Fact]
    public void MapRegions_AppliesVisualPageRotation()
    {
        PdfFormWidgetInfo widget = Widget("name", PdfFormFieldKind.Text,
            left: 10, bottom: 20, right: 60, top: 40, pageRotation: 90);

        PdfOcrFormRegion region = Assert.Single(
            PdfOcrFormLayout.MapRegions([widget], 100, 200));

        Assert.Equal((20, 20, 40, 120),
            (region.Left, region.Top, region.Right, region.Bottom));
    }

    [Fact]
    public void ClosestChoice_CorrectsNearMatchButPreservesUnrelatedText()
    {
        string[] choices = ["Mathematics", "English", "Science"];

        Assert.Equal("Science", PdfOcrFormLayout.NormalizeChoice("Scienoe", choices));
        Assert.Equal("History", PdfOcrFormLayout.NormalizeChoice("History", choices));
    }

    [Fact]
    public void MapRegions_IdentifiesCombCellCount()
    {
        PdfFormWidgetInfo widget = Widget("studentId", PdfFormFieldKind.Text,
            left: 10, bottom: 20, right: 90, top: 40, maximumLength: 8) with
        {
            Flags = 1L << 24
        };

        PdfOcrFormRegion region = Assert.Single(
            PdfOcrFormLayout.MapRegions([widget], 200, 100));

        Assert.True(region.IsComb);
        Assert.Equal(8, region.MaximumLength);
    }

    private static PdfFormWidgetInfo Widget(string name, PdfFormFieldKind kind,
        double left, double bottom, double right, double top, int maximumLength = 0,
        int pageRotation = 0) => new()
        {
            PageIndex = 0,
            AnnotationIndex = 0,
            ObjectNumber = 1,
            Generation = 0,
            FieldName = name,
            FieldKind = kind,
            Flags = 0,
            Value = "",
            DefaultAppearance = "",
            MaximumLength = maximumLength,
            OnValue = "",
            HasAction = false,
            HasAppearanceState = false,
            Options = [],
            Left = left,
            Bottom = bottom,
            Right = right,
            Top = top,
            PageBoxLeft = 0,
            PageBoxBottom = 0,
            PageBoxWidth = 100,
            PageBoxHeight = 100,
            PageRotation = pageRotation
        };
}
