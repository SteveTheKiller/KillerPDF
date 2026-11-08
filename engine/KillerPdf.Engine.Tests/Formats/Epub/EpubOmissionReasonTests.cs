using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Every chapter and content diagnostic carries a stable reason the app can localize next to its English message.
public sealed class EpubOmissionReasonTests
{
    [Theory]
    [InlineData("<p>A</p><script>x</script>", EpubOmissionReason.Scripting)]
    [InlineData("<p>A&#x05D7;</p>", EpubOmissionReason.UnsupportedScript)]
    [InlineData("<pre>A\tA</pre>", EpubOmissionReason.UnsupportedLayout)]
    public void ChapterRefusalsCarryTheirReason(string body, EpubOmissionReason reason)
    {
        var layout = Layout(new Book { Chapters = { Chapter(body) } });

        Assert.Equal(reason, Assert.Single(layout.ChapterDiagnostics).Reason);
    }

    [Fact]
    public void AnEncryptedChapterIsReportedAsEncrypted()
    {
        const string encryption = """
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/><enc:CipherData><enc:CipherReference URI="OEBPS/text/c0.xhtml"/></enc:CipherData></enc:EncryptedData>
            </encryption>
            """;
        var layout = Layout(new Book { Encryption = encryption, Chapters = { Chapter("<p>A</p>") } });

        Assert.Equal(EpubOmissionReason.Encrypted, Assert.Single(layout.ChapterDiagnostics).Reason);
    }

    [Fact]
    public void ContentOmissionsCarryTheirReason()
    {
        var book = new Book
        {
            Chapters = { Chapter("<p>A&#x2720;<img src='../images/a.gif' alt=''/></p>") },
            ExtraManifest = "<item id=\"gif\" href=\"images/a.gif\" media-type=\"image/gif\"/>"
        };
        book.Resources.Add(("images/a.gif", Encoding.ASCII.GetBytes("GIF89a")));

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(new[] { EpubOmissionReason.MissingGlyph, EpubOmissionReason.UnsupportedImageFormat },
            layout.ContentDiagnostics.Select(diagnostic => diagnostic.Reason).OrderBy(reason => reason));
    }
}
