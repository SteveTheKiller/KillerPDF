using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubUtf16EntityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HtmlNamedEntitiesInUtf16ReadAndLayOutWithoutChangingTheSource(bool bigEndian)
    {
        byte[] source = new Book { Chapters = { Chapter("<p>A</p>") } }.Build();
        Encoding encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        byte[] chapter = encoding.GetPreamble().Concat(encoding.GetBytes(Chapter("<p>A&nbsp;A</p>", doctype: "<!DOCTYPE html>"))).ToArray();
        using var data = new MemoryStream();
        data.Write(source);
        using (var zip = new ZipArchive(data, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("OEBPS/text/c0.xhtml")!.Delete();
            using var entry = zip.CreateEntry("OEBPS/text/c0.xhtml").Open();
            entry.Write(chapter);
        }
        var book = EpubDocument.Open(data.ToArray());

        Assert.Equal("A\u00A0A", book.ReadText(0));
        var layout = book.CreateLayout(Options());
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal("A\u00A0A", Assert.Single(layout.Pages).Text);
        Assert.Equal(chapter, book.ReadResource("OEBPS/text/c0.xhtml"));
    }
}
