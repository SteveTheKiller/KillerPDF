using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
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

    [Fact]
    public void SelectedAbsoluteIntentAppliesOnlyToSourceToPrinterLeg()
    {
        // The printer declares D50 media white, so its absolute reverse is defined. A gray reverse uses
        // only Y, whose white is exactly 1.0, so the printer leg matches the relative values below.
        var proof = new PdfProofTransform(MediaWhiteGray(), D50WhiteGray(), Profile(gray: false),
            PdfRenderingIntent.AbsoluteColorimetric, blackPointCompensation: false);
        double[] source = [0.5];
        double[] result = new double[3];
        proof.Convert(source, result);
        // ICC.1:2022 section 6.3.2: absolute source XYZ is media white times 0.5,
        // [0.375,0.4375,0.25]. Printer gray is Y=0.4375. Relative display leg
        // returns D50-scaled gray [0.4218375,0.4375,0.36089375].
        Assert.Equal(0.4218375, result[0], 12);
        Assert.Equal(0.4375, result[1], 12);
        Assert.Equal(0.36089375, result[2], 12);
        Assert.Equal(new double[] { 0.5 }, source);
    }

    [Fact]
    public void DisablingSourceBlackPointCompensationKeepsDisplayNormalizationEnabled()
    {
        var proof = new PdfProofTransform(Profile(gray: true), RaisedBlackGray(), Profile(gray: true),
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        double[] source = [0.05];
        double[] result = new double[1];
        proof.Convert(source, result);
        // Source Y=0.05 lies below printer p=8192/65535 and clips to device zero.
        // Printer forward yields p, then the always-enabled display BPC maps p
        // to display black zero. Disabling both legs would instead leave p.
        Assert.Equal(0, result[0], 12);
        Assert.Equal(new double[] { 0.05 }, source);
    }

    [Fact]
    public void BlackSimulationPreservesPrinterBlackWithoutPaperSimulation()
    {
        var proof = new PdfProofTransform(Profile(gray: true), RaisedBlackGray(), Profile(gray: true),
            new PdfProofOptions { SimulateBlack = true, SimulatePaper = false });
        double[] source = [0];
        double[] result = new double[1];
        proof.Convert(source, result);
        // Source BPC maps source black to printer black. Relative display
        // conversion with BPC disabled preserves printer Y=8192/65535.
        Assert.Equal(0.1250019073777371, result[0], 12);
        Assert.Equal(new double[] { 0 }, source);
    }

    [Fact]
    public void PaperAndBlackSimulationUseAbsolutePrinterViewingWithoutViewCompensation()
    {
        var printer = SampledGrayWithWhite(8738, 49152, "scnr");
        var display = SampledGrayWithWhite(4369, 65536, "mntr");
        var proof = new PdfProofTransform(printer, printer, display, new PdfProofOptions
        {
            RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
            BlackPointCompensation = false,
            SimulatePaper = true,
            SimulateBlack = true
        });
        double[] source = [0.5];
        double[] result = new double[1];
        proof.Convert(source, result);
        // ICC.1:2022 section 6.3.2.2: absolute Y is relative Y times media white Y.
        // Printer curve is 2/15+(13/15)t. At t=1/2, absolute Y=(3/4)(17/30)=17/40.
        // Display inverse is (Y-1/15)/(14/15), giving 43/112 without view BPC.
        Assert.Equal(0.38392857142857142857, result[0], 12);
        Assert.Equal(new double[] { 0.5 }, source);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.36607142857142857143)]
    [InlineData(1, 0.73214285714285714286)]
    public void PaperOnlyViewingPreservesPaperAndNormalizesPrinterBlack(double input, double expected)
    {
        var printer = SampledGrayWithWhite(8738, 49152, "scnr");
        var display = SampledGrayWithWhite(4369, 65536, "mntr");
        var proof = new PdfProofTransform(printer, printer, display, new PdfProofOptions
        {
            BlackPointCompensation = false,
            SimulatePaper = true,
            SimulateBlack = false
        });
        double[] result = new double[1];
        proof.Convert([input], result);
        // Viewing-aid policy maps absolute printer black Y=1/10 to display
        // black Y=1/15 while preserving paper Y=3/4. At t=1/2 this gives
        // Y=49/120, then display inverse (Y-1/15)/(14/15) is 41/112.
        Assert.Equal(expected, result[0], 12);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PaperSimulationRequiresExplicitMediaWhiteForBothProfiles(bool missingPrinterWhite)
    {
        var printer = missingPrinterWhite ? Profile(gray: true)
            : SampledGrayWithWhite(8738, 49152, "scnr");
        var display = missingPrinterWhite ? SampledGrayWithWhite(4369, 65536, "mntr")
            : Profile(gray: true);

        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(printer, printer, display,
            new PdfProofOptions { SimulatePaper = true, SimulateBlack = true }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void PaperSimulationRejectsNonD50DisplayMediaWhite(byte version)
    {
        var printer = SampledGrayWithWhite(8738, 49152, "scnr");
        var display = SampledGrayWithWhite(4369, 49152, "mntr", version);
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(printer, printer, display,
            new PdfProofOptions { SimulatePaper = true, SimulateBlack = true }));
        // v2 non-D50 display white is ambiguous without a verified adaptation
        // policy. v4 display media white must be PCS D50 (ICC.1:2022 9.2.36).
    }

    private static PdfColorProfile SampledGrayWithWhite(ushort black, int whiteY, string profileClass,
        byte version = 4)
    {
        byte[] data = new byte[192];
        Write32(data, 0, 192);
        data[8] = version;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12);
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156);
        Write32(data, 140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 172);
        Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(data, 164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), black);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(data, 180, 63190);
        Write32(data, 184, whiteY);
        Write32(data, 188, 54061);
        return PdfColorProfile.Load(data);
    }

    private static PdfColorProfile MediaWhiteGray()
    {
        byte[] data = new byte[188];
        Write32(data, 0, 188);
        data[8] = 4;
        "scnr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156);
        Write32(data, 140, 12);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 168);
        Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        "XYZ "u8.CopyTo(data.AsSpan(168));
        Write32(data, 176, 49152);
        Write32(data, 180, 57344);
        Write32(data, 184, 32768);
        return PdfColorProfile.Load(data);
    }

    // Identity gray TRC with an explicit D50 wtpt: X 0.9642, Y 1.0, Z 0.8249 in s15Fixed16.
    private static PdfColorProfile D50WhiteGray()
    {
        byte[] data = new byte[188];
        Write32(data, 0, 188);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156);
        Write32(data, 140, 12);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 168);
        Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        "XYZ "u8.CopyTo(data.AsSpan(168));
        Write32(data, 176, 63190);
        Write32(data, 180, 65536);
        Write32(data, 184, 54061);
        return PdfColorProfile.Load(data);
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
