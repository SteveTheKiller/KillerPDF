using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    [Fact]
    public void ReadsUnicodeXhtmlBodyTextInDocumentOrderWithStructuralSeparators()
    {
        var book = TextBook("<p>Hello <em>世界</em> &amp; مرحبا</p><p>Second line<br/>continued</p>",
            "<title>Ignored title</title><style>Ignored style</style>");
        Assert.Equal("Hello 世界 & مرحبا\n\nSecond line\ncontinued", book.ReadText(0));
    }

    [Fact]
    public void SeparatesHeadingsNestedBlocksAndListItems()
    {
        var book = TextBook("<h1>Heading</h1><div>Intro <strong>text</strong><ul><li>One</li><li>Two</li></ul></div><p>End</p>");
        Assert.Equal("Heading\n\nIntro text\nOne\nTwo\n\nEnd", book.ReadText(0));
    }

    [Theory]
    [InlineData("<script>execute()</script><p>Text</p>", "")]
    [InlineData("<p>Text</p>", "<script src='code.js'/>")]
    [InlineData("<p onclick='execute()'>Text</p>", "")]
    public void DiagnosesScriptedXhtmlWithoutExecutingIt(string body, string head)
    {
        Assert.Throws<NotSupportedException>(() => TextBook(body, head).ReadText(0));
    }

    [Theory]
    [InlineData("<p><img src='https://example.invalid/image.png' alt='Image'/></p>", "")]
    [InlineData("<p><a href='javascript:execute()'>Text</a></p>", "")]
    [InlineData("<p><a href='file:///C:/private.txt'>Text</a></p>", "")]
    [InlineData("<p><a href='../../outside.xhtml'>Text</a></p>", "")]
    [InlineData("<p>Text</p>", "<link rel='stylesheet' href='https://example.invalid/style.css'/>")]
    public void DiagnosesExternalOrUnsafeXhtmlReferencesWithoutFetching(string body, string head)
    {
        Assert.Throws<NotSupportedException>(() => TextBook(body, head).ReadText(0));
    }

    [Theory]
    [InlineData("<table><caption>Caption</caption><tr><td>One</td></tr></table>")]
    [InlineData("<pre>  Code\n  Indent</pre>")]
    [InlineData("<iframe>Fallback</iframe>")]
    [InlineData("<img alt='Alternative'/>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><text>Diagram</text></svg>")]
    [InlineData("<ruby>字<rt>Pronunciation</rt></ruby>")]
    public void DiagnosesUnsupportedXhtmlInsteadOfInventingTextLayout(string body)
    {
        Assert.Throws<NotSupportedException>(() => TextBook(body).ReadText(0));
    }

    [Theory]
    [InlineData("image/svg+xml", "")]
    [InlineData("application/xhtml+xml", "scripted")]
    public void DiagnosesUnsupportedSpineContentDeclarations(string mediaType, string properties)
    {
        var book = TextBook("<p>Text</p>", mediaType: mediaType, properties: properties);
        Assert.Throws<NotSupportedException>(() => book.ReadText(0));
    }

    [Fact]
    public void HonorsTextReadCancellation()
    {
        Assert.Throws<OperationCanceledException>(() => TextBook("<p>Text</p>").ReadText(0, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundsXhtmlTextProcessingDepthAndNodeCount(bool depth)
    {
        var body = depth ? string.Concat(Enumerable.Repeat("<span>", 129)) + "Text" + string.Concat(Enumerable.Repeat("</span>", 129))
            : string.Concat(Enumerable.Repeat("<span/>", 100001));
        Assert.Throws<InvalidDataException>(() => TextBook(body).ReadText(0));
    }

    [Fact]
    public void PreservesConsecutiveExplicitLineBreaks()
    {
        Assert.Equal("One\n\nTwo", TextBook("<p>One<br/><br/>Two</p>").ReadText(0));
    }

    [Fact]
    public void DiagnosesXhtmlProcessingInstructionsWithoutApplyingThem()
    {
        var book = TextBook("<p>Text</p>", "<?xml-stylesheet href='https://example.invalid/style.css'?>");
        Assert.Throws<NotSupportedException>(() => book.ReadText(0));
    }

    [Fact]
    public void PreservesUnicodeWhitespaceAndInlineOrderWhileIgnoringStyleText()
    {
        var body = "<p>A&#160;B e&#769; 😀 <span>tail</span></p><style>Ignored CSS</style><p><a href='two.xhtml#x'>Link</a></p>";
        Assert.Equal("A\u00a0B e\u0301 😀 tail\n\nLink", TextBook(body).ReadText(0));
        Assert.Equal("", TextBook("").ReadText(0));
    }

    [Fact]
    public void SelectsTextBySpineOrderRatherThanManifestOrder()
    {
        var book = EpubDocument.Open(Book());
        Assert.Equal("One", book.ReadText(0));
        Assert.Equal("Two", book.ReadText(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void RejectsInvalidTextSpineIndexes(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EpubDocument.Open(Book()).ReadText(index));
    }

    [Theory]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'><head/></html>")]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'><body/><body/></html>")]
    [InlineData("<html><body>Text</body></html>")]
    [InlineData("<html xmlns='http://www.w3.org/1999/xhtml'><body><p>Broken</body></html>")]
    [InlineData("<!DOCTYPE html [<!ENTITY entity 'expanded'>]><html xmlns='http://www.w3.org/1999/xhtml'><body>&entity;</body></html>")]
    [InlineData("<!DOCTYPE html SYSTEM 'https://example.invalid/xhtml.dtd'><html xmlns='http://www.w3.org/1999/xhtml'><body>Text</body></html>")]
    public void RejectsMalformedOrUnsafeXhtmlTextDocuments(string content)
    {
        var package = Package.Replace("href=\"one.xhtml\"", "href=\"text.xhtml\"");
        var book = EpubDocument.Open(Book(package, ("EPUB/text.xhtml", content)));
        Assert.Throws<InvalidDataException>(() => book.ReadText(0));
    }

    [Fact]
    public void ReadsTextWithCanonicalXhtml11PublicDeclaration()
    {
        const string content = "<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'>"
            + "<html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p>Text</p></body></html>";
        var package = Package.Replace("href=\"one.xhtml\"", "href=\"text.xhtml\"");
        var book = EpubDocument.Open(Book(package, ("EPUB/text.xhtml", content)));
        Assert.Equal("Text", book.ReadText(0));
    }

    [Fact]
    public void AcceptsTextAtDepthAndNodeCountLimits()
    {
        var nested = string.Concat(Enumerable.Repeat("<span>", 126)) + "Text" + string.Concat(Enumerable.Repeat("</span>", 126));
        Assert.Equal("Text", TextBook(nested).ReadText(0));
        Assert.Equal("", TextBook(string.Concat(Enumerable.Repeat("<span/>", 99994))).ReadText(0));
    }

    private static EpubDocument TextBook(string body, string head = "", string mediaType = "application/xhtml+xml", string properties = "")
    {
        var package = Package.Replace("id=\"first\" href=\"one.xhtml\" media-type=\"application/xhtml+xml\"",
            "id=\"first\" href=\"text.xhtml\" media-type=\"" + mediaType + "\" properties=\"" + properties + "\"");
        var content = "<html xmlns='http://www.w3.org/1999/xhtml'><head>" + head + "</head><body>" + body + "</body></html>";
        return EpubDocument.Open(Book(package, ("EPUB/text.xhtml", content)));
    }
}
