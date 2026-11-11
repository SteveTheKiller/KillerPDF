using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubStylesheetEncodingTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void UnusableCommentBytesDoNotDiscardAStylesheetsValidRules(string encoding)
    {
        const string css = "/* Copyright \u00A9 */ p {color:red}";
        Encoding decoder = encoding switch
        {
            "utf16le" => Encoding.Unicode,
            "utf16be" => Encoding.BigEndianUnicode,
            _ => Encoding.Latin1
        };
        var source = new Book
        {
            Chapters = { Chapter("<p>A</p>", "<link rel='stylesheet' href='../other.css'/>") },
            ExtraManifest = "<item id='other' href='other.css' media-type='text/css'/>"
        };
        source.Resources.Add(("other.css", decoder.GetPreamble().Concat(decoder.GetBytes(css)).ToArray()));

        var layout = EpubDocument.Open(source.Build()).CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text.Trim());
        Assert.Equal(new PdfRgbColor(1, 0, 0), Assert.Single(page.TextPlacements).Color);
    }
}
