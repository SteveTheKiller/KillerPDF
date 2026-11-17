using System.Globalization;
using System.IO;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// #499: the viewer planned a redaction on the open document but applied it to a fresh save of the
// file, and a page the save left needing an image surfaced as the engine's raw refusal ("Pages i, j,
// k ... cannot be redacted without replacing them with an image"). ApplyRedaction now re-plans on
// the bytes it actually redacts and asks about any page the first plan did not approve.
public sealed class RedactionReplanTests
{
    private static readonly PdfContentBounds Middle = new(50, 40, 150, 60);

    [Fact]
    public void APageTheFileNeedsAnImageForIsAskedAboutInsteadOfRefused()
    {
        string path = Write(Page());
        try
        {
            IReadOnlyList<int>? asked = null;
            PdfContentRedactionResult result = PdfEngineIntegration.ApplyRedaction(path,
                [new PdfRedactionRegion(0, Middle)], [], pages => { asked = pages; return true; });

            Assert.Equal([0], asked);
            Assert.True(result.Verification.Succeeded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DecliningTheExtraImageCancelsAndLeavesTheFileAlone()
    {
        byte[] original = Page();
        string path = Write(original);
        try
        {
            Assert.Throws<OperationCanceledException>(() => PdfEngineIntegration.ApplyRedaction(path,
                [new PdfRedactionRegion(0, Middle)], [], _ => false));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnAlreadyApprovedPageIsNotAskedAboutAgain()
    {
        string path = Write(Page());
        try
        {
            bool asked = false;
            PdfEngineIntegration.ApplyRedaction(path, [new PdfRedactionRegion(0, Middle)], [0], _ => { asked = true; return true; });
            Assert.False(asked);
        }
        finally { File.Delete(path); }
    }

    private static string Write(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "kp-replan-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // One page whose stroke the region cuts: redacting it in place is impossible, so it needs an image.
    private static byte[] Page()
    {
        const string content = "1 w 0 0 m 100 100 l S";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
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
}
