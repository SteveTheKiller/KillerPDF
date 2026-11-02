using System.Collections.Frozen;

namespace KillerPdf.Engine.Parsing;

/// <summary>The operators PDF content streams are written with, as ISO 32000-2 lists them.</summary>
public static class PdfContentOperators
{
    private static readonly FrozenSet<string> Known = new[]
    {
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1",
        "Do", "DP", "EI", "EMC", "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J",
        "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC", "sc",
        "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr", "Ts", "Tw",
        "Tz", "v", "w", "W", "W*", "y", "'", "\""
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether a name is one of the content operators. A token merely shaped like one, such as
    /// a short run of letters read from ciphertext, is not.</summary>
    public static bool IsKnown(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Known.Contains(name);
    }
}
