using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44RawEqualityTests
{
    [Fact]
    public void PreservesTheNativeLeftColumnAcrossARawArithmeticEquality()
    {
        var input = Convert.FromBase64String(EncodedImage);
        var expected = Convert.FromBase64String(ExpectedLeftColumn);
        Assert.Equal("D8242D4904858D7145ECBFE6032CC308D3013650184E8AF6391C969B044DFE13", Convert.ToHexString(SHA256.HashData(input)));
        Assert.Equal("1EFC2B1FB4BEABF69352DAF2DBB3D944E41E5DEC8354D600180B3A04DC7430CB", Convert.ToHexString(SHA256.HashData(expected)));
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(65, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(37, expected.Length);
        var differences = new List<string>();
        // This frozen native-reference column isolates the arithmetic boundary
        // from the separately diagnosed right-edge reconstruction difference.
        for (int row = 0; row < expected.Length; row++)
        {
            float linear = (float)Math.Pow(expected[row] / 255d, 2.2);
            int offset = row * 65 * 4;
            if (frame.Pixels[offset] != linear || frame.Pixels[offset + 1] != linear
                || frame.Pixels[offset + 2] != linear || frame.Pixels[offset + 3] != 1f)
                differences.Add($"row{row}: expected={linear:R}, actual={frame.Pixels[offset]:R}");
        }
        Assert.True(differences.Count == 0, $"Differing left-column pixels: {differences.Count}/37\n" + string.Join("\n", differences));
    }

    private const string EncodedImage = "QVQmVEZPUk0AAAL+REpWVUlORk8AAAAKAEEAJRgAZAAWAUJHNDQAAAARAB+BAgBBACUA5MhxbVXpi08AQkc0NAAAAAsBCvr2X94Dbn8nfwBCRzQ0AAACsgIv5KfnD55lMMeNLm9jSJRePtixrU7zmMhVqD6vUY+a5la5ZgPZ8W+umvZSd41Ciz1WVKyZeSXrajOCQ8CMs9iYYwyxJI9KhEeoG1TZhtllr+7B4BALh+BiDVtLYH459lrmbhDdFoDKpXH9BTU3XLEmU+PYmbdjMRqVNmrb/Ebe10GUEIa23zsFRQJfFd2It/kMi09d0SCfRUs+ir9sIT6E8YqlqnVTwUcPLyp6QPJBmkCKXlDcOfUf3v7QNM6y80UAmoH2JvPfh9wIjh31oB6evzftGWrohiueA4kWMv11e4yecUpagdrB0xbJUFLA9E1a6uppAPsuV41NPOMBB6Nv4fLTPa/KOGHySTbE+kMQoh+jOM+PR7ujxbI5z/tPvglLM7FEXv0Tsvx3tGnJxVtO/NOb25vZQUDfc9CJQuHUCTeSAzGaHtIgKyLudlB0S7K6RuT8U7JJysUTuDeN8m4pevJ5oaXAR5IBfJbTyZhxm7ZSzHC6Y7lwKDGbPIu78vbZ/dPApKjYVNIljcmXtXdeRYaqXwKBAzu3rwSPqtSPrr9KD4Yn6OkN/0VmWWQMzi0OwKy8NStcZeX04oJjwDRG69wyPKYGYjXkZqWPpgd14wu24lBz0ZKiFpRA5FToypzkPJN8vySWKplvWi/SZReErrRKWx8iP88z7Q4ntIn0TkcBJf7096DxiSv/LyBzcj/nPN8v2uRq6p1v7YiOUYwL2izmkLodxbF34Xxonmr46Uz+VfvEgdXohrcljs7A9xWIey28HXiaISgsxyArPgGPQRNiTmVr5/1qrX6ldSvjRo077yR3juchWXQOUij50v6Hly/hA7BHOCf3RA+fBB9U3P4fzb43jqVGBCfdFMe++OCSXZ0r2QXsgABGEIfdsT6uiXbxWXxrCsFiDnxXQLuIfw==";
    private const string ExpectedLeftColumn = "AwsTMFBjbGRbXWmGpbrFv7bDwNUGCR0YERAXMlBlcm5naHKPqw==";
}
