using System.IO;
using System.Text;

namespace KillerPDF.Services;

/// <summary>One exception's block in a crash log.</summary>
internal static class CrashExceptionText
{
    internal static void Append(StringBuilder sb, Exception ex)
    {
        sb.AppendLine($"Type    : {ex.GetType().FullName}");
        sb.AppendLine($"Message : {ex.Message}");
        // A FileNotFoundException can arrive with an empty Message (#515); its FileName still says
        // what was missing.
        if (ex is FileNotFoundException { FileName: { Length: > 0 } missing })
            sb.AppendLine($"FileName: {missing}");
        sb.AppendLine("Stack trace:");
        sb.AppendLine(ex.StackTrace ?? "(no stack trace)");
    }
}
