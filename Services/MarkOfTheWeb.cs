using System.IO;

namespace KillerPDF.Services
{
    /// <summary>
    /// Tags a file pulled out of a PDF with the Internet zone, as a browser tags a download, so
    /// Windows SmartScreen and Office Protected View treat it as coming from outside.
    /// </summary>
    internal static class MarkOfTheWeb
    {
        internal const string ZoneIdentifier = "[ZoneTransfer]\r\nZoneId=3\r\n";

        // Best effort: FAT drives, network shares and some sync folders have no alternate streams,
        // and a missing tag must not undo a copy that already succeeded.
        internal static bool TryApply(string path)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                File.WriteAllText(path + ":Zone.Identifier", ZoneIdentifier);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }
    }
}
