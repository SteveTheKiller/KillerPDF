using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Stores validated PDF macros without allowing executable content.</summary>
internal sealed class PdfMacroStore
{
    private readonly string _directory;

    internal PdfMacroStore() : this(AppDataPaths.MacrosDirectory) { }

    internal PdfMacroStore(string directory) => _directory = directory;

    internal IReadOnlyList<PdfMacro> LoadAll()
    {
        if (!Directory.Exists(_directory)) return [];
        return Directory.EnumerateFiles(_directory, "*.json")
            .Select(file => PdfMacro.FromJson(File.ReadAllText(file)))
            .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal void Save(PdfMacro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, FileName(macro.Name));
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, macro.ToJson(indented: true));
        File.Move(temporary, file, overwrite: true);
    }

    internal void Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string file = Path.Combine(_directory, FileName(name));
        if (File.Exists(file)) File.Delete(file);
    }

    private static string FileName(string name) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(name)) + ".json";
}
