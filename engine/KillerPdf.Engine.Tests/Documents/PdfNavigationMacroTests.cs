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
}
