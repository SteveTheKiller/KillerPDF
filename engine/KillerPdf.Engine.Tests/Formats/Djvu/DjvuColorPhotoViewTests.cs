using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A color photo page shown smaller than itself is drawn from an IW44 decode at the coarsest subsample (2, 4 or 8)
// that still has at least as many samples as the output, so a 600 dpi color page draws its thumbnail and fitted view
// inside the viewer's 128 MiB, where its full-size decode needs about 240 MB.
public sealed class DjvuColorPhotoViewTests
{
    // c44 -dpi 100 of an owned 212 x 150 scan-like color page (KP-J2's generator: paper grain, text strokes, a red
    // heading, a photo band) with its defaults: three chunks (74, 15 and 10 slices), full chroma delayed 10 slices.
    // ddjvu -subsample=2 draws it as 106 x 75 RGB bytes with SHA-256 08F4AA5C...
    private const string EvenColorPage =
        "QVQmVEZPUk0AAASLREpWVUlORk8AAAAKANQAlhgAZAAWAUJHNDQAAACxAEoBAgDUAJaK/+2lyc3I6aenfKT6kv/8a/AtP/+HDn5OvnCU0LBg7j//+yTb" +
        "AqbRk3b/fEGaXh9A/Mb2RUZY3//////9NVWK9YcOVr6SN6r/7DmTOtw4bIIJd84/HnpoqN/NYFtxyRWvZGJ7FdJedfaLevgmy5R4AmEGUFRRAX2yEILt" +
        "QxUY7Ft0QsBvrwoXVvppXWjMzW359LbxWLTpUXNYrX52vmcUVnW9Nj9g764AAEJHNDQAAACeAQ/OgrnRl9jeh8qVsKsqYZI83WRLo+7wYKQ3kdP6CqiK" +
        "ji7WZuSnW+yzWgNz1Zij7I1tqO9yB8RPNOUikTdh8+7RJt4NZWjS8YQUQZrs9fZ+TG/VvPzoebWaQ3Ho6A60qdkO1h4FNehgAwvm0s7IGbd6Ji6oJ73h" +
        "k50oE0Cs7ubpoZKPhgJSLAyaiX+4/Obnqy+EvBMi+y7pI8uZK89CRzQ0AAADDQIK6/GqfD2/9dERHIT2Mrjo6id0ZcU8AGKnX+JEF+p1drwZGv8w2gR1" +
        "dNkxVt01bQywzVcGOzq+Hqg7yDa2rBY81OIFA5MpO2W53xjwiN7a0JWyCypBEVAHNk6sBPY3lPUIyilJIwEIgAA8vwmSoJWRmobaBmUrDAeOIAFibNyO" +
        "GIjy6riFYu38sOYnbNdaC1yQOm/YpR1gpA+uyQ1Raccaw2BevvHKswZkV/ZbKqp7x34JAqGnaPOWbUJc6sNiQDuZmMQH67Ly457ZphXVsmb8/Rb5/dMp" +
        "buAG4W2+fOy4j8eQfzG58YbOaR89uTLhX+2wl+ZIUxGVcqiXpz8GdMcYTf6Aoa5wph/jXvMFU75BG0vIvt2N32CQX6nm2N0ZQfgF10FPvAhqAYPTyE/b" +
        "Jkrm7h2vZEIWGy82Fd2PNIabISdHoAvYOo037faw2XM9gc4HHE/5bXhzIpjhdF3eOwz+pSBGLfCsmsFpWmcC9Uo/pQMPLTdSqeA9twgakorxEa1ONqqO" +
        "nMDVgkFejI+yKjKD2P5jV4f3K6Df44G1J+njTEXE3ctUuCiRqywGCGV8Ibaz/12ROQVytfM4xzGjnJU1mx2eH074IQ7xyiWSx/Q5+4Z8LrfxpolZCDRp" +
        "0qtAlfau0T6cvLB3P7izBsb70wglwo2zIbUURlFD8JWpsSV38XJl9Ej8bd5ThKh+cEvfziBqmE2WTaGMmzvJBIJxlH9V3iG0waYzT3B6K/IfvC5scbEl" +
        "dYlS4eAAKWJKGsdGgjSeRMB9WRChAJeLBC0ImrA/lUz23etfQbTwM/Oy4LDTrl9lMidQmmA8rFMc0eqWXm2PjMZ7dDu9KwVGciD/Me+UA7alzokf06Hs" +
        "FnYJFJ4uX8kgzW3CApEmfEPos8tDciQVUYR7pQsm1PPhSNnpC6kcOHG+kNhdRAumb4e3V56+vLygW3Wze9yqBaonvy5CK/gK/0Ztp2sqVi66PlnfLvsA" +
        "PdfRdSOFIlCdqtDUtGVOejf1ayzjHgd1jOk82ua3TTiMaIeTdT6+1domias=";

