using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfProofTransformTests
{
    [Fact]
    public void DefaultProofConvertsThroughPrinterComponentsAndPreservesSource()
    {
        var proof = new PdfProofTransform(Profile(gray: false), Profile(gray: true), Profile(gray: false));
        double[] source = [0.2, 0.4, 0.6];
        double[] result = new double[3];
        proof.Convert(source, result);
        // Identity XYZ matrix RGB becomes gray Y=0.4, then D50 gray XYZ.
        // ICC.1:2022 sections 6.3.4.3 and 10.6 define PCS white and identity TRCs.
        // https://www.color.org/specifications/ICC.1-2022-05.pdf
        // Zero-valued device black is XYZ zero for all three profiles. Both
        // relative-colorimetric BPC legs therefore have equal zero black endpoints.
        Assert.Equal(0.38568, result[0], 12);
        Assert.Equal(0.4, result[1], 12);
        Assert.Equal(0.32996, result[2], 12);
        Assert.Equal(new double[] { 0.2, 0.4, 0.6 }, source);
    }

    [Fact]
    public void DefaultProofCompensatesBothLegsWhenPrinterBlackExceedsSourceShadow()
    {
        var proof = new PdfProofTransform(Profile(gray: true), RaisedBlackGray(), Profile(gray: true));
        double[] source = [0.05];
        double[] result = new double[1];
        proof.Convert(source, result);
        // Printer Y=p+(1-p)*g, p=8192/65535, with zero-black source/display.
        // p is below (33/58)^3, the Y corresponding to the L*=50 detection cap.
        // ISO/CD 18619 sections 4.2.3, 4.2.4, 4.2.6 and 4.2.7:
        // first BPC maps t to p+(1-p)*t; inverse printer TRC gives t.
        // Printer forward restores p+(1-p)*t; second BPC restores t=0.05.
        // https://www.color.org/BlackPointCompensation.pdf
        // Without first-leg BPC, this shadow clips below p. Without second-leg
        // BPC, it stays lifted above p instead of returning to 0.05.
        Assert.Equal(0.05, result[0], 12);
        Assert.Equal(new double[] { 0.05 }, source);
    }

    private static PdfColorProfile RaisedBlackGray()
    {
        byte[] data = new byte[160];
        Write32(data, 0, 160);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 144);
        Write32(data, 140, 16);
        "curv"u8.CopyTo(data.AsSpan(144));
        Write32(data, 152, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(156), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(158), 65535);
        return PdfColorProfile.Load(data);
    }

    // Minimal ICC encoding fixtures, not full profile-conformance specimens.
    private static PdfColorProfile Profile(bool gray)
    {
        string[] signatures = gray ? ["kTRC"] : ["rTRC", "gTRC", "bTRC", "rXYZ", "gXYZ", "bXYZ"];
        int offset = 132 + signatures.Length * 12;
        int size = offset + (gray ? 12 : 96);
        byte[] data = new byte[size];
        Write32(data, 0, size);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        Encoding.ASCII.GetBytes(gray ? "GRAY" : "RGB ").CopyTo(data, 16);
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, signatures.Length);
        for (int index = 0; index < signatures.Length; index++)
        {
            int entry = 132 + index * 12;
            bool curve = gray || index < 3;
            int length = curve ? 12 : 20;
            Encoding.ASCII.GetBytes(signatures[index]).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, length);
            if (curve) "curv"u8.CopyTo(data.AsSpan(offset));
            else
            {
                "XYZ "u8.CopyTo(data.AsSpan(offset));
                Write32(data, offset + 8 + (index - 3) * 4, 65536);
            }
            offset += length;
        }
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
