using System.IO;
using KillerPdf.Engine.Authoring;

namespace KillerPDF.Services;

/// <summary>Persists validated text-field presets for the desktop application.</summary>
internal sealed class TextFieldPresetStore
{
    private readonly string _directory;
    private readonly string _file;

    internal TextFieldPresetStore()
        : this(AppDataPaths.UserRoot, AppDataPaths.TextFieldPresetsFile) { }

    internal TextFieldPresetStore(string directory, string file)
    {
        _directory = directory;
        _file = file;
    }

    internal PdfTextFieldPresetCollection Load()
    {
        if (!File.Exists(_file)) return new PdfTextFieldPresetCollection([]);
        return PdfTextFieldPresetCollection.FromJson(File.ReadAllText(_file));
    }

    internal void Save(PdfTextFieldPresetCollection presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        Directory.CreateDirectory(_directory);
        string temporary = _file + ".tmp";
        File.WriteAllText(temporary, presets.ToJson(indented: true));
        File.Move(temporary, _file, overwrite: true);
    }
}
