using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuAnnotationRenderingTests
{
    // DjvuCompoundTests' palette page with an annotation chunk appended, uncompressed (ANTa) and BZZ-compressed by
    // DjVuLibre bzz (ANTz). The annotation sets a red background, page zoom and a highlighted hyperlink area; ddjvu
    // draws both pages exactly as it draws the page without it.
    private const string WithAnta =
        "41542654464F524D0000010E444A5655494E464F0000000A0060004018002C011601536A627A00000020817B4FC3A17032BCDF26753F5D125A81" +
        "4BB297F70B21C3F4061A686F5971159F4647627A0000001C800004000000D22814141EDCC8C8C8000007FFFFF081ED9BA2CB83BF424734340000" +
        "000E004A81020008000600963C2AFD7F4247343400000003010FBF004247343400000002020A414E546100000077286261636B67726F756E6420" +
        "23666630303030290A287A6F6F6D2070616765290A286D6170617265612022687474703A2F2F6578616D706C652E696E76616C69642F22202222" +
        "2028726563742030203020363020343029202868696C69746520233030666630302920286F70616369747920383029290A00";

    private const string WithAntz =
        "41542654464F524D000000FE444A5655494E464F0000000A0060004018002C011601536A627A00000020817B4FC3A17032BCDF26753F5D125A81" +
        "4BB297F70B21C3F4061A686F5971159F4647627A0000001C800004000000D22814141EDCC8C8C8000007FFFFF081ED9BA2CB83BF424734340000" +
        "000E004A81020008000600963C2AFD7F4247343400000003010FBF004247343400000002020A414E547A00000067FFFF87FAD01068DD06033404" +
        "CAB4C8608AEC9B967E1E6FCDA71EAB9CB2D6E634882441C51CD6C4D9BA3611CE965A85037A40F65FFF120764578071A76EC79ED4C2FDB88C9675" +
        "68D2F4733747E39199AD45D96F562F7B967B709D032D4303C9049FC6F4766306C700";

    [Theory]
    [InlineData(WithAnta)]
    [InlineData(WithAntz)]
    public void AnnotationsDoNotChangeOrStopTheDrawnPage(string page)
    {
        var frame = DjvuDocument.Open(Convert.FromHexString(page)).RenderPage(0);
        DjvuCompoundTests.AssertColors(DjvuCompoundTests.AuthoredColors(), frame, 1);
    }

    [Fact]
    public void AnnotationsDoNotStopABitonalPageFromDrawingOrConverting()
    {
        // The 3x1 Smmr mask of DjvuRenderingTests (white, black, black) with an annotation.
        var document = DjvuDocument.Open(DjvuJb2Tests.Page(3, 1, 1,
            DjvuJb2Tests.Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]),
            DjvuJb2Tests.Chunk("ANTa", "(zoom page)"u8.ToArray())));
        Assert.Equal(new float[] { 1, 0, 0 }, document.RenderPage(0).Pixels.ToArray().Where((_, i) => i % 4 == 0));
        Assert.NotEmpty(document.ConvertToPdf());
    }
}
