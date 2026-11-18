using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Controls;
using KillerPDF.Services;
using System.IO;
using System.Windows.Media;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RedactionPageEditTests
{
    [Theory]
    [InlineData("delete", 0)]
    [InlineData("insert", 2)]
    [InlineData("move", 0)]
    [InlineData("duplicate", 1)]
    public void MarksFollowTheMarkedPageThroughStructuralEdits(string operation, int expectedPage)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddPage(100, 100, new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                    .MoveText(10, 50).ShowLatin1Text("UNRELATED").EndText())
                .AddPage(100, 100, new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                    .MoveText(10, 50).ShowLatin1Text("SECRET").EndText()).Build();
            File.WriteAllBytes(path, source);
            var mark = new RedactionMark(1, 5, 40, 80, 65, "Hidden", Colors.Black, AnnotationFlags: 36);
            var marks = new RedactionMarkSnapshot([mark], foundText: ["SECRET"]);
            RedactionMarkSnapshot mapped = RedactionPageEdit.Apply(path, marks, file =>
            {
                switch (operation)
                {
                    case "delete": PdfEngineIntegration.RemovePages(file, [0]); break;
                    case "insert": PdfEngineIntegration.InsertBlankPage(file, 0, 100, 100); break;
                    case "move": PdfEngineIntegration.MovePage(file, 1, 0); break;
                    case "duplicate": PdfEngineIntegration.DuplicatePage(file, 1); break;
                }
            });
            Assert.Equal(operation == "duplicate" ? 2 : 1, mapped.Marks.Count);
            Assert.Equal(["SECRET"], mapped.FoundText);
            Assert.Equal(mark with { PageIndex = expectedPage }, mapped.Marks[0]);
            if (operation == "duplicate") Assert.Equal(mark with { PageIndex = 2 }, mapped.Marks[1]);
            PdfEngineIntegration.ApplyRedaction(path, mapped.Marks.Select(item => new PdfRedactionRegion(
                item.PageIndex, new PdfContentBounds(item.Left, item.Bottom, item.Right, item.Top))).ToList(), []);
            PdfDocument output = PdfDocument.Open(File.ReadAllBytes(path));
            var reader = new PdfPageContentReader(output);
            string text = string.Concat(Enumerable.Range(0, PdfPageInformation.Read(output).Count)
                .SelectMany(page => reader.Read(page).Letters).Select(letter => letter.Value));
            Assert.DoesNotContain("SECRET", text);
            if (operation != "delete") Assert.Contains("UNRELATED", text);
            Assert.Equal(mark, Assert.Single(marks.Marks));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void DividedSpreadCopiesFullMarksIncludingHiddenContentToBothHalves(int rotation)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(200, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 4).MoveText(80, 50).ShowLatin1Text("LEFT")
                .MoveText(30, 0).ShowLatin1Text("RIGHT").EndText()).Build());
            PdfEngineIntegration.ApplyPageRotations(path, new Dictionary<int, int> { [0] = rotation });
            var mark = new RedactionMark(0, 75, 25, 125, 75, "Both halves", Colors.Red);
            var before = new RedactionMarkSnapshot([mark]);
            var after = RedactionPageEdit.Apply(path, before, file => PdfEngineIntegration.DivideSpreads(file,
                [new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical)]));
            Assert.Equal(2, after.Marks.Count);
            Assert.Equal(new[] { 0, 1 }, after.Marks.Select(item => item.PageIndex));
            foreach (var item in after.Marks)
            {
                Assert.Equal(mark.OverlayText, item.OverlayText);
                Assert.Equal(mark.Fill, item.Fill);
                Assert.Equal(2500, (item.Right - item.Left) * (item.Top - item.Bottom));
            }
            Assert.Equal(mark, Assert.Single(before.Marks));
            PdfEngineIntegration.ApplyRedaction(path, after.Marks.Select(item => new PdfRedactionRegion(
                item.PageIndex, new PdfContentBounds(item.Left, item.Bottom, item.Right, item.Top))).ToList(), []);
            var reader = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path)));
            foreach (int page in new[] { 0, 1 })
            {
                string text = string.Concat(reader.Read(page).Letters.Select(letter => letter.Value));
                Assert.DoesNotContain("LEFT", text);
                Assert.DoesNotContain("RIGHT", text);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedPageEditLeavesThePendingSnapshotUnchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(100, 100, new PdfContentStreamBuilder()).Build());
            var mark = new RedactionMark(0, 10, 10, 20, 20, "Pending", Colors.Blue);
            var before = new RedactionMarkSnapshot([mark]);
            Assert.Throws<IOException>(() => RedactionPageEdit.Apply(path, before,
                _ => throw new IOException("Injected page-edit failure.")));
            Assert.Equal(mark, Assert.Single(before.Marks));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DeletingTheMarkedPageRemovesItsMark()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(100, 100, new PdfContentStreamBuilder())
                .AddPage(100, 100, new PdfContentStreamBuilder()).Build());
            var after = RedactionPageEdit.Apply(path, new([new RedactionMark(0, 10, 10, 20, 20)]),
                file => PdfEngineIntegration.RemovePages(file, [0]));
            Assert.Empty(after.Marks);
        }
        finally { File.Delete(path); }
    }
}
