using System.IO;
using System.Text;

namespace KillerPDF.Services;

/// <summary>Saving a report out of a document to a file the user chooses. A saved report is a data
/// export, so the gate answers first; reading it on screen and copying it are not outputs.</summary>
internal static class GatedReportSave
{
    /// <summary>Writes the report where the user chooses, once the gate allows the export. Returns the
    /// path written, or null when the gate refused or no path was chosen. A refusal asks for no path.</summary>
    internal static string? Save(Func<bool> mayExport, Func<string?> choosePath, Func<string> content)
    {
        ArgumentNullException.ThrowIfNull(mayExport);
        ArgumentNullException.ThrowIfNull(choosePath);
        ArgumentNullException.ThrowIfNull(content);
        if (!mayExport() || choosePath() is not string path) return null;
        // Through a temporary file and a move, so a failed write never leaves half a report in
        // place of a file the user chose to replace.
        AtomicFileOutput.Write(path, new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(true).GetBytes(content())).ToArray());
        return path;
    }
}
