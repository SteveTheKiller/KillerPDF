using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfProofProfilePolicyTests
{
    [Theory]
    [InlineData(true, 0, 0.0499994724903729493, 0.1, 0.0999981502376944563)]
    [InlineData(true, 0.5, 0.3999957799229835945, 0.425, 0.2999944507130833688)]
    [InlineData(true, 1, 0.7499920873555942396, 0.75, 0.4999907511884722813)]
    [InlineData(false, 0, 0, 0, 0)]
    [InlineData(false, 0.5, 0.3749960436777971198, 0.375, 0.2499953755942361407)]
    [InlineData(false, 1, 0.7499920873555942396, 0.75, 0.4999907511884722813)]
    public void ChromaticPaperViewingMatchesIndependentEncodedEndpointVectors(
        bool simulateBlack, double input, double x, double y, double z)
    {
        var printer = Rgb(printer: true);
        var display = Rgb(printer: false);
        var proof = new PdfProofTransform(printer, printer, display, new PdfProofOptions
        {
            BlackPointCompensation = false,
            SimulatePaper = true,
            SimulateBlack = simulateBlack
        });
        double[] source = [input, input, input];
        double[] result = new double[3];
        proof.Convert(source, result);
        // Equal diagonal D50 matrices. Printer TRC black is (1/15,2/15,1/5).
        // Ideal media scales (.75,.75,.5) give both-simulation endpoints
        // (.05,.10,.10), (.40,.425,.30), (.75,.75,.50); paper-only gives
        // (0,0,0), (.375,.375,.25), (.75,.75,.50).
        // ICC fixed-point white tags change X scale to 47392/63190 and Z
        // to 27030/54061. The equal encoded D50 matrices cancel algebraically
        // between printer forward and display inverse, not through code calls.
        // Inline literals use those exact independent media-white ratios,
        // including display-white encoding. No production inverse is the oracle.
        Assert.Equal(x, result[0], 12);
        Assert.Equal(y, result[1], 12);
        Assert.Equal(z, result[2], 12);
        Assert.Equal(new double[] { input, input, input }, source);
    }

    [Fact]
    public void PaperViewingRejectsLutProfileWithOnlyPerceptualTables()
    {
        var printer = PdfColorProfile.Load(Profile("scnr", ("A2B0", Lut16(forward: true)),
            ("B2A0", Lut16(forward: false)), ("wtpt", Xyz(47392, 49152, 27030))));
        var display = Rgb(printer: false);
        // Both LUT directions and explicit media white exist, but no A2B1/B2A1.
        // A perceptual table fallback cannot establish colorimetric paper data.
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(printer, printer, display,
            new PdfProofOptions
            {
                BlackPointCompensation = false,
                SimulatePaper = true,
                SimulateBlack = true
            }));
    }

    [Fact]
    public void PaperViewingRejectsForwardWhiteThatDisagreesWithDeclaredMediaWhite()
    {
        var printer = PdfColorProfile.Load(Profile("scnr",
            ("rTRC", Curve(4369)), ("gTRC", Curve(8738)), ("bTRC", Curve(13107)),
            ("rXYZ", Xyz(63190, 0, 0)), ("gXYZ", Xyz(0, 32768, 0)),
            ("bXYZ", Xyz(0, 0, 54061)), ("wtpt", Xyz(47392, 49152, 27030))));
        var display = Rgb(printer: false);
        double[] relativeWhite = new double[3];
        printer.ToXyz([1, 1, 1], relativeWhite);
        Assert.Equal(0.5, relativeWhite[1], 12);
        // Declared media white Y is 3/4. Actual relative device-white Y is
        // 1/2, making absolute forward white Y=3/8, far below the declaration.
        // Valid explicit wtpt and colorimetric provenance alone are insufficient.
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(printer, printer, display,
            new PdfProofOptions
            {
                BlackPointCompensation = false,
                SimulatePaper = true,
                SimulateBlack = true
            }));
    }

    [Fact]
    public void CmykPaperOnlyViewingUsesPerceptualDeviceBlackThenColorimetricForward()
    {
        var printer = Cmyk(includePerceptualReverse: true);
        var display = Rgb(printer: false);
        var proof = new PdfProofTransform(display, printer, display, new PdfProofOptions
        {
            BlackPointCompensation = false,
            SimulatePaper = true,
            SimulateBlack = false
        });
        double[] source = [0, 0, 0];
        double[] result = new double[3];
        proof.Convert(source, result);
        // B2A1 selects K=32768/65535. B2A0(PCS zero) estimates device black
        // K=49151/65535 (ICC WP40 section 6), then actual A2B1 gives its XYZ.
        // Affine endpoint viewing leaves fraction (49151-32768)/49151 of paper.
        // X,Y,Z literals include all LUT, matrix and media-white quantization.
        Assert.Equal(0.2499871898261825889, result[0], 12);
        Assert.Equal(0.2499898272669935505, result[1], 12);
        Assert.Equal(0.2499867444267528867, result[2], 12);
        Assert.Equal(new double[] { 0, 0, 0 }, source);
    }

    [Fact]
    public void CmykWithoutPerceptualReverseRejectsPaperOnlyButAllowsPaperAndBlack()
    {
        var printer = Cmyk(includePerceptualReverse: false);
        var display = Rgb(printer: false);
        Assert.Throws<NotSupportedException>(() => new PdfProofTransform(display, printer, display,
            new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true }));
        var proof = new PdfProofTransform(display, printer, display,
            new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true });
        double[] source = [0, 0, 0];
        double[] result = new double[3];
        proof.Convert(source, result);
        // No endpoint normalization: F(Km)/32768 times media-white scaling,
        // followed by the independent diagonal display inverse. Km=32768/65535.
        // Y=(32768+(6554-32768)Km)/32768 * 3/4 is exactly 9/20.
        Assert.Equal(0.4499906747550832205, result[0], 12);
        Assert.Equal(0.45, result[1], 12);
        Assert.Equal(0.4499898730116417002, result[2], 12);
        Assert.Equal(new double[] { 0, 0, 0 }, source);
    }

    [Theory]
    [MemberData(nameof(GrayModeEndpointVectors))]
    public void GrayProofModeEndpointsMatchIndependentV2AndV4Table(
        byte version, bool paper, bool black, double input, double expected)
    {
        var printer = Gray(version, printer: true);
        var display = Gray(version, printer: false);
        var proof = new PdfProofTransform(printer, printer, display, new PdfProofOptions
        {
            BlackPointCompensation = false,
            SimulatePaper = paper,
            SimulateBlack = black
        });
        double[] source = [input];
        double[] result = new double[1];
        proof.Convert(source, result);
        // Printer curve 2/15+(13/15)t, display 1/15+(14/15)t.
        // Printer paper Y=3/4, display paper Y=1. Literal table covers
        // endpoint normalization, black preservation and absolute viewing.
        Assert.Equal(expected, result[0], 12);
        Assert.Equal(new double[] { input }, source);
    }

    public static IEnumerable<object[]> GrayModeEndpointVectors()
    {
        double[][] expected =
        [
            [0, 0.5, 1],
            [0.07142857142857142857, 0.53571428571428571429, 1],
            [0, 0.36607142857142857143, 0.73214285714285714286],
            [0.03571428571428571429, 0.38392857142857142857, 0.73214285714285714286]
        ];
        foreach (byte version in new byte[] { 2, 4 })
            for (int mode = 0; mode < 4; mode++)
                for (int sample = 0; sample < 3; sample++)
                    yield return [version, mode >= 2, (mode & 1) != 0, sample * 0.5, expected[mode][sample]];
    }

    [Fact]
    public void PaperOnlyViewingSupportsColorimetricRgbLutDisplayWithKnownInverse()
    {
        byte[] reverse = CmykTable(3, 3, cell =>
            [(ushort)(((cell >> 2) & 1) * 65535),
                (ushort)(((cell >> 1) & 1) * 65535), (ushort)((cell & 1) * 65535)]);
        // XYZ input is u1Fixed15 normalized by 65535. Inverse factors are
        // 65535/[31595,32768,27030], rounded once to s15Fixed16 integers.
        Write32(reverse, 12, 135936);
        Write32(reverse, 28, 131070);
        Write32(reverse, 44, 158894);
        var display = PdfColorProfile.Load(Profile("mntr", ("A2B1", Lut16(forward: true)),
            ("B2A1", reverse), ("wtpt", Xyz(63190, 65536, 54061))));
        var printer = Rgb(printer: true);
        var proof = new PdfProofTransform(printer, printer, display, new PdfProofOptions
        {
            BlackPointCompensation = false,
            SimulatePaper = true,
            SimulateBlack = false
        });
        double[][] expected =
        [
            [0, 0, 0],
            [0.3749957084000915541, 0.375, 0.2500001781181602626],
            [0.7499914168001831083, 0.75, 0.5000003562363205253]
        ];
        for (int sample = 0; sample < 3; sample++)
        {
            double t = sample * 0.5;
            double[] source = [t, t, t];
            double[] result = new double[3];
            proof.Convert(source, result);
            // Independent rational oracle: Wp*C*t/(2*65535*65536), with
            // Wp=[47392,49152,27030] and C=[135936,131070,158894].
            // Forward-white encoding, absolute white ratios and equal printer
            // matrix/white factors are included, not inferred by an output inverse.
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(expected[sample][channel], result[channel], 12);
            Assert.Equal(new double[] { t, t, t }, source);
        }
    }

    private static PdfColorProfile Gray(byte version, bool printer)
    {
        byte[] data = Profile(printer ? "scnr" : "mntr",
            ("kTRC", Curve(printer ? (ushort)8738 : (ushort)4369)),
            ("wtpt", printer ? Xyz(47392, 49152, 40546) : Xyz(63190, 65536, 54061)));
        data[8] = version;
        "GRAY"u8.CopyTo(data.AsSpan(16));
        return PdfColorProfile.Load(data);
    }

    private static PdfColorProfile Cmyk(bool includePerceptualReverse)
    {
        byte[] forward = CmykTable(4, 3, cell => (cell & 1) == 0
            ? [31595, 32768, 27030] : [6319, 6554, 5406]);
        var tags = new List<(string Name, byte[] Data)>
        {
            ("A2B0", forward), ("A2B1", forward),
            ("B2A1", CmykTable(3, 4, _ => [0, 0, 0, 32768])),
            ("wtpt", Xyz(47392, 49152, 40546))
        };
        if (includePerceptualReverse)
            tags.Add(("B2A0", CmykTable(3, 4, _ => [0, 0, 0, 49151])));
        byte[] data = Profile("prtr", tags.ToArray());
        "CMYK"u8.CopyTo(data.AsSpan(16));
        return PdfColorProfile.Load(data);
    }

    private static byte[] CmykTable(int inputs, int outputs, Func<int, ushort[]> vertex)
    {
        byte[] data = new byte[52 + 4 * inputs + 2 * (1 << inputs) * outputs + 4 * outputs];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs; data[9] = (byte)outputs; data[10] = 2;
        for (int axis = 0; axis < 3; axis++) Write32(data, 12 + axis * 16, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Append(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < inputs; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < 1 << inputs; cell++)
            foreach (ushort value in vertex(cell)) Append(value);
        for (int channel = 0; channel < outputs; channel++) { Append(0); Append(65535); }
        return data;
    }

    private static byte[] Lut16(bool forward)
    {
        byte[] data = new byte[124];
        "mft2"u8.CopyTo(data);
        data[8] = 3; data[9] = 3; data[10] = 2;
        for (int axis = 0; axis < 3; axis++) Write32(data, 12 + axis * 16, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Append(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < 3; channel++) { Append(0); Append(65535); }
        // RGB cube order: R changes slowest and B fastest. mft2 PCS XYZ
        // uses u1Fixed15. Forward white is encoded D50 within one CLUT unit.
        for (int cell = 0; cell < 8; cell++)
        {
            Append((ushort)(((cell >> 2) & 1) * (forward ? 31595 : 65535)));
            Append((ushort)(((cell >> 1) & 1) * (forward ? 32768 : 65535)));
            Append((ushort)((cell & 1) * (forward ? 27030 : 65535)));
        }
        for (int channel = 0; channel < 3; channel++) { Append(0); Append(65535); }
        return data;
    }

    // Synthetic encoding fixtures, not physical printer models or complete
    // conformance profiles. ICC.1:2022 6.3.2.2, 10.6 and 10.31 define the
    // absolute scaling, sampled curves and s15Fixed16 matrix/white encodings.
    // https://www.color.org/specifications/ICC.1-2022-05.pdf
    private static PdfColorProfile Rgb(bool printer)
    {
        return PdfColorProfile.Load(Profile(printer ? "scnr" : "mntr",
            ("rTRC", Curve(printer ? (ushort)4369 : (ushort)0)),
            ("gTRC", Curve(printer ? (ushort)8738 : (ushort)0)),
            ("bTRC", Curve(printer ? (ushort)13107 : (ushort)0)),
            ("rXYZ", Xyz(63190, 0, 0)), ("gXYZ", Xyz(0, 65536, 0)),
            ("bXYZ", Xyz(0, 0, 54061)),
            ("wtpt", printer ? Xyz(47392, 49152, 27030) : Xyz(63190, 65536, 54061))));
    }

    private static byte[] Curve(ushort black)
    {
        byte[] data = new byte[16];
        "curv"u8.CopyTo(data);
        Write32(data, 8, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), black);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), 65535);
        return data;
    }

    private static byte[] Xyz(int x, int y, int z)
    {
        byte[] data = new byte[20];
        "XYZ "u8.CopyTo(data);
        Write32(data, 8, x); Write32(data, 12, y); Write32(data, 16, z);
        return data;
    }

    private static byte[] Profile(string profileClass, params (string Name, byte[] Data)[] tags)
    {
        int offset = 132 + 12 * tags.Length;
        byte[] data = new byte[offset + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        Write32(data, 0, data.Length);
        data[8] = 4;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12);
        "RGB "u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tags.Length);
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + 12 * index;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, tags[index].Data.Length);
            tags[index].Data.CopyTo(data, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return data;
    }

    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
