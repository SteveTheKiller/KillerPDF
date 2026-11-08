using System.IO;
using System.Collections.Concurrent;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ProofPresetLibraryTests : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.GetTempPath());
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kp-proof-presets-" + Guid.NewGuid().ToString("N"));
    private const string Requested = "{\"schemaVersion\":1,\"name\":\"Paper proof\",\"proof\":{\"enabled\":true," +
        "\"printerProfileSha256\":\"7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1\"," +
        "\"renderingIntent\":\"saturation\",\"blackPointCompensation\":false," +
        "\"simulatePaper\":true,\"simulateBlack\":false,\"gamutWarnings\":true}}";

    [Fact]
    public void NamedSnapshotCanBeReopenedAndAppliedWithItsUnavailableProfileReference()
    {
        var owner = new PdfLogicalDocument { Proof = PdfProofPreset.FromJson(Requested).ResolveProfile(null) };
        var preset = PdfProofPreset.Capture("Paper proof", owner.Proof);
        new ProofPresetLibrary(_directory).Save(preset);
        owner.Proof = new PdfDocumentProofSettings();

        var restored = Assert.Single(new ProofPresetLibrary(_directory).GetPresets());
        var other = new PdfLogicalDocument { Proof = restored.ResolveProfile(null) };
        Assert.Equal("Paper proof", restored.Name);
        Assert.True(other.Proof.Enabled);
        Assert.True(other.Proof.GamutWarnings);
        Assert.Null(other.Proof.PrinterProfile);
        Assert.Equal("7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1", other.Proof.PrinterProfileSha256);
        Assert.Equal(PdfRenderingIntent.Saturation, other.Proof.Options.RenderingIntent);
        Assert.False(other.Proof.Options.BlackPointCompensation);
        Assert.True(other.Proof.Options.SimulatePaper);
        Assert.False(other.Proof.Options.SimulateBlack);
        Assert.False(owner.Proof.Enabled);
    }

    [Fact]
    public void DuplicateNamesRequireExplicitReplacementWithoutChangingStoredSettings()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        var replacement = PdfProofPreset.Capture("PAPER PROOF", new PdfDocumentProofSettings());

        Assert.Throws<InvalidOperationException>(() => library.Save(replacement));
        var stored = Assert.Single(new ProofPresetLibrary(_directory).GetPresets());
        Assert.True(stored.Enabled);
        Assert.Equal("Paper proof", stored.Name);
    }

    [Fact]
    public void ExplicitReplacementLeavesPreviouslyAppliedDocumentsAndOtherPresetsAlone()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        library.Save(PdfProofPreset.Capture("Other", new PdfDocumentProofSettings()));
        var owner = new PdfLogicalDocument { Proof = library.GetPresets()[0].ResolveProfile(null) };

        library.Save(PdfProofPreset.Capture("PAPER PROOF", new PdfDocumentProofSettings()), replaceExisting: true);

        var stored = new ProofPresetLibrary(_directory).GetPresets();
        Assert.Equal(2, stored.Count);
        Assert.Equal("PAPER PROOF", stored[0].Name);
        Assert.False(stored[0].Enabled);
        Assert.Null(stored[0].PrinterProfileSha256);
        Assert.Equal("Other", stored[1].Name);
        Assert.True(owner.Proof.Enabled);
        Assert.True(owner.Proof.Options.SimulatePaper);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"presets\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"presets\":[],\"extra\":1}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"presets\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"presets\":{},\"presets\":[]}")]
    [InlineData("{\"schemaVersion\":\"1\",\"presets\":[]}")]
    public void DamagedCatalogIsReplacedOnlyAfterItIsKeptForReview(string json)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "proof-presets.json");
        File.WriteAllText(path, json);
        var library = new ProofPresetLibrary(_directory);

        Assert.Empty(library.GetPresets());
        Assert.Equal(json, File.ReadAllText(path));
        library.Save(PdfProofPreset.FromJson(Requested));

        Assert.Single(library.GetPresets());
        Assert.Equal(json, File.ReadAllText(path + ".recovery"));
    }

    [Fact]
    public void ADamagedCatalogFallsBackToThePreviousCatalogAndKeepsTheDamagedCopyForReview()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));
        string path = Path.Combine(_directory, "proof-presets.json");
        File.WriteAllText(path, "{");

        Assert.Equal("Paper proof", Assert.Single(library.GetPresets()).Name);
        library.Save(PdfProofPreset.Capture("Third", new PdfDocumentProofSettings()));

        Assert.Equal(["Paper proof", "Third"], new ProofPresetLibrary(_directory).GetPresets().Select(item => item.Name));
        Assert.Equal("{", File.ReadAllText(path + ".recovery"));
    }

    [Fact]
    public void ADamagedCatalogWithoutAPreviousCopyStartsEmptyAndKeepsTheDamagedCopyForReview()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "proof-presets.json");
        File.WriteAllText(path, "{");
        var library = new ProofPresetLibrary(_directory);

        Assert.Empty(library.GetPresets());
        library.Save(PdfProofPreset.FromJson(Requested));

        Assert.Equal("Paper proof", Assert.Single(new ProofPresetLibrary(_directory).GetPresets()).Name);
        Assert.Equal("{", File.ReadAllText(path + ".recovery"));
    }

    [Fact]
    public void RestoringThePreviousCatalogAgainIsNotBlockedByTheRetainedCopy()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));
        Assert.Single(library.RestorePrevious());
        library.Save(PdfProofPreset.Capture("Third", new PdfDocumentProofSettings()));

        Assert.Single(library.RestorePrevious());
    }

    [Fact]
    public void ASaveWaitsForABrieflyHeldWriterGate()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        var gate = new FileStream(Path.Combine(_directory, "presets.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); gate.Dispose(); });

        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));

        release.Wait();
        Assert.Equal(2, new ProofPresetLibrary(_directory).GetPresets().Count);
    }

    [Fact]
    public void FutureCatalogSchemaIsPreservedForACompatibleApplication()
    {
        Directory.CreateDirectory(_directory);
        const string json = "{\"schemaVersion\":2,\"presets\":[]}";
        string path = Path.Combine(_directory, "proof-presets.json");
        File.WriteAllText(path, json);
        var library = new ProofPresetLibrary(_directory);
        Assert.Throws<NotSupportedException>(() => library.GetPresets());
        Assert.Throws<NotSupportedException>(() => library.Save(PdfProofPreset.FromJson(Requested)));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void DuplicateStoredNamesCannotSilentlyChooseDifferentProofSettings()
    {
        Directory.CreateDirectory(_directory);
        string duplicate = Requested.Replace("Paper proof", "PAPER PROOF", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_directory, "proof-presets.json"),
            "{\"schemaVersion\":1,\"presets\":[" + Requested + "," + duplicate + "]}");
        // Neither duplicate is chosen: the damaged catalog contributes no presets.
        Assert.Empty(new ProofPresetLibrary(_directory).GetPresets());
    }

    [Fact]
    public void OversizedCatalogIsRejectedBeforeAllocatingItsContents()
    {
        Directory.CreateDirectory(_directory);
        using (var file = File.Create(Path.Combine(_directory, "proof-presets.json"))) file.SetLength(8 * 1024 * 1024);
        var library = new ProofPresetLibrary(_directory);
        _ = library.GetType();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var presets = library.GetPresets();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(presets);
        Assert.True(allocated < 64 * 1024, $"Oversized rejection allocated {allocated} bytes.");
    }

    [Fact]
    public void FullLibraryAllowsReplacementButRefusesAnotherPreset()
    {
        var library = new ProofPresetLibrary(_directory);
        for (int index = 0; index < 128; index++)
            library.Save(PdfProofPreset.Capture("Proof" + index, new PdfDocumentProofSettings()));
        Assert.Throws<InvalidOperationException>(() =>
            library.Save(PdfProofPreset.Capture("Overflow", new PdfDocumentProofSettings())));
        library.Save(PdfProofPreset.Capture("Proof127", new PdfDocumentProofSettings(enabled: true)), replaceExisting: true);
        var stored = new ProofPresetLibrary(_directory).GetPresets();
        Assert.Equal(128, stored.Count);
        Assert.True(stored[127].Enabled);
        Assert.Equal("Proof0", stored[0].Name);
    }

    [Fact]
    public void PublicationReplacesTheCatalogWithoutWritingIntoAnExistingReader()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        string path = Path.Combine(_directory, "proof-presets.json");
        byte[] previous = File.ReadAllBytes(path);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));

        byte[] retained = new byte[reader.Length];
        reader.ReadExactly(retained);
        Assert.Equal(previous, retained);
        Assert.Equal(2, new ProofPresetLibrary(_directory).GetPresets().Count);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public void ConcurrentWriterGuardRefusesMutationAndAllowsRetryAfterRelease()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        var second = PdfProofPreset.Capture("Second", new PdfDocumentProofSettings());
        using (var gate = new FileStream(Path.Combine(_directory, "presets.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => new ProofPresetLibrary(_directory).Save(second));
            Assert.DoesNotContain(_directory, error.Message);
            Assert.Null(error.InnerException);
            Assert.Single(library.GetPresets());
        }
        library.Save(second);
        Assert.Equal(2, new ProofPresetLibrary(_directory).GetPresets().Count);
    }

    [Fact]
    public void LockedCatalogReportsSanitizedReadFailureAndPreservesTheStoredSnapshot()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        using (var locked = new FileStream(Path.Combine(_directory, "proof-presets.json"), FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => library.GetPresets());
            Assert.DoesNotContain(_directory, error.Message);
            Assert.Null(error.InnerException);
            Assert.Throws<IOException>(() => library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings())));
        }
        Assert.Equal("Paper proof", Assert.Single(library.GetPresets()).Name);
    }

    [Fact]
    public void ExplicitRecoveryRestoresPreviousCatalogAndRetainsDamagedBytesForReview()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));
        string path = Path.Combine(_directory, "proof-presets.json");
        const string damaged = "{damaged catalog";
        File.WriteAllText(path, damaged);
        Assert.Equal("Paper proof", Assert.Single(library.GetPresets()).Name);

        var restored = new ProofPresetLibrary(_directory).RestorePrevious();

        Assert.Equal("Paper proof", Assert.Single(restored).Name);
        Assert.True(Assert.Single(library.GetPresets()).Enabled);
        Assert.Equal(damaged, File.ReadAllText(path + ".recovery"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Fact]
    public void ALaterRecoveryRetainsTheNewestDamagedCatalogInsteadOfBlocking()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings()));
        string path = Path.Combine(_directory, "proof-presets.json");
        File.WriteAllText(path, "first damage");
        library.RestorePrevious();
        File.WriteAllText(path, "second damage");

        Assert.Equal("Paper proof", Assert.Single(library.RestorePrevious()).Name);

        Assert.Equal("second damage", File.ReadAllText(path + ".recovery"));
        Assert.Equal("Paper proof", Assert.Single(library.GetPresets()).Name);
    }

    [Fact]
    public void ExternalCatalogCannotBypassThePresetCountLimit()
    {
        Directory.CreateDirectory(_directory);
        string entries = string.Join(",", Enumerable.Range(0, 129).Select(index =>
            Requested.Replace("Paper proof", "Proof" + index, StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(_directory, "proof-presets.json"),
            "{\"schemaVersion\":1,\"presets\":[" + entries + "]}");
        Assert.Empty(new ProofPresetLibrary(_directory).GetPresets());
    }

    [Fact]
    public void FailedPublicationPreservesTheCurrentCatalogAndRemovesTemporaryFiles()
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        using (var reader = new FileStream(Path.Combine(_directory, "proof-presets.json"), FileMode.Open,
            FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => library.Save(PdfProofPreset.Capture("Second", new PdfDocumentProofSettings())));
        Assert.Equal("Paper proof", Assert.Single(library.GetPresets()).Name);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"presets\":[{}]}")]
    public void InvalidBackupCannotReplaceTheCurrentCatalog(string invalid)
    {
        var library = new ProofPresetLibrary(_directory);
        library.Save(PdfProofPreset.FromJson(Requested));
        string path = Path.Combine(_directory, "proof-presets.json");
        byte[] before = File.ReadAllBytes(path);
        File.WriteAllText(path + ".bak", invalid);
        Assert.Throws<FormatException>(() => library.RestorePrevious());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".recovery"));
    }

    [Fact]
    public void MissingLibraryIsEmptyButMissingBackupCannotBeRestored()
    {
        var library = new ProofPresetLibrary(_directory);
        Assert.Empty(library.GetPresets());
        var error = Assert.Throws<IOException>(() => library.RestorePrevious());
        Assert.DoesNotContain(_directory, error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(library.GetPresets());
    }

    [Fact]
    public void ExactUtf8SizeLimitAcceptsAValidCatalog()
    {
        Directory.CreateDirectory(_directory);
        const string json = "{\"schemaVersion\":1,\"presets\":[]}";
        File.WriteAllText(Path.Combine(_directory, "proof-presets.json"), json.PadRight(1024 * 1024));
        Assert.Empty(new ProofPresetLibrary(_directory).GetPresets());
    }

    [Fact]
    public async Task ConcurrentSuccessfulSavesAreAllRetainedAcrossLibraryInstances()
    {
        var successful = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            string name = "Proof" + index;
            try
            {
                new ProofPresetLibrary(_directory).Save(PdfProofPreset.Capture(name, new PdfDocumentProofSettings()));
                successful.Add(name);
            }
            catch (IOException) { }
        })));
        Assert.NotEmpty(successful);
        var stored = new ProofPresetLibrary(_directory).GetPresets();
        Assert.Equal(successful.Count, stored.Count);
        foreach (string name in successful) Assert.Contains(stored, item => item.Name == name);
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(_directory);
        if (!resolved.StartsWith(_parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its parent.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
