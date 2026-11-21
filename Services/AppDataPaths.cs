using System.IO;
using System.Text.Json;

namespace KillerPDF.Services;

internal static class AppDataPaths
{
    private const string LauncherPathVariable = "KILLERPDF_LAUNCHER_PATH";
    private static readonly Lock SettingsGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string DefaultLocalRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerPDF");
    internal static string? StartupSmokeRoot { get; set; }
    internal static string LocalRoot => StartupSmokeRoot ?? DefaultLocalRoot;

    internal static string? PortableRoot
    {
        get
        {
            if (StartupSmokeRoot is not null) return StartupSmokeRoot;
            try
            {
                string? launcher = Environment.GetEnvironmentVariable(LauncherPathVariable);
                if (string.IsNullOrWhiteSpace(launcher)) return null;
                string fullPath = Path.GetFullPath(launcher);
                if (!File.Exists(fullPath)) return null;
                string? directory = Path.GetDirectoryName(fullPath);
                return directory is null ? null : Path.Combine(directory, "KillerPDF-Data");
            }
            catch { return null; }
        }
    }

    internal static string UserRoot => PortableRoot ?? LocalRoot;
    internal static string SettingsFile => Path.Combine(UserRoot, "settings.json");
    internal static string SignaturesFile => Path.Combine(UserRoot, "signatures.json");
    internal static string TextFieldPresetsFile => Path.Combine(UserRoot, "text-field-presets.json");
    internal static string MacrosDirectory => Path.Combine(UserRoot, "macros");
    internal static string ImageAssetsDirectory => Path.Combine(UserRoot, "image-assets");
    internal static string ProofProfilesDirectory => Path.Combine(UserRoot, "color-management", "profiles");
    internal static string ProofPresetsDirectory => Path.Combine(UserRoot, "color-management", "presets");
    internal static string DocumentProofDirectory => Path.Combine(UserRoot, "color-management", "documents");
    internal static string TessDataDirectory => Path.Combine(UserRoot, "tessdata");

    internal static string? GetPortableSetting(string name)
    {
        if (PortableRoot is null) return null;
        lock (SettingsGate)
        {
            Dictionary<string, string> settings = ReadSettings();
            return settings.GetValueOrDefault(name);
        }
    }

    internal static void SetPortableSetting(string name, string value)
    {
        if (PortableRoot is null) return;
        lock (SettingsGate)
        using (AcrossProcesses())
        {
            // A file that exists but cannot be read right now (half written by another copy,
            // locked, or damaged) is left alone rather than replaced by a one-entry file.
            Dictionary<string, string>? settings = TryReadSettings();
            if (settings is null) return;
            settings[name] = value;
            WriteSettings(settings);
        }
    }

    internal static void RemovePortableSetting(string name)
    {
        if (PortableRoot is null) return;
        lock (SettingsGate)
        using (AcrossProcesses())
        {
            Dictionary<string, string>? settings = TryReadSettings();
            if (settings is null || !settings.Remove(name)) return;
            WriteSettings(settings);
        }
    }

    // Two portable copies sharing one data folder would otherwise each read, change and write the
    // file, and the later write would drop the other's change. The lock is an exclusively opened
    // file beside the settings, so it holds off copies in other Windows sessions and on other
    // machines sharing the folder, and Windows releases it when a copy dies. A copy that keeps it
    // for 15 seconds is hung; the write then goes ahead, and it still re-reads the file first.
    private static IDisposable? AcrossProcesses()
    {
        string path = SettingsFile + ".lock";
        long deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            try
            {
                var handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { File.SetAttributes(path, FileAttributes.Hidden); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return handle;
            }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(25); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private static Dictionary<string, string> ReadSettings() =>
        TryReadSettings() ?? new Dictionary<string, string>(StringComparer.Ordinal);

    // Null means the file is there but could not be read as settings, which is different from
    // there being no settings yet.
    private static Dictionary<string, string>? TryReadSettings()
    {
        if (!File.Exists(SettingsFile)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteSettings(Dictionary<string, string> settings)
    {
        Directory.CreateDirectory(UserRoot);
        // A unique temp name keeps two portable copies that share a data folder from racing on
        // the same intermediate file; the move itself is atomic.
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Flushed to the disk before the move, so a power cut cannot leave an empty file in
            // place of the settings.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
