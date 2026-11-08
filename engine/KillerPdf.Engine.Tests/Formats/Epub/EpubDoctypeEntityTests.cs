using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// EP-BF-015: XHTML 1.0 doctypes and the HTML named entities are read without fetching or processing a DTD.
public sealed class EpubDoctypeEntityTests
{
    private const string Strict = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">";
    private const string Transitional = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\"\n  \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">\n";

    [Fact]
    public void AnXhtml10DoctypeWithNamedEntitiesReadsAsTheirCharacters()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A&nbsp;A&mdash;A&amp;&eacute;</p>", doctype: Strict) } }.Build());

        // nbsp is U+00A0, mdash U+2014, eacute U+00E9 (HTML 4.01 section 24); amp stays the XML entity.
        Assert.Equal("A" + (char)0xA0 + "A" + (char)0x2014 + "A&" + (char)0xE9, book.ReadText(0));
        Assert.Empty(book.CreateLayout(Options()).ChapterDiagnostics);
    }

    [Fact]
    public void ATransitionalDoctypeAfterTheXmlDeclarationIsAccepted()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>", doctype: Transitional) } }.Build());

        Assert.Equal("A", book.ReadText(0));
    }

    [Theory]
    [InlineData("<!DOCTYPE html [ <!ENTITY x \"A\"> ]>", "<p>&x;</p>")]
    [InlineData("", "<p>&notanentity;</p>")]
    public void AnInternalSubsetOrAnUnknownEntityStillRefusesOnlyItsChapter(string doctype, string body)
    {
        var layout = EpubDocument.Open(new Book { Chapters = { Chapter(body, doctype: doctype), Chapter("<p>A</p>") } }.Build())
            .CreateLayout(Options());

        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(0, diagnostic.SpineIndex);
        Assert.Equal(EpubOmissionReason.InvalidContent, diagnostic.Reason);
    }
}
