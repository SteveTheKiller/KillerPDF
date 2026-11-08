using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ProofProfileLibraryTests : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.GetTempPath());
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kp-proof-profiles-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ImportedProfileCanBeReopenedAndAppliedAfterCallerBytesAreDiscarded()
    {
        byte[] bytes = GrayProfile();
        var library = new ProofProfileLibrary(_directory);
        var imported = library.Import("Gray paper", bytes);
        Array.Fill(bytes, (byte)255);
        var reopened = new ProofProfileLibrary(_directory);
        const string json = "{\"schemaVersion\":1,\"name\":\"Gray proof\",\"proof\":{\"enabled\":true," +
            "\"printerProfileSha256\":\"7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1\"," +
            "\"renderingIntent\":\"saturation\",\"blackPointCompensation\":false," +
            "\"simulatePaper\":true,\"simulateBlack\":false,\"gamutWarnings\":true}}";

        var entry = Assert.Single(reopened.GetProfiles());
        var resolved = reopened.Resolve(PdfProofPreset.FromJson(json));
        var owner = new PdfLogicalDocument { Proof = resolved.Settings };

        Assert.Equal("Gray paper", imported.Name);
        Assert.Equal("Gray paper", entry.Name);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", entry.ContentSha256);
        Assert.Equal(ProofProfileResolutionKind.Matched, resolved.Kind);
        Assert.NotNull(owner.Proof.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", owner.Proof.PrinterProfileSha256);
        Assert.True(owner.Proof.Enabled);
        Assert.True(owner.Proof.GamutWarnings);
        Assert.Equal(PdfRenderingIntent.Saturation, owner.Proof.Options.RenderingIntent);
        Assert.False(owner.Proof.Options.BlackPointCompensation);
        Assert.True(owner.Proof.Options.SimulatePaper);
        Assert.False(owner.Proof.Options.SimulateBlack);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad\nname")]
    [InlineData("long")]
    public void InvalidNamesDoNotReplaceTheExistingLibrary(string name)
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        if (name == "long") name = new string('A', 129);

        Assert.Throws<ArgumentException>(() => library.Import(name, GrayProfile()));

        Assert.Equal("Original", Assert.Single(new ProofProfileLibrary(_directory).GetProfiles()).Name);
    }

    [Fact]
    public void ReimportingIdenticalContentKeepsOneStableNamedEntry()
    {
        var first = new ProofProfileLibrary(_directory).Import("Original", GrayProfile());

        var second = new ProofProfileLibrary(_directory).Import("Another label", GrayProfile());

        Assert.Equal(first, second);
        Assert.Equal(first, Assert.Single(new ProofProfileLibrary(_directory).GetProfiles()));
    }

    [Fact]
    public void MissingProfileKeepsTheRequestWithoutChoosingAnotherLibraryEntry()
    {
        byte[] anotherProfile = GrayProfile();
        anotherProfile[48] = 65;
        var library = new ProofProfileLibrary(_directory);
        library.Import("Other", anotherProfile);
        var requested = PdfProofPreset.Capture("Missing", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile()), gamutWarnings: true));

        var result = library.Resolve(requested);

        Assert.Equal(ProofProfileResolutionKind.Missing, result.Kind);
        Assert.Null(result.Settings.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", result.Settings.PrinterProfileSha256);
        Assert.True(result.Settings.Enabled);
        Assert.True(result.Settings.GamutWarnings);
        Assert.Single(library.GetProfiles());
    }

    [Fact]
    public void AReplacedProfileCopyIsDiagnosedWithoutSubstitution()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        byte[] replacement = GrayProfile();
        replacement[48] = 65;
        File.WriteAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.icc")), replacement);

        var result = library.Resolve(preset);

        Assert.Equal(ProofProfileResolutionKind.Changed, result.Kind);
        Assert.Null(result.Settings.PrinterProfile);
        Assert.True(result.Settings.Enabled);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", result.Settings.PrinterProfileSha256);
        Assert.Equal("Original", Assert.Single(library.GetProfiles()).Name);
    }

    [Fact]
    public void NoSelectedProfileDoesNotResolveAnUnreferencedFile()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Unselected", GrayProfile());
        File.WriteAllBytes(Path.Combine(_directory, ".icc"), GrayProfile());
        var preset = PdfProofPreset.Capture("No selection", new PdfDocumentProofSettings(enabled: true));

        var result = library.Resolve(preset);

        Assert.Equal(ProofProfileResolutionKind.NotSelected, result.Kind);
        Assert.Null(result.Settings.PrinterProfile);
        Assert.Null(result.Settings.PrinterProfileSha256);
        Assert.True(result.Settings.Enabled);
    }

    [Fact]
    public void DamagedProfileReturnsAStatusAndKeepsTheRequestedReference()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        File.WriteAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.icc")), [0, 1, 2]);

        var result = library.Resolve(preset);

        Assert.Equal(ProofProfileResolutionKind.Invalid, result.Kind);
        Assert.Null(result.Settings.PrinterProfile);
        Assert.True(result.Settings.Enabled);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", result.Settings.PrinterProfileSha256);
    }

    [Fact]
    public void LockedProfileRecoversWithoutLosingTheDocumentRequest()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        var owner = new PdfLogicalDocument { Proof = library.Resolve(preset).Settings };
        using (var locked = new FileStream(Assert.Single(Directory.GetFiles(_directory, "*.icc")),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = library.Resolve(preset);
            Assert.Equal(ProofProfileResolutionKind.Unreadable, unavailable.Kind);
            owner.Proof = unavailable.Settings;
            Assert.Null(owner.Proof.PrinterProfile);
            Assert.Equal(2L, owner.ProofGeneration);
        }

        var recovered = library.Resolve(preset);
        owner.Proof = recovered.Settings;
        Assert.Equal(ProofProfileResolutionKind.Matched, recovered.Kind);
        Assert.True(owner.Proof.Enabled);
        Assert.NotNull(owner.Proof.PrinterProfile);
        Assert.Equal(3L, owner.ProofGeneration);
    }

    [Fact]
    public void ADamagedManifestReadsThePreviousManifestAndKeepsTheDamagedCopyForReview()
    {
        var library = new ProofProfileLibrary(_directory);
        var first = library.Import("First", DistinctProfile(1));
        library.Import("Second", DistinctProfile(2));
        string manifest = Path.Combine(_directory, "profiles.json");
        File.WriteAllText(manifest, "{");

        Assert.Equal(first, Assert.Single(library.GetProfiles()));
        var third = library.Import("Third", DistinctProfile(3));

        Assert.Equal([first, third], new ProofProfileLibrary(_directory).GetProfiles());
        Assert.Equal("{", File.ReadAllText(manifest + ".recovery"));
    }

    [Fact]
    public void ResolvingDuringABriefImportWaitsInsteadOfReportingTheProfileUnreadable()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        var gate = new FileStream(Path.Combine(_directory, "library.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); gate.Dispose(); });

        var resolved = library.Resolve(preset);

        release.Wait();
        Assert.Equal(ProofProfileResolutionKind.Matched, resolved.Kind);
    }

    [Fact]
    public void OversizedImportIsRejectedWithoutAllocatingAnotherProfileBuffer()
    {
        var library = new ProofProfileLibrary(_directory);
        byte[] oversized = new byte[32 * 1024 * 1024 + 1];
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<FormatException>(() => library.Import("Too large", oversized));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 65_536);
        Assert.Empty(library.GetProfiles());
    }

    [Fact]
    public void OversizedStoredCopyIsDiagnosedBeforeAllocatingItsContents()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        var preset = PdfProofPreset.Capture("Stock", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(GrayProfile())));
        using (var file = File.OpenWrite(Assert.Single(Directory.GetFiles(_directory, "*.icc"))))
            file.SetLength(32 * 1024 * 1024 + 1);
        long before = GC.GetAllocatedBytesForCurrentThread();

        var result = library.Resolve(preset);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ProofProfileResolutionKind.Invalid, result.Kind);
        Assert.InRange(allocated, 0, 65_536);
        Assert.Null(result.Settings.PrinterProfile);
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("root")]
    [InlineData("missing")]
    [InlineData("array")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("reference")]
    public void DamagedCatalogIsRejectedWithoutOverwritingTheOriginal(string kind)
    {
        string json = kind switch
        {
            "syntax" => "{",
            "root" => "[]",
            "missing" => "{}",
            "array" => "{\"schemaVersion\":1,\"profiles\":null}",
            "unknown" => "{\"schemaVersion\":1,\"profiles\":[],\"sourcePath\":\"private-marker\"}",
            "duplicate" => "{\"schemaVersion\":1,\"schemaVersion\":1,\"profiles\":[]}",
            "name" => "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"\",\"contentSha256\":\"0000000000000000000000000000000000000000000000000000000000000001\"}]}",
            "reference" => "{\"schemaVersion\":1,\"profiles\":[{\"name\":\"Stock\",\"contentSha256\":\"../private-marker.icc\"}]}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Directory.CreateDirectory(_directory);
        string manifest = Path.Combine(_directory, "profiles.json");
        File.WriteAllText(manifest, json);
        var library = new ProofProfileLibrary(_directory);

        var error = Assert.Throws<FormatException>(() => library.GetProfiles());
        Assert.Throws<FormatException>(() => library.Import("New", GrayProfile()));

        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(json, File.ReadAllText(manifest));
    }

    [Fact]
    public void FailedCatalogPublicationPreservesExistingDataAndRemovesTheNewCopy()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        byte[] next = GrayProfile();
        next[48] = 65;
        var newPreset = PdfProofPreset.Capture("New", new PdfDocumentProofSettings(enabled: true,
            printerProfile: PdfColorProfile.Load(next)));
        Exception? failure;
        using (var locked = new FileStream(Path.Combine(_directory, "profiles.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
            failure = Record.Exception(() => library.Import("New", next));

        Assert.NotNull(failure);
        Assert.Equal("Original", Assert.Single(library.GetProfiles()).Name);
        Assert.Equal(ProofProfileResolutionKind.Missing, library.Resolve(newPreset).Kind);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        var error = Assert.IsType<IOException>(failure);
        Assert.DoesNotContain(_directory, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ProfileCountLimitDoesNotTurnTheCatalogIntoAnUnreadableFile()
    {
        var library = new ProofProfileLibrary(_directory);
        for (int index = 0; index < 128; index++)
            library.Import("Stock " + index, DistinctProfile(index));

        Assert.Throws<InvalidOperationException>(() => library.Import("Overflow", DistinctProfile(128)));

        Assert.Equal(128, library.GetProfiles().Count);
        Assert.DoesNotContain(library.GetProfiles(), entry => entry.Name == "Overflow");
    }

    [Fact]
    public void ImportedCopiesRetainOnlyValidatedDeclaredIccBytes()
    {
        byte[] padded = new byte[1024];
        GrayProfile().CopyTo(padded, 0);
        Array.Fill(padded, (byte)99, 156, padded.Length - 156);
        var library = new ProofProfileLibrary(_directory);

        var imported = library.Import("Padded", padded);

        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", imported.ContentSha256);
        Assert.Equal(156, new FileInfo(Assert.Single(Directory.GetFiles(_directory, "*.icc"))).Length);
        Assert.Equal(imported, Assert.Single(library.GetProfiles()));
    }

    [Fact]
    public void StorageLimitRejectsNewContentButAllowsRepairOfAnExistingCopy()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        string existing = Assert.Single(Directory.GetFiles(_directory, "*.icc"));
        using (var file = File.OpenWrite(existing)) file.SetLength(256L * 1024 * 1024);

        Assert.Throws<InvalidOperationException>(() => library.Import("Overflow", DistinctProfile(1)));

        Assert.Equal("Original", Assert.Single(library.GetProfiles()).Name);
        library.Import("Repair", GrayProfile());
        Assert.Equal("Original", Assert.Single(library.GetProfiles()).Name);
        Assert.Equal(156, new FileInfo(existing).Length);
    }

    [Fact]
    public void UnreadableCatalogIsPreservedAndReportsNoPersonalPath()
    {
        var library = new ProofProfileLibrary(_directory);
        library.Import("Original", GrayProfile());
        using (var locked = new FileStream(Path.Combine(_directory, "profiles.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => library.GetProfiles());
            Assert.DoesNotContain(_directory, error.ToString());
            Assert.Null(error.InnerException);
            Assert.Throws<IOException>(() => library.Import("New", DistinctProfile(1)));
        }
        Assert.Equal("Original", Assert.Single(library.GetProfiles()).Name);
    }

    [Fact]
    public void FutureCatalogSchemaIsNotRewrittenByAnOlderApplication()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "profiles.json");
        const string json = "{\"schemaVersion\":2,\"profiles\":[]}";
        File.WriteAllText(path, json);
        var library = new ProofProfileLibrary(_directory);

        Assert.Throws<NotSupportedException>(() => library.GetProfiles());
        Assert.Throws<NotSupportedException>(() => library.Import("New", GrayProfile()));

        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void CatalogUtf8LimitRejectsGrowthBeforeAllocatingItsContents()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "profiles.json");
        const string json = "{\"schemaVersion\":1,\"profiles\":[]}";
        string atLimit = json + new string(' ', 1_048_576 - json.Length);
        File.WriteAllText(path, atLimit);
        var library = new ProofProfileLibrary(_directory);
        Assert.Empty(library.GetProfiles());
        File.AppendAllText(path, " ");
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<FormatException>(() => library.GetProfiles());

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 65_536);
    }

    [Fact]
    public async Task ConcurrentInstancesRetainEveryImportThatReportedSuccess()
    {
        var accepted = new ConcurrentBag<ProofProfileEntry>();
        var busy = new ConcurrentBag<IOException>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            try { accepted.Add(new ProofProfileLibrary(_directory).Import("Stock " + index, DistinctProfile(index))); }
            catch (IOException error) { busy.Add(error); }
        })));

        Assert.NotEmpty(accepted);
        Assert.Equal(16, accepted.Count + busy.Count);
        var stored = new ProofProfileLibrary(_directory).GetProfiles();
        Assert.Equal(accepted.OrderBy(entry => entry.ContentSha256), stored.OrderBy(entry => entry.ContentSha256));
        Assert.All(busy, error => Assert.DoesNotContain(_directory, error.ToString()));
    }

    private static byte[] DistinctProfile(int index)
    {
        byte[] bytes = GrayProfile();
        bytes[48] = (byte)('A' + index / 26);
        bytes[49] = (byte)('A' + index % 26);
        return bytes;
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_directory);
        string parent = Path.TrimEndingDirectorySeparator(_parent) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test directory escaped its temporary parent.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private static byte[] GrayProfile()
    {
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
