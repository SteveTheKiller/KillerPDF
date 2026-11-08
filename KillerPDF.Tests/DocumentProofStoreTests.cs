using System.IO;
using System.Buffers.Binary;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DocumentProofStoreTests : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.GetTempPath());
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kp-document-proof-" + Guid.NewGuid().ToString("N"));
    private const string OriginalHash = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
    private const string Requested = "{\"schemaVersion\":1,\"name\":\"Proof\",\"proof\":{\"enabled\":true," +
        "\"printerProfileSha256\":\"7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1\"," +
        "\"renderingIntent\":\"saturation\",\"blackPointCompensation\":false," +
        "\"simulatePaper\":true,\"simulateBlack\":false,\"gamutWarnings\":true}}";
    private string SourcePath => Path.Combine(_directory, "original.pdf");
    private string StorePath => Path.Combine(_directory, "preferences");
    private ProofProfileLibrary Profiles => new(Path.Combine(_directory, "profiles"));

    [Fact]
    public void ReopenRestoresMatchingPathAndContentsWithUnavailableProfileAndAllRequestedFlags()
    {
        var owner = new PdfLogicalDocument { Proof = PdfProofPreset.FromJson(Requested).ResolveProfile(null) };
        new DocumentProofStore(StorePath).Save(SourcePath, OriginalHash, owner.Proof);
        owner.Proof = new PdfDocumentProofSettings();

        var restored = new DocumentProofStore(StorePath).Restore(SourcePath, OriginalHash, Profiles);
        var other = new PdfLogicalDocument { Proof = restored.Settings };

        Assert.Equal(DocumentProofMatch.Matched, restored.Match);
        Assert.Equal(ProofProfileResolutionKind.Missing, restored.ProfileKind);
        Assert.Null(restored.PreviousForReview);
        Assert.True(other.Proof.Enabled);
        Assert.True(other.Proof.GamutWarnings);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", other.Proof.PrinterProfileSha256);
        Assert.Null(other.Proof.PrinterProfile);
        Assert.Equal(PdfRenderingIntent.Saturation, other.Proof.Options.RenderingIntent);
        Assert.False(other.Proof.Options.BlackPointCompensation);
        Assert.True(other.Proof.Options.SimulatePaper);
        Assert.False(other.Proof.Options.SimulateBlack);
        Assert.False(owner.Proof.Enabled);
    }

    [Fact]
    public void IdenticalContentsAtAnotherPathStartWithProofOff()
    {
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, PdfProofPreset.FromJson(Requested).ResolveProfile(null));
        var restored = store.Restore(Path.Combine(_directory, "another.pdf"), OriginalHash, Profiles);
        Assert.Equal(DocumentProofMatch.NotFound, restored.Match);
        Assert.Equal(ProofProfileResolutionKind.NotSelected, restored.ProfileKind);
        Assert.False(restored.Settings.Enabled);
        Assert.False(restored.Settings.GamutWarnings);
        Assert.Null(restored.Settings.PrinterProfileSha256);
        Assert.Null(restored.PreviousForReview);
    }

    [Fact]
    public void ReplacementContentsStartProofOffAndOfferOldSettingsOnlyForReview()
    {
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, PdfProofPreset.FromJson(Requested).ResolveProfile(null));
        var restored = new DocumentProofStore(StorePath).Restore(SourcePath, new string('B', 64), Profiles);
        Assert.Equal(DocumentProofMatch.DifferentContent, restored.Match);
        Assert.False(restored.Settings.Enabled);
        Assert.False(restored.Settings.GamutWarnings);
        Assert.Null(restored.Settings.PrinterProfileSha256);
        Assert.Equal(ProofProfileResolutionKind.NotSelected, restored.ProfileKind);
        var review = Assert.Single(restored.PreviousForReview!);
        Assert.Equal(OriginalHash, review.SourceSha256);
        Assert.True(review.Preset.Enabled);
        Assert.True(review.Preset.GamutWarnings);
        Assert.Equal(PdfRenderingIntent.Saturation, review.Preset.Options.RenderingIntent);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", review.Preset.PrinterProfileSha256);
        Assert.Equal(DocumentProofMatch.Matched, store.Restore(SourcePath, OriginalHash, Profiles).Match);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD0")]
    public void InvalidContentIdentityCannotReadOrPublishPreferences(string? hash)
    {
        var store = new DocumentProofStore(StorePath);
        var save = Assert.ThrowsAny<ArgumentException>(() => store.Save(SourcePath, hash!, new PdfDocumentProofSettings()));
        var restore = Assert.ThrowsAny<ArgumentException>(() => store.Restore(SourcePath, hash!, Profiles));
        Assert.Equal("sourceSha256", save.ParamName);
        Assert.Equal("sourceSha256", restore.ParamName);
        Assert.False(Directory.Exists(StorePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("report.pdf")]
    [InlineData("C:report.pdf")]
    [InlineData("\\report.pdf")]
    public void AmbiguousPathsCannotDependOnTheCurrentDirectory(string? path)
    {
        var store = new DocumentProofStore(StorePath);
        var save = Assert.ThrowsAny<ArgumentException>(() => store.Save(path!, OriginalHash, new PdfDocumentProofSettings()));
        var restore = Assert.ThrowsAny<ArgumentException>(() => store.Restore(path!, OriginalHash, Profiles));
        Assert.Equal("documentPath", save.ParamName);
        Assert.Equal("documentPath", restore.ParamName);
        Assert.False(Directory.Exists(StorePath));
    }

    [Fact]
    public void InvalidRecordedIdentityIsDiagnosedWithoutOverwritingTheCatalog()
    {
        Directory.CreateDirectory(StorePath);
        string json = "{\"schemaVersion\":1,\"presets\":[" + Requested + "]}";
        string path = Path.Combine(StorePath, "proof-presets.json");
        File.WriteAllText(path, json);
        var store = new DocumentProofStore(StorePath);
        Assert.Throws<FormatException>(() => store.Restore(SourcePath, OriginalHash, Profiles));
        Assert.Throws<FormatException>(() => store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings()));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void NormalizedPathAndDigestCasingRestoreTheSameRecordedFile()
    {
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings(enabled: true));
        var restored = store.Restore(Path.Combine(_directory, ".", "ORIGINAL.PDF"), OriginalHash.ToLowerInvariant(), Profiles);
        Assert.Equal(DocumentProofMatch.Matched, restored.Match);
        Assert.True(restored.Settings.Enabled);
    }

    [Fact]
    public void EachKnownVersionRetainsIndependentSettingsAndIsIdentifiedDuringReview()
    {
        var store = new DocumentProofStore(StorePath);
        string secondHash = new('B', 64);
        store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings(enabled: true));
        store.Save(SourcePath, secondHash, PdfProofPreset.FromJson(Requested).ResolveProfile(null));
        var owner = new PdfLogicalDocument { Proof = store.Restore(SourcePath, OriginalHash, Profiles).Settings };
        store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings());

        var unknown = store.Restore(SourcePath, new string('C', 64), Profiles);
        Assert.False(unknown.Settings.Enabled);
        Assert.Equal(DocumentProofMatch.DifferentContent, unknown.Match);
        Assert.Equal(2, unknown.PreviousForReview!.Count);
        var first = Assert.Single(unknown.PreviousForReview, item => item.SourceSha256 == OriginalHash);
        var second = Assert.Single(unknown.PreviousForReview, item => item.SourceSha256 == secondHash);
        Assert.False(first.Preset.Enabled);
        Assert.True(second.Preset.Enabled);
        Assert.True(second.Preset.GamutWarnings);
        Assert.True(owner.Proof.Enabled);
        Assert.False(store.Restore(SourcePath, OriginalHash, Profiles).Settings.Enabled);
        Assert.True(store.Restore(SourcePath, secondHash, Profiles).Settings.Enabled);
    }

    [Fact]
    public void ReplacementWithTheSameLengthAndTimestampStillStartsProofOff()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SourcePath, "abc");
        DateTime time = File.GetLastWriteTimeUtc(SourcePath);
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings(enabled: true));
        File.WriteAllText(SourcePath, "def");
        File.SetLastWriteTimeUtc(SourcePath, time);
        var restored = store.Restore(SourcePath,
            "CB8379AC2098AA165029E3938A51DA0BCECFC008FD6795F401178647F96C5B34", Profiles);
        Assert.Equal(3, new FileInfo(SourcePath).Length);
        Assert.Equal(time, File.GetLastWriteTimeUtc(SourcePath));
        Assert.Equal(DocumentProofMatch.DifferentContent, restored.Match);
        Assert.False(restored.Settings.Enabled);
        Assert.Equal(OriginalHash, Assert.Single(restored.PreviousForReview!).SourceSha256);
        Assert.Equal("def", File.ReadAllText(SourcePath));
    }

    [Fact]
    public void MatchingOwnedProfileIsResolvedWhenTheDocumentReopens()
    {
        byte[] bytes = new byte[156];
        Write(0, 156);
        bytes[8] = 4;
        "mntr"u8.CopyTo(bytes.AsSpan(12));
        "GRAY"u8.CopyTo(bytes.AsSpan(16));
        "XYZ "u8.CopyTo(bytes.AsSpan(20));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        Write(128, 1);
        "kTRC"u8.CopyTo(bytes.AsSpan(132));
        Write(136, 144);
        Write(140, 12);
        "curv"u8.CopyTo(bytes.AsSpan(144));
        Profiles.Import("Gray", bytes);
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, PdfProofPreset.FromJson(Requested).ResolveProfile(null));
        var restored = new DocumentProofStore(StorePath).Restore(SourcePath, OriginalHash, Profiles);
        Assert.Equal(DocumentProofMatch.Matched, restored.Match);
        Assert.Equal(ProofProfileResolutionKind.Matched, restored.ProfileKind);
        Assert.NotNull(restored.Settings.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", restored.Settings.PrinterProfileSha256);
        Assert.True(restored.Settings.Enabled);
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"presets\":[] ,\"extra\":true}")]
    public void DamagedStoreNeverFallsBackToUnrecordedOrOverwritesItsCatalog(string json)
    {
        Directory.CreateDirectory(StorePath);
        string path = Path.Combine(StorePath, "proof-presets.json");
        File.WriteAllText(path, json);
        var store = new DocumentProofStore(StorePath);
        Assert.Throws<FormatException>(() => store.Restore(SourcePath, OriginalHash, Profiles));
        Assert.Throws<FormatException>(() => store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings()));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void FutureStoreRemainsAvailableForACompatibleApplication()
    {
        Directory.CreateDirectory(StorePath);
        string path = Path.Combine(StorePath, "proof-presets.json");
        const string json = "{\"schemaVersion\":2,\"presets\":[]}";
        File.WriteAllText(path, json);
        var store = new DocumentProofStore(StorePath);
        Assert.Throws<NotSupportedException>(() => store.Restore(SourcePath, OriginalHash, Profiles));
        Assert.Throws<NotSupportedException>(() => store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings()));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void LockedCatalogRemainsDiagnosedUntilTheSameFileCanBeReadAgain()
    {
        var store = new DocumentProofStore(StorePath);
        store.Save(SourcePath, OriginalHash, new PdfDocumentProofSettings(enabled: true));
        using (var locked = new FileStream(Path.Combine(StorePath, "proof-presets.json"), FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => store.Restore(SourcePath, OriginalHash, Profiles));
            Assert.DoesNotContain(_directory, error.Message);
            Assert.Null(error.InnerException);
        }
        Assert.True(store.Restore(SourcePath, OriginalHash, Profiles).Settings.Enabled);
    }

    [Fact]
    public void ManyDocumentsKeepSavingAndTheLeastRecentlySavedAreForgotten()
    {
        var store = new DocumentProofStore(StorePath);
        for (int index = 0; index < 200; index++)
            store.Save(Path.Combine(_directory, "document-" + index + ".pdf"), DigestFor(index),
                new PdfDocumentProofSettings(enabled: true));

        var newest = store.Restore(Path.Combine(_directory, "document-199.pdf"), DigestFor(199), Profiles);
        var oldest = store.Restore(Path.Combine(_directory, "document-0.pdf"), DigestFor(0), Profiles);
        Assert.Equal(DocumentProofMatch.Matched, newest.Match);
        Assert.True(newest.Settings.Enabled);
        Assert.Equal(DocumentProofMatch.NotFound, oldest.Match);
    }

    private static string DigestFor(int index) => index.ToString("X64");

    [Fact]
    public void PersistedPreferencesDoNotContainTheOriginalPath()
    {
        new DocumentProofStore(StorePath).Save(SourcePath, OriginalHash, new PdfDocumentProofSettings(enabled: true));
        string json = File.ReadAllText(Path.Combine(StorePath, "proof-presets.json"));
        Assert.DoesNotContain("original.pdf", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_directory, json, StringComparison.OrdinalIgnoreCase);
        Assert.True(new DocumentProofStore(StorePath).Restore(SourcePath, OriginalHash, Profiles).Settings.Enabled);
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(_parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its parent.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
