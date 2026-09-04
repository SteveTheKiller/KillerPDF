using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaBarcodeTests
{
    [Fact]
    public void TemplatePreservesDeclaredBarcodeParameters()
    {
        var info = new PdfXfaInfo
        {
            IsPacketArray = true,
            Packets = [new PdfXfaPacket("template", Encoding.UTF8.GetBytes("""
                <template><subform name="form"><field name="tracking"><ui><barcode
                  type="code128" dataLength="24" textLocation="below"
                  moduleWidth="0.25mm" checksum="1mod10"/></ui></field></subform></template>
                """))]
        };

        PdfXfaTemplateField field = Assert.Single(PdfXfaTemplate.Read(info).Fields);

        Assert.Equal("barcode", field.ControlType);
        Assert.NotNull(field.Barcode);
        Assert.Equal("code128", field.Barcode.Type);
        Assert.Equal("24", field.Barcode.Attributes["dataLength"]);
        Assert.Equal("below", field.Barcode.Attributes["textLocation"]);
        Assert.Equal("0.25mm", field.Barcode.Attributes["moduleWidth"]);
        Assert.Equal("1mod10", field.Barcode.Attributes["checksum"]);
    }
}
