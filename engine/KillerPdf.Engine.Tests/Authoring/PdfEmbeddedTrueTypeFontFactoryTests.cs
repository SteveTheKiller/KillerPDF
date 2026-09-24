using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Authoring;

public sealed class PdfEmbeddedTrueTypeFontFactoryTests
{
    [Fact]
    public void CreateUsesIdentityEncodingWhenCharacterCodesMatchGlyphs()
    {
        EmbeddedTrueTypeFontObjects objects = Create(
            new Dictionary<ushort, EmbeddedCharacterMapping>
            {
                [1] = new(1, "A")
            });

        Assert.Equal("Identity-H", Assert.IsType<PdfName>(
            objects.Type0[Name("Encoding")]).ValueAsLatin1());
    }

    [Fact]
    public void CreateKeepsCustomEncodingWhenCharacterCodesDifferFromGlyphs()
    {
        EmbeddedTrueTypeFontObjects objects = Create(
            new Dictionary<ushort, EmbeddedCharacterMapping>
            {
                [1] = new(1, "A"),
                [2] = new(1, "B")
            });

        Assert.Equal(6, Assert.IsType<PdfIndirectReference>(
            objects.Type0[Name("Encoding")]).ObjectNumber);
    }

    private static EmbeddedTrueTypeFontObjects Create(
        IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> mappings)
    {
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        return PdfEmbeddedTrueTypeFontFactory.Create(font, mappings,
            new PdfIndirectReference(1, 0), new PdfIndirectReference(2, 0),
            new PdfIndirectReference(3, 0), new PdfIndirectReference(4, 0),
            new PdfIndirectReference(5, 0), new PdfIndirectReference(6, 0));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
