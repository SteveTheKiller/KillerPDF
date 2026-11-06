using System.Buffers.Binary;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfProofPresetTests
{
    private const string ReferenceHash = "0000000000000000000000000000000000000000000000000000000000000001";

    [Fact]
    public void ReadsExactRequestedChoicesAndProfileContentReference()
    {
        const string json = """
            {"schemaVersion":1,"name":"Coated stock","proof":{
              "enabled":true,
              "printerProfileSha256":"0000000000000000000000000000000000000000000000000000000000000001",
              "renderingIntent":"absoluteColorimetric","blackPointCompensation":false,
              "simulatePaper":true,"simulateBlack":false,"gamutWarnings":true}}
            """;

        var preset = PdfProofPreset.FromJson(json);

        Assert.Equal("Coated stock", preset.Name);
        Assert.Equal(ReferenceHash, preset.PrinterProfileSha256);
        Assert.True(preset.Enabled);
        Assert.True(preset.GamutWarnings);
        Assert.Equal(PdfRenderingIntent.AbsoluteColorimetric, preset.Options.RenderingIntent);
        Assert.False(preset.Options.BlackPointCompensation);
        Assert.True(preset.Options.SimulatePaper);
        Assert.False(preset.Options.SimulateBlack);
    }

    [Fact]
    public void ExportCapturesPreferencesWithoutBundlingProfileBytesOrFollowingLaterChanges()
    {
        byte[] bytes = GrayProfile();
        var owner = new PdfLogicalDocument
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: PdfColorProfile.Load(bytes),
                options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.Saturation,
                    BlackPointCompensation = false, SimulatePaper = false, SimulateBlack = true })
        };

        var preset = PdfProofPreset.Capture("Gray reference", owner.Proof);
        owner.Proof = new PdfDocumentProofSettings();
        Array.Fill(bytes, (byte)255);
        using var document = JsonDocument.Parse(preset.ToJson());

        var root = document.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Gray reference", root.GetProperty("name").GetString());
        var proof = root.GetProperty("proof");
        Assert.Equal(7, proof.EnumerateObject().Count());
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1",
            proof.GetProperty("printerProfileSha256").GetString());
        Assert.True(proof.GetProperty("enabled").GetBoolean());
        Assert.False(proof.GetProperty("gamutWarnings").GetBoolean());
        Assert.Equal("saturation", proof.GetProperty("renderingIntent").GetString());
        Assert.False(proof.GetProperty("blackPointCompensation").GetBoolean());
        Assert.False(proof.GetProperty("simulatePaper").GetBoolean());
        Assert.True(proof.GetProperty("simulateBlack").GetBoolean());
    }

    [Theory]
    [InlineData("\"name\":\"Different\",", "")]
    [InlineData("\"profilePath\":\"private.icc\",", "")]
    [InlineData("", "\"enabled\":false,")]
    [InlineData("", "\"unknownSetting\":true,")]
    public void RejectsAmbiguousOrUnrecognizedSettings(string rootExtra, string proofExtra)
    {
        string json = "{" + rootExtra + "\"schemaVersion\":1,\"name\":\"Coated stock\",\"proof\":{" + proofExtra +
            "\"enabled\":true,\"printerProfileSha256\":null,\"renderingIntent\":\"perceptual\"," +
            "\"blackPointCompensation\":true,\"simulatePaper\":false,\"simulateBlack\":false,\"gamutWarnings\":false}}";

        Assert.Throws<FormatException>(() => PdfProofPreset.FromJson(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad\nname")]
    [InlineData("bad\u0000name")]
    [InlineData("long")]
    public void RejectsNamesThatCannotBeUsedByThePresetLibrary(string name)
    {
        if (name == "long") name = new string('A', 129);
        string json = "{\"schemaVersion\":1,\"name\":" + JsonSerializer.Serialize(name) +
            ",\"proof\":{\"enabled\":true,\"printerProfileSha256\":null,\"renderingIntent\":\"perceptual\"," +
            "\"blackPointCompensation\":true,\"simulatePaper\":false,\"simulateBlack\":false,\"gamutWarnings\":false}}";

        Assert.Throws<FormatException>(() => PdfProofPreset.FromJson(json));
        Assert.Throws<ArgumentException>(() => PdfProofPreset.Capture(name, new PdfDocumentProofSettings()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///private.icc")]
    [InlineData("https://example.invalid/profile.icc")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFG")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]
    public void RejectsProfileReferencesThatAreNotContentDigests(string reference)
    {
        string json = "{\"schemaVersion\":1,\"name\":\"Stock\",\"proof\":{\"enabled\":true,\"printerProfileSha256\":" +
            JsonSerializer.Serialize(reference) + ",\"renderingIntent\":\"perceptual\",\"blackPointCompensation\":true," +
            "\"simulatePaper\":false,\"simulateBlack\":false,\"gamutWarnings\":false}}";

        Assert.Throws<FormatException>(() => PdfProofPreset.FromJson(json));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"private-marker\",\"name\":\"Stock\",\"proof\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"Stock\",\"proof\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"name\":\"Stock\",\"proof\":{}}")]
    [InlineData("{\"schemaVersion\":999999999999999999999,\"name\":\"Stock\",\"proof\":{}}")]
    public void DamagedPresetsProduceAConsistentSanitizedFormatError(string json)
    {
        var error = Assert.Throws<FormatException>(() => PdfProofPreset.FromJson(json));

        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ImportsAtTheUtf8LimitAndRejectsOneByteMore()
    {
        // This literal has one two-byte UTF-8 character; all other characters are ASCII.
        const string json = "{\"schemaVersion\":1,\"name\":\"Café\",\"proof\":{\"enabled\":true,\"printerProfileSha256\":null," +
            "\"renderingIntent\":\"relativeColorimetric\",\"blackPointCompensation\":true," +
            "\"simulatePaper\":false,\"simulateBlack\":false,\"gamutWarnings\":false}}";
        string atLimit = json + new string(' ', 1_048_576 - json.Length - 1);

        Assert.Equal("Café", PdfProofPreset.FromJson(atLimit).Name);
        Assert.Throws<FormatException>(() => PdfProofPreset.FromJson(atLimit + " "));
    }

    [Fact]
    public void ProfileContentReferencesUseCanonicalCasing()
    {
        const string json = "{\"schemaVersion\":1,\"name\":\"Stock\",\"proof\":{\"enabled\":true," +
            "\"printerProfileSha256\":\"abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd\"," +
            "\"renderingIntent\":\"perceptual\",\"blackPointCompensation\":true," +
            "\"simulatePaper\":false,\"simulateBlack\":false,\"gamutWarnings\":false}}";

        var preset = PdfProofPreset.FromJson(json);

        Assert.Equal("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD", preset.PrinterProfileSha256);
    }

    [Fact]
    public void CaptureRejectsAnIntentThatCannotBeSaved()
    {
        var settings = new PdfDocumentProofSettings(options:
            new PdfProofOptions { RenderingIntent = (PdfRenderingIntent)999 });

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfProofPreset.Capture("Stock", settings));
    }

    [Theory]
    [InlineData("perceptual", PdfRenderingIntent.Perceptual)]
    [InlineData("relativeColorimetric", PdfRenderingIntent.RelativeColorimetric)]
    [InlineData("saturation", PdfRenderingIntent.Saturation)]
    [InlineData("absoluteColorimetric", PdfRenderingIntent.AbsoluteColorimetric)]
    public void PreservesEveryIntentAndRequestsWithNoSelectedProfile(string intent, PdfRenderingIntent expected)
    {
        string json = "{\"schemaVersion\":1,\"name\":\"Stock\",\"proof\":{\"enabled\":true,\"printerProfileSha256\":null," +
            "\"renderingIntent\":\"" + intent + "\",\"blackPointCompensation\":true," +
            "\"simulatePaper\":true,\"simulateBlack\":false,\"gamutWarnings\":true}}";

        var preset = PdfProofPreset.FromJson(json);
        using var exported = JsonDocument.Parse(preset.ToJson(indented: true));

        Assert.True(preset.Enabled);
        Assert.Null(preset.PrinterProfileSha256);
        Assert.Equal(expected, preset.Options.RenderingIntent);
        Assert.True(preset.GamutWarnings);
        Assert.True(preset.Options.BlackPointCompensation);
        Assert.True(preset.Options.SimulatePaper);
        Assert.False(preset.Options.SimulateBlack);
        Assert.Equal(intent, exported.RootElement.GetProperty("proof").GetProperty("renderingIntent").GetString());
    }

    [Fact]
    public void UnsupportedSchemasAreDistinctFromDamagedPresets()
    {
        const string json = "{\"schemaVersion\":2,\"name\":\"Future\",\"proof\":{}}";

        Assert.Throws<NotSupportedException>(() => PdfProofPreset.FromJson(json));
        Assert.Equal(128, PdfProofPreset.Capture(new string('A', 128), new PdfDocumentProofSettings()).Name.Length);
    }

    private static byte[] GrayProfile()
    {
        // The 156-byte profile and independent digest also appear in the public identity tests.
        byte[] data = new byte[156];
        Write(0, 156);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write(128, 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write(136, 144);
        Write(140, 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return data;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }
}
