using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuNavigationDestinationTests
{
    [Fact]
    public void ResolvesKnownComponentIdsBeforeDiagnosingViewerCommands()
    {
        // Independently encoded and inspected with DjVu.js 0.5.4, commit eca3e69.
        var input = Convert.FromHexString(
            "41542654464F524D0000003E444A564D4449524D000000148100010000002CFFFFF6BF871FCC880AD2B3B851" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        Assert.Equal("98C34411585FE510A3B99522B8A3B3F6BE65F83C6A580D2C364A7599710470EB",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        Assert.Equal("q?x", Assert.Single(document.Pages).ComponentId);
        Assert.Equal(0, document.ResolveNavigationTarget("#q?x"));
        Assert.Equal(0, document.ResolveNavigationTarget("#q%3Fx"));
        Assert.Throws<NotSupportedException>(() => document.ResolveNavigationTarget("#1?djvuopts&zoom=200"));
        Assert.Throws<NotSupportedException>(() => document.ResolveNavigationTarget("#1%3Fdjvuopts&zoom=200"));
    }

    [Fact]
    public void PrefersExactComponentIdsAndPreservesUnicodePlusAndLiteralPercent()
    {
        // Independently encoded and inspected with DjVu.js 0.5.4, commit eca3e69.
        // Logical IDs are 2, r\u00e9sum\u00e9+, and %31, in that order.
        var input = Convert.FromHexString(
            "41542654464F524D00000092444A564D4449524D0000002B810003000000440000006200000080" +
            "FFFFE2BF387BCC52B24257673EA3BCAF22BB1D9044EA352D761AA97F00" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601" +
            "464F524D00000016444A5655494E464F0000000A000300011A002C011601" +
            "464F524D00000016444A5655494E464F0000000A000400011A002C011601");
        Assert.Equal("B4DADC941A50F136D7F3B2DDD5F1C60B6583070E497971BCC141C82D1B72457B",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        Assert.Equal(new[] { "2", "r\u00e9sum\u00e9+", "%31" }, document.Pages.Select(page => page.ComponentId));
        Assert.Equal(0, document.ResolveNavigationTarget("#2"));
        Assert.Equal(1, document.ResolveNavigationTarget("#r\u00e9sum\u00e9+"));
        Assert.Equal(1, document.ResolveNavigationTarget("#r%C3%A9sum%C3%A9%2B"));
        Assert.Equal(2, document.ResolveNavigationTarget("#%2531"));
        Assert.Equal(0, document.ResolveNavigationTarget("#%31"));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#re\u0301sume\u0301+"));
        var invalidUnicode = Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#" + (char)0xd800));
        Assert.Contains("invalid UTF-8", invalidUnicode.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#%")]
    [InlineData("#%2")]
    [InlineData("#%GG")]
    [InlineData("#%C0%AF")]
    [InlineData("#%FF")]
    [InlineData("#%E2%82")]
    [InlineData("#%ED%A0%80")]
    [InlineData("#%00")]
    [InlineData("#1%00")]
    [InlineData("#%7f")]
    [InlineData("#%C2%85")]
    [InlineData("#p\n2")]
    [InlineData("#p\t2")]
    public void DiagnosesMalformedEncodingAndControlCharacters(string target)
    {
        var error = Assert.Throws<InvalidDataException>(() => OpenTwoPages().ResolveNavigationTarget(target));
        Assert.DoesNotContain("does not identify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesPercentEscapesExactlyOnceWithoutFormDecoding()
    {
        var document = OpenTwoPages();
        Assert.Equal(1, document.ResolveNavigationTarget("#%70%32"));
        Assert.Equal(0, document.ResolveNavigationTarget("#%31"));
        Assert.Equal(1, document.ResolveNavigationTarget("#%2B1", 0));
        Assert.Equal(0, document.ResolveNavigationTarget("#%2d1", 1));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#%2531"));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#p+2"));
        Assert.Throws<NotSupportedException>(() => document.ResolveNavigationTarget("#1%3Fdjvuopts&zoom=200"));
    }

    [Theory]
    [InlineData("x1")]
    [InlineData("https://example.invalid/book.djvu#1")]
    [InlineData("file:///book.djvu#1")]
    [InlineData("other.djvu#1")]
    [InlineData("?djvuopts&page=1")]
    [InlineData("#1?djvuopts&zoom=200")]
    public void RejectsExternalResourcesAndViewerCommandsWithoutFetching(string target)
    {
        var error = Assert.Throws<NotSupportedException>(() => OpenTwoPages().ResolveNavigationTarget(target));
        Assert.Contains("local", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#0")]
    [InlineData("#3")]
    [InlineData("#2147483648")]
    [InlineData("#P2")]
    [InlineData("# p2")]
    [InlineData("#2 ")]
    [InlineData("#+")]
    [InlineData("#--1")]
    [InlineData("#1.0")]
    [InlineData("#\u0661")]
    public void RejectsUnknownMalformedAndUnavailablePages(string target)
    {
        Assert.Throws<InvalidDataException>(() => OpenTwoPages().ResolveNavigationTarget(target, 0));
    }

    [Fact]
    public void ValidatesCancellationThenArgumentsBeforeResolving()
    {
        var document = OpenTwoPages();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.ResolveNavigationTarget(null!, -1, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => document.ResolveNavigationTarget(null!));
        Assert.Throws<ArgumentException>(() => document.ResolveNavigationTarget("#" + new string('1', 4096)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ResolveNavigationTarget("#1", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ResolveNavigationTarget("#1", 2));
        Assert.Equal(0, document.ResolveNavigationTarget("#" + new string('0', 4094) + "1"));
    }

    [Fact]
    public void ResolvesSignedDisplacementsFromTheCurrentPage()
    {
        var document = OpenTwoPages();
        Assert.Equal(1, document.ResolveNavigationTarget("#+1", 0));
        Assert.Equal(0, document.ResolveNavigationTarget("#-1", 1));
        Assert.Equal(1, document.ResolveNavigationTarget("#+0", 1));
        Assert.Equal(0, document.ResolveNavigationTarget("#-0", 0));
        Assert.Throws<ArgumentException>(() => document.ResolveNavigationTarget("#+1"));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#+2", 0));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#-2", 1));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#+2147483647", 1));
        Assert.Throws<InvalidDataException>(() => document.ResolveNavigationTarget("#-2147483648", 0));
    }

    [Fact]
    public void ResolvesDirectoryComponentIdsAndOneBasedOrdinalsInDirectoryOrder()
    {
        var document = OpenTwoPages();
        Assert.Equal(1, document.ResolveNavigationTarget("#p2"));
        Assert.Equal(0, document.ResolveNavigationTarget("#p3"));
        Assert.Equal(1, document.ResolveNavigationTarget("#2"));
        Assert.Equal(0, document.ResolveNavigationTarget("#01"));
    }

    [Fact]
    public void ResolvesTheTargetReadFromIndependentlyEncodedNavigation()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        Assert.Equal("4C312B62F2F1D62D2228516A11045DD6E8D313472D19CC67E6A1F4B0184BA332",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        var entry = Assert.Single(document.ReadNavigation());
        Assert.Equal(0, document.ResolveNavigationTarget(entry.Target));
    }

    private static DjvuDocument OpenTwoPages()
    {
        // Independently encoded directory metadata: sizes 30,30, flags 1,1, IDs p3,p2.
        var input = Convert.FromHexString(
            "41542654464F524D00000062444A564D4449524D0000001A8100020000005000000032" +
            "FFFFF0BF347BCC70D5BE417048DC17" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601" +
            "464F524D00000016444A5655494E464F0000000A000300011A002C011601");
        Assert.Equal("4EC7E50CFB5DE8A8FC40259333DFC420298672873E1D91DD9FFABAEB4908AA19",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        return DjvuDocument.Open(input);
    }
}
