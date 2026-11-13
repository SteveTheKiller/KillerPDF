using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A save counts every page's annotations to know where new ones go. One page with a broken /Annots value must
// cost only that page, from a single page-tree read, instead of a fallback that rereads the tree per page.
public sealed class PdfCommentReaderCountTests
{
    [Fact]
    public void TolerantCountsReadTheTreeOnceAndRecordTheUnreadablePage()
    {
        PdfDocument document = PdfDocument.Open(ThreePages());
        var unreadable = new List<int>();
        PdfPageTree.ReadCountOnThisThread = 0;

        IReadOnlyList<int> counts = PdfCommentReader.ReadPageAnnotationCounts(document, unreadable);

        Assert.Equal([0, 0, 2], counts);
        Assert.Equal([1], unreadable);
        Assert.Equal(1, PdfPageTree.ReadCountOnThisThread);
    }

    [Fact]
    public void TheStrictCountsStillRefuseABrokenAnnotationArray() =>
        Assert.Throws<InvalidOperationException>(() => PdfCommentReader.ReadPageAnnotationCounts(PdfDocument.Open(ThreePages())));

    [Fact]
    public void ANullAnnotationListCountsZeroAndACyclicOneIsRecordedAsUnreadable()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots null >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots 5 0 R >>",
            "5 0 R"
        ];
        PdfDocument document = PdfDocument.Open(Build(objects));
        var unreadable = new List<int>();

        IReadOnlyList<int> counts = PdfCommentReader.ReadPageAnnotationCounts(document, unreadable);

        Assert.Equal([0, 0], counts);
        Assert.Equal([1], unreadable);
    }

    // The count above says a null annotation list holds nothing, so a save adds the page's new annotations natively.
    // The annotation editor must then accept that page and give it a list of its own.
    [Fact]
    public void APageWithANullAnnotationListTakesANewAnnotation()
    {
        PdfDocument document = PdfDocument.Open(Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots null >>"
        ]));

        byte[] saved = new KillerPdf.Engine.Editing.PdfIncrementalAnnotationEditor(document)
            .AddTextNote(0, 20, 20, "Added")
            .Build();

        Assert.Equal([1], PdfCommentReader.ReadPageAnnotationCounts(PdfDocument.Open(saved)));
    }

    // Page 2's /Annots is a number, page 3 holds two (empty) entries.
    private static byte[] ThreePages() => Build(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots 7 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [null null] >>"
    ]);

    private static byte[] Build(string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
