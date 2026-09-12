using System.IO;
using System.Text.Json;

namespace KillerPDF.Services;

internal static class AppDataPaths
{
    private const string LauncherPathVariable = "KILLERPDF_LAUNCHER_PATH";
    private static readonly Lock SettingsGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerPDF");

    internal static string? PortableRoot
    {
        get
        {
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
    internal static string ImageLibraryFile => Path.Combine(UserRoot, "imagelibrary.json");
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
            Dictionary<string, string> settings = ReadSettings();
            settings[name] = value;
            WriteSettings(settings);
        }
    }

    internal static void RemovePortableSetting(string name)
    {
        if (PortableRoot is null) return;
        lock (SettingsGate)
        {
            Dictionary<string, string> settings = ReadSettings();
            if (!settings.Remove(name)) return;
            WriteSettings(settings);
        }
    }

    private static Dictionary<string, string> ReadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(SettingsFile))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private static void WriteSettings(Dictionary<string, string> settings)
    {
        Directory.CreateDirectory(UserRoot);
        string temporary = SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsFile, overwrite: true);
    }
}
