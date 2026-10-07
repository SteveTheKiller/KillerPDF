using System.Reflection;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests;

public sealed class PdfRendererReferenceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(31, false)]
    [InlineData(31, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(33, true)]
    public void ResourceReferences_PreserveChainLimitAndRejectCycles(int count, bool cycle)
    {
        using var input = new MemoryStream(CreatePdf(count, cycle));
        PdfDocument document = PdfDocument.Open(input);
        var renderer = new PdfPageRenderer(document);
        MethodInfo resolve = typeof(PdfPageRenderer).GetMethod("Resolve",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        PdfObject value = count == 0 ? new PdfInteger(42) : new PdfIndirectReference(4, 0);
        if (cycle || count > 32)
        {
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(
                () => resolve.Invoke(renderer, [value]));
            FormatException cause = Assert.IsType<FormatException>(failure.InnerException);
            Assert.Equal("An image resource contains an invalid reference chain.", cause.Message);
        }
        else
        {
            PdfInteger result = Assert.IsType<PdfInteger>(resolve.Invoke(renderer, [value]));
            Assert.Equal(42, result.Value);
            if (count == 0) Assert.Same(value, result);
        }
    }

    private static byte[] CreatePdf(int count, bool cycle)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>"
        };
        for (int index = 0; index < count; index++)
            objects.Add(index + 1 < count ? $"{index + 5} 0 R" : cycle ? "4 0 R" : "42");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(source.Length);
            source.Append($"{index + 1} 0 obj {objects[index]} endobj\n");
        }
        int xref = source.Length;
        source.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f\n");
        foreach (int offset in offsets) source.Append($"{offset:0000000000} 00000 n\n");
        source.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
