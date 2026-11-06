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

    [Fact]
    public void MatchingProfileAppliesTheRequestedSnapshotOnlyToTheChosenDocument()
    {
        const string json = "{\"schemaVersion\":1,\"name\":\"Gray stock\",\"proof\":{\"enabled\":true," +
            "\"printerProfileSha256\":\"7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1\"," +
            "\"renderingIntent\":\"absoluteColorimetric\",\"blackPointCompensation\":false," +
            "\"simulatePaper\":true,\"simulateBlack\":false,\"gamutWarnings\":true}}";
        var preset = PdfProofPreset.FromJson(json);
        var profile = PdfColorProfile.Load(GrayProfile());
        var chosen = new PdfLogicalDocument();
        var other = new PdfLogicalDocument();

        chosen.Proof = preset.ResolveProfile(profile);

        Assert.True(chosen.Proof.Enabled);
        Assert.Same(profile, chosen.Proof.PrinterProfile);
        Assert.Equal(PdfRenderingIntent.AbsoluteColorimetric, chosen.Proof.Options.RenderingIntent);
        Assert.False(chosen.Proof.Options.BlackPointCompensation);
        Assert.True(chosen.Proof.Options.SimulatePaper);
        Assert.False(chosen.Proof.Options.SimulateBlack);
        Assert.True(chosen.Proof.GamutWarnings);
        Assert.False(other.Proof.Enabled);
        Assert.Null(other.Proof.PrinterProfile);
        chosen.Proof = new PdfDocumentProofSettings();
        Assert.True(preset.Enabled);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", preset.PrinterProfileSha256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherValidProfileCannotReplaceTheRequestedContent(bool profileWasSelected)
    {
        var original = PdfColorProfile.Load(GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: profileWasSelected ? original : null));
        byte[] changedBytes = GrayProfile();
        changedBytes[48] = 65; // A distinct manufacturer header, retaining the valid Gray transform.
        var changed = PdfColorProfile.Load(changedBytes);
        var owner = new PdfLogicalDocument();
        var before = owner.Proof;

        var error = Assert.Throws<ArgumentException>(() => owner.Proof = preset.ResolveProfile(changed));

        Assert.Equal("profile", error.ParamName);
        Assert.Same(before, owner.Proof);
        Assert.NotEqual(original.ContentSha256, changed.ContentSha256);
    }

    [Fact]
    public void AnUnavailableProfileKeepsItsReferenceAndAllRequestedChoices()
    {
        var preset = PdfProofPreset.Capture("Missing stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile()),
            options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.Saturation,
                BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true }, gamutWarnings: true));

        var missing = preset.ResolveProfile(null);
        var copied = PdfProofPreset.Capture("Copy", missing);

        Assert.Null(missing.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", missing.PrinterProfileSha256);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", copied.PrinterProfileSha256);
        Assert.True(missing.Enabled);
        Assert.True(missing.GamutWarnings);
        Assert.Equal(PdfRenderingIntent.Saturation, missing.Options.RenderingIntent);
        Assert.False(missing.Options.BlackPointCompensation);
        Assert.True(missing.Options.SimulatePaper);
        Assert.True(missing.Options.SimulateBlack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingAnUnavailableReferenceUpdatesNormalAndComparisonSnapshots(bool comparison)
    {
        var first = PdfProofPreset.Capture("First", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile()))).ResolveProfile(null);
        byte[] otherBytes = GrayProfile();
        otherBytes[48] = 65;
        var second = PdfProofPreset.Capture("Second", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(otherBytes))).ResolveProfile(null);
        var owner = new PdfLogicalDocument();
        var view = new PdfDocumentProofView(owner);
        if (comparison) view.ComparisonOverride = first;
        else owner.Proof = first;
        var previous = view.CaptureProof();

        if (comparison) view.ComparisonOverride = second;
        else owner.Proof = second;
        var current = view.CaptureProof();

        Assert.Same(second, current.Settings);
        Assert.NotEqual(previous.Stamp, current.Stamp);
        Assert.Equal(2L, comparison ? current.Stamp.OverrideGeneration : current.Stamp.DocumentGeneration);
        Assert.Equal(0L, comparison ? current.Stamp.DocumentGeneration : current.Stamp.OverrideGeneration);
        var equivalent = PdfProofPreset.Capture("Equivalent", second).ResolveProfile(null);
        if (comparison) view.ComparisonOverride = equivalent;
        else owner.Proof = equivalent;
        Assert.Equal(current.Stamp, view.CaptureProof().Stamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfileAvailabilityChangesInvalidateNormalAndComparisonSnapshots(bool comparison)
    {
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        var missing = preset.ResolveProfile(null);
        var available = preset.ResolveProfile(PdfColorProfile.Load(GrayProfile()));
        var owner = new PdfLogicalDocument();
        var view = new PdfDocumentProofView(owner);
        Select(missing);

        Select(available);
        var present = view.CaptureProof();
        Assert.Same(available, present.Settings);
        Assert.Equal(2L, comparison ? present.Stamp.OverrideGeneration : present.Stamp.DocumentGeneration);

        Select(preset.ResolveProfile(PdfColorProfile.Load(GrayProfile())));
        Assert.Equal(present.Stamp, view.CaptureProof().Stamp);
        Select(missing);
        var absent = view.CaptureProof();
        Assert.Same(missing, absent.Settings);
        Assert.Equal(3L, comparison ? absent.Stamp.OverrideGeneration : absent.Stamp.DocumentGeneration);

        void Select(PdfDocumentProofSettings settings)
        {
            if (comparison) view.ComparisonOverride = settings;
            else owner.Proof = settings;
        }
    }

    [Fact]
    public void IndependentDocumentCopyRetainsAnUnavailableSelectionWithoutSharingLaterChanges()
    {
        var selected = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile()))).ResolveProfile(null);
        var original = new PdfLogicalDocument { Proof = selected };

        var copy = original.CreateIndependentCopy();

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Same(selected, copy.Proof);
        Assert.Null(copy.Proof.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", copy.Proof.PrinterProfileSha256);
        copy.Proof = new PdfDocumentProofSettings();
        Assert.Same(selected, original.Proof);
        Assert.False(copy.Proof.Enabled);
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