    // At exactly half size each output pixel is one subsampled sample, so the drawn page, read back as the page's
    // gamma-2.2 bytes and turned upright, is ddjvu's half-size image byte for byte, in every orientation.
    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    public void AColorPhotoPageShownAtHalfItsSizeDrawsTheReferenceDecodersHalfSizeImage(byte orientation)
    {
        var data = Convert.FromBase64String(EvenColorPage);
        Assert.Equal("4973860AF9D00A828ECC579882DA85FBB67E201741CBA93B90ACD843E9A95BF3", Convert.ToHexString(SHA256.HashData(data)));
        data[33] = orientation;
        bool quarter = orientation is 5 or 6;
        var snapshot = PdfReadSnapshot.OpenDjvu(data);

        var frame = snapshot.RenderPage(0, new PdfRenderOptions(quarter ? 75 : 106, quarter ? 106 : 75));

        var pixels = frame.Pixels.ToArray();
        var upright = new byte[106 * 75 * 3];
        for (int sy = 0; sy < 75; sy++)
            for (int sx = 0; sx < 106; sx++)
            {
                // Where stored (sx, sy) shows, as DjvuCompoundTests.Turned places it.
                var (x, y) = orientation switch
                {
                    2 => (105 - sx, 74 - sy),
                    5 => (74 - sy, sx),
                    6 => (sy, 105 - sx),
                    _ => (sx, sy)
                };
                for (int c = 0; c < 3; c++)
                    upright[(sy * 106 + sx) * 3 + c] = (byte)Math.Round(Math.Pow(pixels[(y * frame.Width + x) * 4 + c], 1 / 2.2) * 255);
            }
        Assert.Equal("08F4AA5CB081A92C5E644279B09582444057A076E184C801DA8D2234280DE931", Convert.ToHexString(SHA256.HashData(upright)));
    }

    // The KP-J2 pseudo-random 600 dpi letter page (c44's chunk shape, 5100 x 6600): a thumbnail and a page fitted at
    // 2048 pixels draw at the viewer's 128 MiB, which counts what is live at once. The fit decodes at subsample 2
    // (2550 x 3300 samples for 1583 x 2048). In all it allocates 50.9 MB of kept coefficients and 19.1 MB of two-bit
    // states, three 8.4 MB planes, 25.2 MB of RGB, 16.8 MB of chunk copies, then 51.9 MB of output floats and their
    // 51.9 MB owned copy: about 241 MB (230 MiB), never all at once.
    [Fact]
    public void A600DpiColorPhotoPageDrawsAThumbnailAndAFittedPageInsideTheViewerBudget()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(ColorPage(5100, 6600, 6u));

        var thumbnail = snapshot.RenderPage(0, new PdfRenderOptions(396, 512, maximumPixelBytes: 128L << 20));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var fit = snapshot.RenderPage(0, new PdfRenderOptions(1583, 2048, maximumPixelBytes: 128L << 20));
        long fitBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((396, 512), (thumbnail.Width, thumbnail.Height));
        Assert.Equal((1583, 2048), (fit.Width, fit.Height));
        Assert.True(fitBytes < 240L << 20, $"the fitted page allocated {fitBytes >> 20} MiB");
    }

    // A reduced decode keeps the viewer's rule that a region is exactly that crop of the whole resized page, and a
    // turned page is the upright one turned, with the page's last partial row and column of samples in place.
    [Theory]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    [InlineData((byte)2)]
    public void AReducedColorPageTurnsAndCropsLikeTheWholePage(byte orientation)
    {
        // 211 x 149: at subsample 4, 53 x 38 samples with a partial last column (3 pixels) and row (1 pixel).
        var upright = PdfReadSnapshot.OpenDjvu(OddColorPage(1));
        var turned = PdfReadSnapshot.OpenDjvu(OddColorPage(orientation));
        bool quarter = orientation is 5 or 6;
        int w = quarter ? 37 : 52, h = quarter ? 52 : 37;

        var whole = upright.RenderPage(0, new PdfRenderOptions(52, 37)).Pixels.ToArray();
        var shown = turned.RenderPage(0, new PdfRenderOptions(w, h)).Pixels.ToArray();
        var region = turned.RenderPageRegion(0, new PdfRenderOptions(w, h), 3, 4, w - 6, h - 9).Pixels.ToArray();

        for (int y = 0; y < 37; y++)
            for (int x = 0; x < 52; x++)
            {
                var (tx, ty) = orientation switch { 2 => (51 - x, 36 - y), 5 => (36 - y, x), _ => (y, 51 - x) };
                for (int c = 0; c < 3; c++)
                    Assert.Equal(whole[(y * 52 + x) * 4 + c], shown[(ty * w + tx) * 4 + c], 5);
            }
        for (int y = 0; y < h - 9; y++)
            for (int x = 0; x < w - 6; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(shown[((y + 4) * w + x + 3) * 4 + c], region[(y * (w - 6) + x) * 4 + c]);
    }

    private static byte[] OddColorPage(byte orientation)
    {
        var data = Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color);
        data[33] = orientation;
        return data;
    }

    private static byte[] ColorPage(int width, int height, uint seed)
    {
        var chunks = DjvuIw44RealPageTests.CodedChunks(color: true, width, height, 0x8a, seed, payloadDivisor: 2);
        byte[] info = [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 24, 0, 0x58, 0x02, 22, 1];
        var form = new List<byte>("DJVU"u8.ToArray());
        form.AddRange(Chunk("INFO", info));
        foreach (var chunk in chunks) form.AddRange(Chunk("BG44", chunk.ToArray()));
        return [.. "AT&T"u8, .. Chunk("FORM", [.. form])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8),
            (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
