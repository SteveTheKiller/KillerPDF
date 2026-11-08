using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A DeviceCMYK luminosity soft mask is an alpha computed from ink amounts, not a displayed color,
// so it does not follow the working CMYK profile. Masks made by Illustrator fill their background
// with 0 0 0 1 k and expect it to close the mask (Ghent GWG16.10); 0 0 0 0 must leave it fully open.
// Each case runs the direct byte path (no transfer function or /Identity) and the general path
// (an identity transfer function), with the built-in and a chosen working profile.
public sealed class PdfWorkingCmykSoftMaskTests
{
    // Same synthetic affine CMYK profile (A2B1 only) as PdfWorkingCmykByteRenderTests.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    private const string FunctionTransfer = "/TR << /FunctionType 2 /Domain [0 1] /Range [0 1] /N 1 /C0 [0] /C1 [1] >>";

    // Black ink 100 percent on the left half closes the mask: red is hidden and the page stays
    // white. The 0 0 0 0 backdrop on the right half has luminosity 1: pure red shows.
    [Theory]
    [InlineData("", false)]
    [InlineData("/TR /Identity", false)]
    [InlineData(FunctionTransfer, false)]
    [InlineData("", true)]
    [InlineData(FunctionTransfer, true)]
    public void BlackInkClosesADeviceCmykLuminosityMaskAndPaperWhiteOpensIt(string transfer, bool chosenProfile)
    {
        PdfDocument document = PdfDocument.Open(MaskedRed("DeviceCMYK", "0 0 0 1 k", "[0 0 0 0]", transfer));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, Options(chosenProfile));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 2, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 7, 5));
        Assert.Empty(page.Diagnostics);
    }

    // The ink formula per channel is (1 - C)(1 - K): black ink 128 gives (255 - 128) = 127 on
    // every channel, luminosity 127, so red at 127/255 over white leaves green and blue at 128.
    [Theory]
    [InlineData("", false)]
    [InlineData(FunctionTransfer, false)]
    [InlineData("", true)]
    [InlineData(FunctionTransfer, true)]
    public void HalfBlackInkGivesTheInkComponentLuminosity(string transfer, bool chosenProfile)
    {
        PdfDocument document = PdfDocument.Open(MaskedRed("DeviceCMYK", "0 0 0 0.5 k", "[0 0 0 0]", transfer));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, Options(chosenProfile));

        Assert.Equal([128, 128, 255, 255], Pixel(page, 2, 5));
    }

    // Gray carries a black ink as well, but a gray group's luminosity is its gray: 0.25 g gives
    // luminosity 0.25, mask 64, so green and blue 255 - 64 = 191, whatever the working profile.
    [Theory]
    [InlineData("", false)]
    [InlineData(FunctionTransfer, false)]
    [InlineData("", true)]
    [InlineData(FunctionTransfer, true)]
    public void AGrayGroupMaskUsesItsGrayNotABlackInk(string transfer, bool chosenProfile)
    {
        PdfDocument document = PdfDocument.Open(MaskedRed("DeviceGray", "0.25 g", "[0.25]", transfer));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, Options(chosenProfile));

        Assert.Equal([191, 191, 255, 255], Pixel(page, 2, 5));
        Assert.Equal([191, 191, 255, 255], Pixel(page, 7, 5));
    }

    private static PdfRenderOptions Options(bool chosenProfile) => chosenProfile
        ? new PdfRenderOptions(10, 10) { WorkingCmykProfile = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk)) }
        : new PdfRenderOptions(10, 10);

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    // Red paints the whole 10 x 10 page through a luminosity mask whose group paints the left half.
    private static byte[] MaskedRed(string space, string color, string backdrop, string transfer)
    {
        string mask = color + " 0 0 5 10 re f";
        const string paint = "q /M gs 1 0 0 rg 0 0 10 10 re f Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ExtGState << /M 4 0 R >> >> /Contents 6 0 R >>",
            $"<< /Type /ExtGState /SMask << /S /Luminosity /G 5 0 R /BC {backdrop} {transfer} >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> /Group << /Type /Group /S /Transparency /CS /{space} >> /Length {mask.Length} >>\nstream\n{mask}\nendstream",
            $"<< /Length {paint.Length} >>\nstream\n{paint}\nendstream",
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
