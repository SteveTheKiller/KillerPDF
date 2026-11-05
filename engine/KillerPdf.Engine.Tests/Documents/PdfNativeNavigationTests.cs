using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Tests.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNativeNavigationTests
{
    [Fact]
    public void CapturedDjvuTargetResolvesToItsNativePageStart()
    {
        byte[] source = Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        var snapshot = PdfReadSnapshot.OpenDjvu(source);
        string target = snapshot.ReadNavigation()[0].Target;

        var destination = snapshot.ResolveNavigationTarget(target);

        Assert.Equal(new PdfReadNavigationDestination(0, 0, 0), destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedEpubTargetResolvesToItsNativePageAndLogicalPosition(bool ncx)
    {
        byte[] source = EpubNavigationDestinationTests.Book(ncx: ncx);
        var snapshot = PdfReadSnapshot.OpenEpub(source, Options());
        Array.Fill(source, (byte)0);
        string target = snapshot.ReadNavigation()[0].Target;

        var destination = snapshot.ResolveNavigationTarget(target);

        Assert.Equal(new PdfReadNavigationDestination(2, 2, 2), destination);
    }

    [Fact]
    public void DjvuOutlineUsesTheSameOwnedNativeNavigationTree()
    {
        byte[] source = Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        var snapshot = PdfReadSnapshot.OpenDjvu(source);
        Array.Fill(source, (byte)0);

        var root = Assert.Single(snapshot.ReadNavigation());

        Assert.Equal("R\u00e9sum\u00e9", root.Label);
        Assert.Equal("#1", root.Target);
        var child = Assert.Single(root.Children);
        Assert.Equal("\u05e4\u05e8\u05e7", child.Label);
        Assert.Equal("#1", child.Target);
        Assert.Throws<NotSupportedException>(() => ((IList<PdfReadNavigationEntry>)root.Children).Clear());
    }

    [Theory]
    [InlineData("<li><span>Unlinked without children</span></li>")]
    [InlineData("<li>")]
    public void MalformedOutlineDoesNotPreventReadingNativePages(string entries)
    {
        byte[] source = EpubNavigationDestinationTests.Book(entries);

        var snapshot = PdfReadSnapshot.OpenEpub(source, Options());

        Assert.Equal(3, snapshot.PageCount);
        var before = snapshot.RenderPage(0);
        Assert.Equal(16, before.Width);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, before.Pixels.Slice((5 * 16 + 7) * 4, 4).ToArray());
        Assert.Throws<InvalidDataException>(() => snapshot.ReadNavigation());
        Assert.Equal(before.Pixels.ToArray(), snapshot.RenderPage(0).Pixels.ToArray());
    }

    [Fact]
    public void CancellationPrecedesCapturedNavigationErrorsAndTargetArguments()
    {
        var snapshot = PdfReadSnapshot.OpenEpub(EpubNavigationDestinationTests.Book("<li>"), Options());
        var canceled = new CancellationToken(true);

        Assert.Throws<OperationCanceledException>(() => snapshot.ReadNavigation(canceled));
        Assert.Throws<OperationCanceledException>(() => snapshot.ResolveNavigationTarget(null!, -1, canceled));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ResolveNavigationTarget("EPUB/later.xhtml", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ResolveNavigationTarget("EPUB/later.xhtml", snapshot.PageCount));
        Assert.Throws<ArgumentException>(() => snapshot.ResolveNavigationTarget("EPUB/later.xhtml#anchor"));
        Assert.Equal(new PdfReadNavigationDestination(2, 2, 2), snapshot.ResolveNavigationTarget("EPUB/later.xhtml"));
    }

    [Fact]
    public void DjvuWithoutOutlineStillProvidesPageMetadataAndLocalTargets()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Convert.FromHexString(
            "41542654464F524D00000016444A5655494E464F0000000A000200011A002C011601"));

        Assert.Empty(snapshot.ReadNavigation());
        Assert.Equal(1, snapshot.PageCount);
        Assert.Equal(new PdfReadNavigationDestination(0, 0, 0), snapshot.ResolveNavigationTarget("#1"));
        Assert.Equal(new PdfReadNavigationDestination(0, 0, 0), snapshot.ResolveNavigationTarget("#+0", 0));
        Assert.Throws<ArgumentException>(() => snapshot.ResolveNavigationTarget("#+0"));
        Assert.Throws<NotSupportedException>(() => snapshot.ResolveNavigationTarget("https://example.invalid"));
    }

    [Fact]
    public void EpubOutlineSurvivesSourceMutationAndReturnsOwnedReadOnlyTrees()
    {
        byte[] source = EpubNavigationDestinationTests.Book(
            "<li><span>Group</span><ol><li><a href='../later.xhtml'>Later</a></li></ol></li>");
        var snapshot = PdfReadSnapshot.OpenEpub(source, Options());
        Array.Fill(source, (byte)0);

        var entries = snapshot.ReadNavigation();

        var group = Assert.Single(entries);
        Assert.Equal("Group", group.Label);
        Assert.Equal("", group.Target);
        var child = Assert.Single(group.Children);
        Assert.Equal("Later", child.Label);
        Assert.Equal("EPUB/later.xhtml", child.Target);
        Assert.Empty(child.Children);
        Assert.Throws<NotSupportedException>(() => ((IList<PdfReadNavigationEntry>)entries).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PdfReadNavigationEntry>)group.Children).Clear());
    }

    private static EpubLayoutOptions Options() => new(16, 23,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2
    };
}
