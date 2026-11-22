using System.IO;
using System.Text.Json;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

[Collection("Environment variables")]
public sealed class AppDataPathsTests
{
    [Fact]
    public void StartupSmokeDataDoesNotUseInheritedPortableSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-smoke-isolation-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "KillerPDF-Portable.exe");
        string isolated = Path.Combine(root, "isolated");
        string? previousLauncher = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        string? previousSmokeRoot = AppDataPaths.StartupSmokeRoot;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
            AppDataPaths.StartupSmokeRoot = isolated;

            Assert.Null(AppDataPaths.GetPortableSetting("LastFile"));
            Assert.Equal(isolated, AppDataPaths.LocalRoot);
            Assert.Equal(isolated, AppDataPaths.UserRoot);
            Assert.Equal(Path.Combine(isolated, "signatures.json"), AppDataPaths.SignaturesFile);
            AppDataPaths.SetPortableSetting("Locale", "en-US");
            Assert.True(File.Exists(Path.Combine(isolated, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(root, "KillerPDF-Data")));
        }
        finally
        {
            AppDataPaths.StartupSmokeRoot = previousSmokeRoot;
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", previousLauncher);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableSettingsAreNotReplacedWhenTheExistingFileCannotBeRead()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-portable-damaged-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "KillerPDF-Portable.exe");
        string? previous = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
            string dataRoot = Path.Combine(root, "KillerPDF-Data");
            Directory.CreateDirectory(dataRoot);
            string settingsFile = Path.Combine(dataRoot, "settings.json");
            // A half-written or locked file reads as garbage; the other settings in it must survive.
            File.WriteAllText(settingsFile, "{\"Locale\":\"ja-JP\",\"Theme\":\"Dark\"");
            string damaged = File.ReadAllText(settingsFile);

            AppDataPaths.SetPortableSetting("Accent", "Red");

            Assert.Equal(damaged, File.ReadAllText(settingsFile));
            Assert.Empty(Directory.GetFiles(dataRoot, "*.tmp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task APortableSettingWaitsWhileAnotherCopyHoldsTheSettingsLock()
    {
        // The lock is a file beside the settings, so a copy in another Windows session or on
        // another machine sharing the folder is held off too, which a session-local mutex is not.
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-portable-lock-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "KillerPDF-Portable.exe");
        string? previous = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
            string dataRoot = Path.Combine(root, "KillerPDF-Data");
            Directory.CreateDirectory(dataRoot);
            string settingsFile = Path.Combine(dataRoot, "settings.json");

            Task write;
            using (new FileStream(settingsFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                write = Task.Run(() => AppDataPaths.SetPortableSetting("Locale", "ja-JP"));
                Thread.Sleep(400);
                Assert.False(File.Exists(settingsFile), "The setting was written while another copy held the lock.");
            }
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ja-JP", AppDataPaths.GetPortableSetting("Locale"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableSettingsAreStoredBesideTheLauncher()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-portable-data-{Guid.NewGuid():N}");
        string launcher = Path.Combine(root, "KillerPDF-Portable.exe");
        string? previous = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(launcher, []);
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);

            AppDataPaths.SetPortableSetting("Locale", "ja-JP");

            string dataRoot = Path.Combine(root, "KillerPDF-Data");
            Assert.Equal(dataRoot, AppDataPaths.PortableRoot);
            Assert.Equal("ja-JP", AppDataPaths.GetPortableSetting("Locale"));
            Dictionary<string, string>? settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
            Assert.Equal("ja-JP", settings?["Locale"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;
