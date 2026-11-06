using System.IO;
using System.Buffers.Binary;
using KillerPDF.Services;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPDF.Tests;

[Collection("Environment variables")]
public sealed class ProofStoreDefaultPathsTests
{
    [Fact]
    public void DefaultStoresReopenInsideIsolatedUserDataWithoutMixingCatalogs()
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "kp-proof-defaults-" + Guid.NewGuid().ToString("N"));
        string? previous = AppDataPaths.StartupSmokeRoot;
        try
        {
            AppDataPaths.StartupSmokeRoot = root;
            var profiles = new ProofProfileLibrary();
            Assert.Empty(profiles.GetProfiles());
            byte[] profileBytes = GrayProfile();
            profiles.Import("Gray", profileBytes);
            var preset = PdfProofPreset.Capture("Paper", new PdfDocumentProofSettings(enabled: true,
                printerProfile: PdfColorProfile.Load(profileBytes)));
            new ProofPresetLibrary().Save(preset);
            string path = Path.Combine(root, "example.pdf");
            const string hash = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
            new DocumentProofStore().Save(path, hash, preset.ResolveProfile(null));

            Assert.Equal("Paper", Assert.Single(new ProofPresetLibrary().GetPresets()).Name);
            Assert.True(new DocumentProofStore().Restore(path, hash, new ProofProfileLibrary()).Settings.Enabled);
            Assert.Equal("Gray", Assert.Single(new ProofProfileLibrary().GetProfiles()).Name);
            Assert.Equal(ProofProfileResolutionKind.Matched,
                new DocumentProofStore().Restore(path, hash, new ProofProfileLibrary()).ProfileKind);
            Assert.True(File.Exists(Path.Combine(root, "color-management", "presets", "proof-presets.json")));
            Assert.True(File.Exists(Path.Combine(root, "color-management", "documents", "proof-presets.json")));
            Assert.True(File.Exists(Path.Combine(root, "color-management", "profiles",
                "7F7BE1EAABD868E6F6D9C72E24DF59BB195CCEAAEBA23644FD0C814A72D49FB1.icc")));
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
        }
        finally
        {
            AppDataPaths.StartupSmokeRoot = previous;
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its parent.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    [Fact]
    public void PortableStoresStayUnderThePortableLauncherDataRoot()
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "kp-proof-portable-" + Guid.NewGuid().ToString("N"));
        string? previousSmoke = AppDataPaths.StartupSmokeRoot;
        string? previousLauncher = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            string launcher = Path.Combine(root, "KillerPDF-Portable.exe");
            File.WriteAllBytes(launcher, []);
            AppDataPaths.StartupSmokeRoot = null;
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
            new ProofProfileLibrary().Import("Gray", GrayProfile());
            var preset = PdfProofPreset.Capture("Portable paper", new PdfDocumentProofSettings(enabled: true));
            new ProofPresetLibrary().Save(preset);
            string path = Path.Combine(root, "example.pdf");
            const string hash = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
            new DocumentProofStore().Save(path, hash, preset.ResolveProfile(null));
            Assert.True(new DocumentProofStore().Restore(path, hash, new ProofProfileLibrary()).Settings.Enabled);
            string data = Path.Combine(root, "KillerPDF-Data", "color-management");
            Assert.True(File.Exists(Path.Combine(data, "profiles", "profiles.json")));
            Assert.True(File.Exists(Path.Combine(data, "presets", "proof-presets.json")));
            Assert.True(File.Exists(Path.Combine(data, "documents", "proof-presets.json")));
            Assert.Equal("Portable paper", Assert.Single(new ProofPresetLibrary().GetPresets()).Name);
        }
        finally
        {
            AppDataPaths.StartupSmokeRoot = previousSmoke;
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", previousLauncher);
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its parent.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
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
