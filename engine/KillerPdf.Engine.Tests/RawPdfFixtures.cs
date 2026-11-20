using System.Globalization;
using System.Text;

namespace KillerPdf.Engine.Tests;

/// <summary>Hand-written classic PDFs for structures the builder never writes, such as repeated name-tree keys.</summary>
internal static class RawPdfFixtures
{
    internal static string StreamObject(string entries, string data) =>
        $"<< {entries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    // Objects are numbered from 1 in order, and object 1 is the catalog.
    internal static byte[] RawPdf(params string[] objects)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        text.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>
    /// The #455 iLovePDF shape: three C2PA manifests, payloads "manifest 0" to "manifest 2", all under the
    /// name-tree key "Content Credentials". With <paramref name="directSpecifications"/> the file
    /// specifications sit in the tree itself instead of being indirect objects.
    /// </summary>
    internal static byte[] RepeatedContentCredentials(bool directSpecifications = false)
    {
        static string Specification(int stream) =>
            $"<< /Type /Filespec /F (Content Credentials) /AFRelationship /C2PA_Manifest /EF << /F {stream} 0 R >> >>";
        string tree = directSpecifications
            ? $"<< /Names [(Content Credentials) {Specification(4)} (Content Credentials) {Specification(6)} (Content Credentials) {Specification(8)}] >>"
            : "<< /Names [(Content Credentials) 5 0 R (Content Credentials) 7 0 R (Content Credentials) 9 0 R] >>";
        return RawPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 10 0 R >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            StreamObject("/Type /EmbeddedFile", "manifest 0"),
            Specification(4),
            StreamObject("/Type /EmbeddedFile", "manifest 1"),
            Specification(6),
            StreamObject("/Type /EmbeddedFile", "manifest 2"),
            Specification(8),
            tree);
    }
}
