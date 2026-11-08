using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44MultiblockTests
{
    [Fact]
    public void RendersAllPixelsOfOwnedNonSquareMultiBlockImage()
    {
        var input = Convert.FromBase64String(EncodedImage);
        var expected = Convert.FromBase64String(ExpectedGray);
        Assert.Equal("0FD0CEB1FCA75103AB87A284A90FB2CDB6F2C728AB3629E3D4B9683C536B6AF0",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        Assert.Equal("C31CA920C706D7C77D63A49729428B83C1FDC0F6507946F458FA1BD5400D76F3",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)));
        var document = DjvuDocument.Open(input);
        var page = Assert.Single(document.Pages);
        Assert.Equal(65, page.Width);
        Assert.Equal(37, page.Height);
        var frame = document.RenderPage(0);
        Assert.Equal(65, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(9620, frame.Pixels.Length);
        var mismatches = new List<string>();
        // The oracle is DjVuLibre 3.5.29 ddjvu -format=pgm output for this input, not
        // DjVu.js, whose bytes differed from it at 2125 of 2405 pixels. The independent
        // bytes receive the declared INFO gamma of 2.2.
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            var linear = (float)Math.Pow(expected[pixel] / 255.0, 2.2);
            var offset = pixel * 4;
            if (frame.Pixels[offset] != linear || frame.Pixels[offset + 1] != linear ||
                frame.Pixels[offset + 2] != linear || frame.Pixels[offset + 3] != 1f)
                mismatches.Add($"({pixel % 65},{pixel / 65}) gray={expected[pixel]} expected={linear:R} actual=({frame.Pixels[offset]:R},{frame.Pixels[offset + 1]:R},{frame.Pixels[offset + 2]:R},{frame.Pixels[offset + 3]:R})");
        }
        Assert.True(mismatches.Count == 0,
            $"Differing pixels: {mismatches.Count} of {expected.Length}.\n" + string.Join("\n", mismatches));
    }

    private const string EncodedImage = "QVQmVEZPUk0AAATHREpWVUlORk8AAAAKAEEAJRgAZAAWAUJHNDQAAAARAB+BAgBBACUA5MhxbVXpi08AQkc0NAAAAAsBCvr2X94Dbn8nfwBCRzQ0AAAEewI75KfnD55lMMeNLm9jSJRePtixrU7zmMhVqD6vUY+a5la5ZgPZ8W+umvZSd41Ciz1WVKyZeSXrajOCQ8CMs9iYYwyxJI9KhEeoG1TZhtllr+7B4BALh+BiDVtLYH459lrmbhDdFoDKpXH9BTU3XLEmU+PYmbdjMRqVNmrb/Ebe10GUEIa23zsFRQJfFd2It/kMi09d0SCfRUs+ir9sIT6E8YqlqnVTwUcPLyp6QPJBmkCKXlDcOfUf3v7QNM6y80UAmoH2JvPfh9wIjh31oB6evzftGWrohiueA4kWMv11e4yecUpagdrB0xbJUFLA9E1a6uppAPsuV41NPOMBB6Nv4fLTPa/KOGHySTbE+kMQoh+jOM+PR7ujxbI5z/tPvglLM7FEXv0Tsvx3tGnJxVtO/NOb25vZQUDfc9CJQuHUCTeSAzGaHtIgKyLudlB0S7K6RuT8U7JJysUTuDeN8m4pevJ5oaXAR5IBfJbTyZhxm7ZSzHC6Y7lwKDGbPIu78vbZ/dPApKjYVNIljcmXtXdeRYaqXwKBAzu3rwSPqtSPrr9KD4Yn6OkN/0VmWWQMzi0OwKy8NStcZeX04oJjwDRG69wyPKYGYjXkZqWPpgd14wu24lBz0ZKiFpRA5FToypzkPJN8vySWKplvWi/SZReErrRKWx8iP88z7Q4ntIn0TkcBJf7096DxiSv/LyBzcj/nPN8v2uRq6p1v7YiOUYwL2izmkLodxbF34Xxonmr46Uz+VfvEgdXohrcljs7A9xWIey28HXiaISgsxyArPgGPQRNiTmVr5/1qrX6ldSvjRo077yR3juchWXQOUij50v6Hly/hA7BHOCf3RA+fBB9U3P4fzb43jqVGBCfdFMe++OCSXZ0r2QXsgABGEIfdsT6uiXbxWXxrCsFiDnxXQLuIZNbjX0emLTVF2TBeRyeeBsUciPSX6wpA0GeXZDvtyzq+qyXY0Jnc7LHDah5IO0egoHrt0rPmMHxHUbseGsXuY5DjBYtpOYc4mzPp8tTDC5MhZPrLWit7ZdwcOqC+CLnZL01uJQdxUCYyD4OZtxTcO2Yw4XXG+/7eUhW6LzqCZOG7MEaddRVtAFNzT7bs6h61QTXpyH+INRp1iKStCnMV5GYFmQZsMpyjWvyfTNPsyJgADZjoNVQ7r3GWu/zjc48aoR4O387y14bKzshPG15l/SO26c6Iozgt8wq+Z2MSqdM/Q9gBVoMkGXv7+1fJFbIwWIKNpPyQAAABudzbeUMyo7XIOA4Z8w8620tYCt9EF4177GwsBs43AKAAABuKM4giQqfPSQRRL2LI/u2ud6KkKj/ME4XJiQGiMfjXq6Jq1UY5bq+6749xDbxX9TWftIk99yQpctxN680I8G+c8LYqMLfvoc1XHc4/nVqmsMLgsHuuTrL6Iaz6KdY8yQxvxDxSlSviu3Ws19M/NRXKTgaXHDwqKCVu9h07qrHPc5PYAbhM6/i8CAtFHzaAOYt0iP7cHKN5tNm57gBmUc6UaS+7IvPkBOHGK8uJ5b8=";
    private const string ExpectedGray = "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";
}
