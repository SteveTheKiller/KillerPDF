using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPreciseFrameDiagnosticsTests
{
    [Fact]
    public void RepairedPageTreeStillRendersAPreciseFrameAndCarriesTheRecoveryNote()
    {
        // The second Pages node lists itself, so compatibility recovery omits the cycle.
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 1 /MediaBox [0 0 2 2] >>",
            "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R] /Count 0 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Length 21 >>\nstream\n1 0 0 rg 0 0 2 2 re f\nendstream"
        ];
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Build(objects)));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 2));

        // Opaque DeviceRGB red is exactly one in linear sRGB.
        Assert.Equal(1, frame.Pixels[0]);
        Assert.Equal(0, frame.Pixels[1]);
        Assert.Equal(0, frame.Pixels[2]);
        Assert.Contains("A cyclic page-tree reference was omitted.", frame.Diagnostics);
    }

    [Fact]
    public void AnUnrepairedPageCarriesNoFrameDiagnostics()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, "0 1 0 rg 0 0 1 1 re f"u8.ToArray()).Build();

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, new PdfRenderOptions(1, 1));

        Assert.Empty(frame.Diagnostics);
    }

    // Same synthetic affine CMYK profile (A2B1 only) and Decimal60 sample as PdfCmykViewingFrameTests.
    private const string AffineCmykProfile =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";
    private const string HalfInk =
        "0.50000762951094835 0.50000762951094835 0.50000762951094835 0.50000762951094835 k 0 0 1 1 re f";
    // SHA256 of the approved bundled CGATS001Compat-v2-micro profile.
    private const string BundledSha256 = "73E1BA37D2BAD5BAB2A964F40A9EED96209666EFC067C3322626214BBEF234A0";

    [Fact]
    public void RenderOptionsDefaultToTheBundledWorkingCmykProfile()
    {
        Assert.Equal(BundledSha256, new PdfRenderOptions(1, 1).WorkingCmykProfile.ContentSha256);
    }

    [Fact]
    public void UntaggedCmykReportsTheAssumedBundledProfile()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, "1 0 0 0 k 0 0 1 1 re f"u8.ToArray()).Build();

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, new PdfRenderOptions(1, 1));

        Assert.Equal(BundledSha256, frame.AssumedCmykProfileSha256);
        Assert.Contains(frame.Diagnostics, note => note.Contains(BundledSha256, StringComparison.Ordinal));
    }

    [Fact]
    public void AConfiguredWorkingCmykProfileConvertsUntaggedCmykAndIsReported()
    {
        byte[] profile = Convert.FromBase64String(AffineCmykProfile);
        string expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profile));
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(HalfInk)).Build();
        var options = new PdfRenderOptions(1, 1) { WorkingCmykProfile = KillerPdf.Engine.ColorManagement.PdfColorProfile.Load(profile) };

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, options);

        // The profile has only a colorimetric table, so its relative colorimetric oracle applies.
        Assert.InRange(frame.Pixels[0], 0.25656640931229085f - 0.000002f, 0.25656640931229085f + 0.000002f);
        Assert.InRange(frame.Pixels[1], 0.24270025515688283f - 0.000002f, 0.24270025515688283f + 0.000002f);
        Assert.InRange(frame.Pixels[2], 0.31217781759250244f - 0.000002f, 0.31217781759250244f + 0.000002f);
        Assert.Equal(expectedSha, frame.AssumedCmykProfileSha256);
        Assert.Contains(frame.Diagnostics, note => note.Contains(expectedSha, StringComparison.Ordinal));
    }

    [Fact]
    public void APageWithoutUntaggedCmykAssumesNoWorkingProfile()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, "0 0 1 rg 0 0 1 1 re f"u8.ToArray()).Build();

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, new PdfRenderOptions(1, 1));

        Assert.Null(frame.AssumedCmykProfileSha256);
    }

    [Fact]
    public void AWorkingProfileMustDescribeCmyk()
    {
        // An sRGB-like matrix display profile cannot stand in for untagged CMYK.
        var display = KillerPdf.Engine.ColorManagement.PdfColorProfile.Load(MatrixRgbProfile());
        Assert.Throws<ArgumentException>(() => new PdfRenderOptions(1, 1) { WorkingCmykProfile = display });
    }

    private static byte[] MatrixRgbProfile()
    {
        // Minimal v2 mntr RGB/XYZ profile: identity-gamma curves and D50 sRGB primaries.
        string[] tags = ["rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "wtpt"];
        int[][] xyz = [[28578, 14581, 912], [25241, 47027, 6366], [9374, 3972, 46811], [], [], [], [63190, 65536, 54061]];
        var tagData = new List<byte[]>();
        foreach (int[] values in xyz)
        {
            if (values.Length == 0) { tagData.Add([.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 0]); continue; }
            var data = new byte[20];
            "XYZ "u8.CopyTo(data);
            for (int index = 0; index < 3; index++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + index * 4), values[index]);
            tagData.Add(data);
        }
        int offset = 132 + 12 * tags.Length;
        var bytes = new List<byte>(new byte[offset]);
        var table = new byte[12 * tags.Length];
        for (int index = 0; index < tags.Length; index++)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            Encoding.ASCII.GetBytes(tags[index]).CopyTo(table, index * 12);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(index * 12 + 4), bytes.Count);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(index * 12 + 8), tagData[index].Length);
            bytes.AddRange(tagData[index]);
        }
        byte[] profile = [.. bytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile, profile.Length);
        profile[8] = 2;
        "mntr"u8.CopyTo(profile.AsSpan(12));
        "RGB "u8.CopyTo(profile.AsSpan(16));
        "XYZ "u8.CopyTo(profile.AsSpan(20));
        "acsp"u8.CopyTo(profile.AsSpan(36));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128), tags.Length);
        table.CopyTo(profile, 132);
        return profile;
    }

    private static byte[] Build(string[] objects)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(source.Length);
            source.Append($"{index + 1} 0 obj {objects[index]} endobj\n");
        }
        int xrefOffset = source.Length;
        source.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f\n");
        foreach (int offset in offsets) source.Append($"{offset:0000000000} 00000 n\n");
        source.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
