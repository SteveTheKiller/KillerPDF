using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A tool can rewrite a trailer and drop /Encrypt while the rest of the file stays sound, so the strict
// read saw a healthy unprotected PDF whose pages were ciphertext. Failing that read sends the file to
// recovery, where the app already refuses it on every route.
public sealed class PdfLostEncryptionTests
{
    [Theory]
    [InlineData("/F#69lter /Standard")]
    [InlineData("/Filter /Sta#6Edard")]
    [InlineData("/F#69lter % a security handler can follow a long comment\n /Sta#6Edard")]
    public void EscapedSecurityNamesStillProtectCiphertextAfterTheTrailerEntryIsLost(string marker)
    {
        string original = Encoding.Latin1.GetString(Encrypted());
        string changed = original.Replace("/Filter /Standard", marker, StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        byte[] protectedSource = Reindex(changed);
        Assert.True(PdfDocument.Open(protectedSource, "secret").IsDecrypted);
        byte[] lost = WithoutEncryptEntry(protectedSource);
        Assert.Throws<PdfLostEncryptionException>(() => PdfDocumentWriter.Write(PdfDocument.Open(lost)));
    }

    private static byte[] Reindex(string source)
    {
        int xref = source.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        int trailer = source.IndexOf("trailer", xref, StringComparison.Ordinal);
        int count = int.Parse(System.Text.RegularExpressions.Regex.Match(source[trailer..], @"/Size\s+(\d+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var table = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int index = 1; index < count; index++)
        {
            int offset = source.IndexOf($"\n{index} 0 obj", StringComparison.Ordinal) + 1;
            Assert.True(offset > 0);
            table.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        string tail = source[trailer..];
        int start = tail.IndexOf("startxref", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(source[..xref] + table + tail[..start] + $"startxref\n{xref}\n%%EOF\n");
    }

    // The ciphertext is overwritten with fixed high bytes of the same length, because AES draws a random
    // IV and a run that began with a percent sign once read as nothing at all.
    [Fact]
    public void AnEncryptedFileWhoseTrailerLostItsEntryFailsTheStrictRead()
    {
        byte[] lost = WithoutEncryptEntry(WithPageContent(Encrypted(), length => [.. Enumerable.Repeat((byte)0xC8, length)]));

        var error = Assert.Throws<PdfLostEncryptionException>(() => PdfDocument.Open(lost));
        Assert.Contains("/Encrypt", error.Message, StringComparison.Ordinal);
        Assert.False(PdfDocument.OpenWithCompatibilityRecovery(lost).IsEncrypted);
    }

    // R4-011: a comment runs to the end of the line, so ciphertext that begins with a percent sign and
    // has no line end reads as no instructions at all. Bytes that read as nothing are still not a page.
    [Fact]
    public void CiphertextThatBeginsWithAPercentSignStillFailsTheStrictRead()
    {
        byte[] lost = WithoutEncryptEntry(WithPageContent(Encrypted(),
            length => [(byte)'%', .. Enumerable.Repeat((byte)0xC8, length - 1)]));

        Assert.Throws<PdfLostEncryptionException>(() => PdfDocument.Open(lost));
    }

    // R4-011: a token shaped like an operator is not one, so a short run of letters is not a page either.
    [Theory]
    [InlineData("Tj", true)]
    [InlineData("BDC", true)]
    [InlineData("b*", true)]
    [InlineData("'", true)]
    [InlineData("ab", false)]
    [InlineData("Q7", false)]
    [InlineData("xyz", false)]
    public void OnlyRealContentOperatorsCount(string name, bool known) =>
        Assert.Equal(known, KillerPdf.Engine.Parsing.PdfContentOperators.IsKnown(name));

    // A page that holds only whitespace has nothing to lose, with or without a leftover dictionary.
    [Fact]
    public void AWhitespacePageWithALeftoverDictionaryStillOpens() =>
        Assert.False(PdfDocument.Open(WithLeftoverDictionary(new PdfDocumentBuilder()
            .AddPage(200, 200, " \n\t\r\n "u8.ToArray()).Build())).IsEncrypted);

    // R5-006: a plain page written as nothing but a comment reads as no instructions too, and is not
    // ciphertext. Ciphertext behind a percent sign carries a byte of 0x80 or above; an authored
    // comment does not.
    [Theory]
    [InlineData("% blank page\n")]
    [InlineData("%empty")]
    [InlineData("  % page intentionally left blank\r\n% second line\n")]
    public void ACommentOnlyPageWithALeftoverDictionaryStillOpens(string content)
    {
        byte[] plain = WithLeftoverDictionary(new PdfDocumentBuilder()
            .AddPage(200, 200, Encoding.Latin1.GetBytes(content)).Build());

        PdfDocument? document = null;
        Exception? failure = Record.Exception(() => document = PdfDocument.Open(plain));

        Assert.Null(failure);
        Assert.False(document!.IsEncrypted);
        Assert.Single(PdfPageInformation.Read(document));
    }

    // R6-006: a comment in a language other than English carries bytes of 0x80 or above, in UTF-8 or
    // Latin-1, and is still an authored comment. Only bytes that cannot be text convict the page.
    [Theory]
    [InlineData("% Seite für Rechnungen\n", "utf-8")]
    [InlineData("% Seite für Rechnungen\n", "latin1")]
    [InlineData("% 請求書のページです\n% 空白ページ\n", "utf-8")]
    [InlineData("% Café crème brûlée, à la façon de la maïson\n", "latin1")]
    public void ANonEnglishCommentOnlyPageWithALeftoverDictionaryStillOpens(string content, string encoding)
    {
        byte[] bytes = encoding == "latin1" ? Encoding.Latin1.GetBytes(content) : Encoding.UTF8.GetBytes(content);
        Assert.Contains(bytes, value => value >= 0x80);
        byte[] plain = WithLeftoverDictionary(new PdfDocumentBuilder().AddPage(200, 200, bytes).Build());

        PdfDocument? document = null;
        Exception? failure = Record.Exception(() => document = PdfDocument.Open(plain));

        Assert.Null(failure);
        Assert.False(document!.IsEncrypted);
        Assert.Single(PdfPageInformation.Read(document));
    }

    // Ciphertext behind a percent sign almost always carries a control byte, and when it does not, it
    // is not UTF-8 and a quarter or more of it is high bytes.
    [Theory]
    [InlineData(new byte[] { 0x25, 0x41, 0x01, 0x42, 0x43 })]
    [InlineData(new byte[] { 0x25, 0x20, 0x7F, 0x20, 0x20 })]
    [InlineData(new byte[] { 0x25, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8, 0xC8 })]
    public void BytesThatCannotBeTextBehindAPercentSignStillFailTheStrictRead(byte[] content)
    {
        byte[] lost = WithLeftoverDictionary(new PdfDocumentBuilder().AddPage(200, 200, content).Build());

        Assert.Throws<PdfLostEncryptionException>(() => PdfDocument.Open(lost));
    }

    [Fact]
    public void AnEncryptedFileWithItsEntryStillOpens() =>
        Assert.True(PdfDocument.Open(Encrypted()).IsEncrypted);

    // Decrypting tools can leave the old dictionary behind in a file that is perfectly readable.
    [Fact]
    public void ALeftoverEncryptionDictionaryOverPlainPagesStillOpens() =>
        Assert.False(PdfDocument.Open(WithLeftoverDictionary(Plain())).IsEncrypted);

    // Nothing to read proves nothing either way, and a blank page has nothing to lose.
    [Fact]
    public void ABlankPageWithALeftoverDictionaryStillOpens() =>
        Assert.False(PdfDocument.Open(WithLeftoverDictionary(new PdfDocumentBuilder().AddBlankPage().Build())).IsEncrypted);

    private static byte[] WithLeftoverDictionary(byte[] source)
    {
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(source));
        update.AddObject(new PdfDictionary([new(new PdfName("Filter"u8), new PdfName("Standard"u8))]));
        return update.Build();
    }

    private static byte[] Plain() => new PdfDocumentBuilder()
        .AddPage(200, 200, new PdfContentStreamBuilder().DrawImage(PdfImage.FromBitonal(2, 1, new byte[] { 0, 255 }), 0, 0, 100, 100))
        .Build();

    private static byte[] Encrypted() => PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(Plain()),
        new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });

    // The page's content stream overwritten in place with bytes of the same length, so every offset
    // and the stream's /Length stay as they were.
    private static byte[] WithPageContent(byte[] source, Func<int, byte[]> content)
    {
        string text = Encoding.Latin1.GetString(source);
        var contents = System.Text.RegularExpressions.Regex.Match(text, @"/Contents\s+(\d+)\s+0\s+R");
        Assert.True(contents.Success, "The fixture's page has no content stream reference.");
        int start = text.IndexOf($"\n{contents.Groups[1].Value} 0 obj", StringComparison.Ordinal);
        Assert.True(start > 0, "The fixture's content stream was not found.");
        int length = int.Parse(System.Text.RegularExpressions.Regex.Match(text[start..], @"/Length\s+(\d+)(?!\s+\d+\s+R)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        int data = text.IndexOf("stream", start, StringComparison.Ordinal) + "stream".Length;
        data += text[data] == '\r' ? 2 : 1;
        Assert.InRange(text.IndexOf("endstream", data, StringComparison.Ordinal) - (data + length), 0, 2);
        byte[] replaced = content(length);
        Assert.Equal(length, replaced.Length);
        byte[] result = (byte[])source.Clone();
        replaced.CopyTo(result, data);
        return result;
    }

    // Spaces keep every offset, so the file stays structurally sound without the entry.
    private static byte[] WithoutEncryptEntry(byte[] source)
    {
        string text = Encoding.Latin1.GetString(source);
        int trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        int entry = text.IndexOf("/Encrypt", trailer, StringComparison.Ordinal);
        Assert.True(entry > 0, "The fixture has no trailer /Encrypt entry.");
        int end = text.IndexOf(" R", entry, StringComparison.Ordinal) + 2;
        return Encoding.Latin1.GetBytes(text[..entry] + new string(' ', end - entry) + text[end..]);
    }
}
