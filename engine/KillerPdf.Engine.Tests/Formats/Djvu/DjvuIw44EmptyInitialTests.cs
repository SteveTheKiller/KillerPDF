using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44EmptyInitialTests
{
    [Fact]
    public void RendersEveryPixelAcrossTheCompleteFrozenWhiteImage()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000044444A5655494E464F0000000A00410025180064001601" +
            "424734340000000E001F81020041002500CDA5E4B19F4247343400000002010A4247343400000006023BFFFFFFFF");
        Assert.Equal("B45DFC4BA20A8049BA01E9920522797D7CF7D2AF5137BC82937313C89A69F07F",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var frame = document.RenderPage(0);
        Assert.Equal(65, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(9620, frame.Pixels.Length);
        // The unchanged public CLI returns uniform white, also the literal source endpoint.
        for (var component = 0; component < frame.Pixels.Length; component++)
            Assert.Equal(1f, frame.Pixels[component]);
    }

    [Fact]
    public void RendersEveryPixelAcrossTheCompleteFrozenMidgrayImage()
    {
        var input = Convert.FromHexString(
            "41542654464F524D0000003D444A5655494E464F0000000A00410025180064001601" +
            "4247343400000009001F81020041002500004247343400000002010A4247343400000003023BFF");
        Assert.Equal("08327A19A1E70EF451CB8E8213A31B5C24E8B2E08D927824D378FA7471648FF7",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var frame = document.RenderPage(0);
        Assert.Equal(65, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(9620, frame.Pixels.Length);
        for (var pixel = 0; pixel < 2405; pixel++)
        {
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4]);
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void RendersEveryPixelWhenTheInitialChunkHasOnlyItsNineByteHeader()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000028444A5655494E464F0000000A00410025180064001601" +
            "4247343400000009001F8102004100250000");
        Assert.Equal("818552073EDA8921329EF49E4C71C47CFF01F98EB96525F8594CA4EA3DF08BDA",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        var page = Assert.Single(document.Pages);
        Assert.Equal(65, page.Width);
        Assert.Equal(37, page.Height);
        var frame = document.RenderPage(0);
        Assert.Equal(65, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(9620, frame.Pixels.Length);
        // Both public reference outputs contain 2405 literal gray samples of 127.
        // Decimal precision 60 independently freezes the declared gamma 2.2 result.
        for (var pixel = 0; pixel < 2405; pixel++)
        {
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4]);
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(0.2157644f, frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }
}
