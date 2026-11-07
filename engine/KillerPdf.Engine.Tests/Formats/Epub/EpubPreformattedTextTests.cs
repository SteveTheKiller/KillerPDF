using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubPreformattedTextTests
{
    [Fact]
    public void NestedPreformattedBlocksKeepExplicitUnsupportedResult()
    {
        byte[] source = Book("<pre>A<pre>A</pre> A</pre>"), original = source.ToArray();
        Assert.Throws<NotSupportedException>(() => EpubDocument.Open(source).ReadText(0));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<pre>\n  A\n</pre>", "\n  A\n")]
    [InlineData("<pre>A\t A</pre>", "A\t A")]
    [InlineData("<pre>A\r\n A</pre>", "A\n A")]
    [InlineData("<pre><![CDATA[ A\n  A ]]></pre>", " A\n  A ")]
    [InlineData("<pre> A<i>  A\n A</i> A </pre>", " A  A\n A A ")]
    [InlineData("<pre>A&#32;&#32;A</pre>", "A  A")]
    [InlineData("<p>  A\n  A </p>", "A A")]
    [InlineData("<pre>A  </pre><p>  A\n  A </p>", "A  \n\nA A")]
    [InlineData("<pre/>", "")]
    public void StructuralWhitespaceUsesItsOriginalPreformattedContext(string body, string expected)
    {
        byte[] source = Book(body), original = source.ToArray();
        Assert.Equal(expected, EpubDocument.Open(source).ReadText(0));
        Assert.Equal(original, source);
    }

    [Fact]
    public void StructuralTextKeepsTerminalPreformattedSpacesAndNewlines()
    {
        byte[] source = Book("<pre>A  \n </pre>"), original = source.ToArray();
        Assert.Equal("A  \n ", EpubDocument.Open(source).ReadText(0));
        Assert.Equal(original, source);
    }

    [Fact]
    public void StructuralTextPreservesPreformattedIndentationAndLineBreaks()
    {
        byte[] source = Book("<pre>  A\n    A</pre><p>A</p>"), original = source.ToArray();
        Assert.Equal("  A\n    A\n\nA", EpubDocument.Open(source).ReadText(0));
        Assert.Equal(original, source);
    }

    private static byte[] Book(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>pre</dc:identifier><dc:title>Preformatted</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head/><body>" + body + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
