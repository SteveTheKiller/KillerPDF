using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNavigationMacroTests
{
    [Fact]
    public void MacroGeneratesHeadingBookmarksAndClickableContents()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.HelveticaBold, 20).MoveText(30, 350)
                .ShowLatin1Text("Chapter").EndText()).Build();
        PdfMacro macro = new("Navigation",
        [
            PdfNavigationMacro.HeadingBookmarksStep(),
            PdfNavigationMacro.TableOfContentsStep(3)
        ]);

        PdfMacroFileResult result = Assert.Single(PdfMacroRunner.Run(macro, [source],
            (step, input, cancellationToken) =>
                PdfNavigationMacro.Execute(step, input, cancellationToken)));
        PdfDocument output = PdfDocument.Open(result.Data!.Value);

        Assert.True(result.Succeeded);
        Assert.Equal(2, new PdfIncrementalPageEditor(output).PageCount);
        Assert.Equal("Chapter", Assert.Single(PdfBookmarkReader.Read(output)).Title);
        Assert.Equal(1, Assert.Single(PdfLinkReader.ReadPage(output, 0))
            .DestinationPageIndex);
    }

    [Fact]
    public void AuditMacroPreservesBytesAndHeadingGenerationRequiresAcceptance()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfMacroStep audit = PdfNavigationMacro.AuditStep();

        ReadOnlyMemory<byte> unchanged = PdfNavigationMacro.Execute(audit, source);

        Assert.Equal(source, unchanged.ToArray());
        Assert.Empty(PdfNavigationMacro.Inspect(audit, PdfDocument.Open(source)));
        Assert.Throws<ArgumentException>(() => PdfNavigationMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.GenerateBookmarks,
                new Dictionary<string, string>
                {
                    ["minimumPointSize"] = "14",
                    ["maximumTitleLength"] = "160",
                    ["maximumDepth"] = "6",
                    ["acceptDetectedHeadings"] = "false"
                }), source));
    }

    [Fact]
    public void MacroRoundTripRemovesOnlyUnsafeUriLinks()
    {
        byte[] source = DocumentWithUnsafeAndUnresolvedLinks();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Safe links",
        [
            PdfNavigationMacro.RemoveUnsafeLinksStep()
        ]).ToJson());

        PdfMacroFileResult result = Assert.Single(PdfMacroRunner.Run(macro, [source],
            (step, input, cancellationToken) =>
                PdfNavigationMacro.Execute(step, input, cancellationToken)));
        PdfDocument output = PdfDocument.Open(result.Data!.Value);

        Assert.True(result.Succeeded);
        PdfNavigationFinding remaining = Assert.Single(PdfNavigationAudit.Inspect(output));
        Assert.Equal(PdfNavigationFindingCode.LinkUnresolvedDestination, remaining.Code);
    }

    private static byte[] DocumentWithUnsafeAndUnresolvedLinks()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R 6 0 R] >>",
            "<< >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (javascript:alert) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest (missing) >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
