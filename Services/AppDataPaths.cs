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
        {
            Dictionary<string, string>? settings = TryReadSettings();
            if (settings is null || !settings.Remove(name)) return;
            WriteSettings(settings);
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
