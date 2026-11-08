using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    private const string Navigation = """
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
          <nav epub:type="landmarks"><ol><li><a href="two.xhtml">Ignored landmarks</a></li></ol></nav>
          <nav epub:type="toc"><h1>Contents</h1><ol>
            <li><a href="./one.xhtml#start"> First <em>chapter</em> </a><ol><li><a href="two.xhtml#detail">Detail</a></li></ol></li>
            <li><a href="two.xhtml">Second chapter</a></li>
          </ol></nav>
        </body></html>
        """;

    [Fact]
    public void ReadsEpub3TableOfContentsInHierarchyAndDocumentOrder()
    {
        var entries = NavigationBook(Navigation).ReadNavigation();
        Assert.Equal(new[] { "First chapter", "Second chapter" }, entries.Select(x => x.Label));
        Assert.Equal("EPUB/one.xhtml#start", entries[0].Target);
        var detail = Assert.Single(entries[0].Children);
        Assert.Equal("Detail", detail.Label);
        Assert.Equal("EPUB/two.xhtml#detail", detail.Target);
        Assert.Empty(entries[1].Children);
    }

    [Fact]
    public void ReadsEpub2NcxSelectedBySpineTocInDocumentOrder()
    {
        const string ncx = """
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
              <navPoint id="a" playOrder="9"><navLabel><text> First chapter </text></navLabel><content src="one.xhtml#start"/>
                <navPoint id="b" playOrder="2"><navLabel><text>Detail</text></navLabel><content src="two.xhtml#detail"/></navPoint>
              </navPoint>
              <navPoint id="c" playOrder="1"><navLabel><text>Second chapter</text></navLabel><content src="two.xhtml"/></navPoint>
            </navMap></ncx>
            """;
        var entries = NcxBook(ncx).ReadNavigation();
        Assert.Equal(new[] { "First chapter", "Second chapter" }, entries.Select(x => x.Label));
        Assert.Equal("EPUB/one.xhtml#start", entries[0].Target);
        Assert.Equal("EPUB/two.xhtml#detail", Assert.Single(entries[0].Children).Target);
    }

    [Fact]
    public void PreservesUnlinkedGroupingHeadingsWithChildren()
    {
        var navigation = Navigation.Replace("<a href=\"./one.xhtml#start\"> First <em>chapter</em> </a>", "<span>Part one</span>");
        var entry = NavigationBook(navigation).ReadNavigation()[0];
        Assert.Equal("Part one", entry.Label);
        Assert.Equal("", entry.Target);
        Assert.Equal("Detail", Assert.Single(entry.Children).Label);
    }

    [Fact]
    public void ReadsImageAlternativeTextAsNavigationLabel()
    {
        var navigation = Navigation.Replace("First <em>chapter</em>", "<img src=\"unused.png\" alt=\"First chapter\"/>");
        Assert.Equal("First chapter", NavigationBook(navigation).ReadNavigation()[0].Label);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsNavigationEntriesWithoutUsableLabels(bool ncx)
    {
        var book = ncx ? NcxBook(Ncx("<navPoint><navLabel><text>   </text></navLabel><content src='one.xhtml'/></navPoint>"))
            : NavigationBook(Navigation.Replace("Detail", "   "));
        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMoreThan1024NavigationNodes(bool ncx)
    {
        var node = ncx ? "<navPoint><navLabel><text>Chapter</text></navLabel><content src='one.xhtml'/></navPoint>"
            : "<li><a href='one.xhtml'>Chapter</a></li>";
        // Raised to 16,384 entries for the Complete Works of Shakespeare (KP-B slice 10); the method name keeps its history.
        var content = string.Concat(Enumerable.Repeat(node, 16_385));
        var book = ncx ? NcxBook(Ncx(content)) : NavigationBook(NavList(content));
        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
    }

    private static string NavList(string entries) => "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol>" + entries + "</ol></nav></body></html>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsNavigationDeeperThan32Levels(bool ncx)
    {
        Assert.Throws<InvalidDataException>(() => NestedNavigation(ncx, 33).ReadNavigation());
    }

    [Fact]
    public void HonorsNavigationCancellationBeforeReturningAnyEntries()
    {
        var book = NavigationBook(Navigation);
        Assert.Throws<OperationCanceledException>(() => book.ReadNavigation(new CancellationToken(true)));
    }

    [Theory]
    [InlineData("wrong-root")]
    [InlineData("no-toc")]
    [InlineData("empty-list")]
    [InlineData("span-leaf")]
    [InlineData("duplicate-toc")]
    [InlineData("double-label")]
    public void DiagnosesUnusableDeclaredXhtmlNavigation(string scenario)
    {
        var navigation = scenario switch
        {
            "wrong-root" => "<wrong/>",
            "no-toc" => Navigation.Replace("epub:type=\"toc\"", "epub:type=\"page-list\""),
            "empty-list" => NavList(""),
            "span-leaf" => NavList("<li><span>Group</span></li>"),
            "duplicate-toc" => Navigation.Replace("epub:type=\"landmarks\"", "epub:type=\"toc\""),
            _ => NavList("<li><a href='one.xhtml'>One</a><span>Two</span></li>")
        };
        Assert.Throws<InvalidDataException>(() => NavigationBook(navigation).ReadNavigation());
    }

    [Theory]
    [InlineData("wrong-root")]
    [InlineData("empty-map")]
    [InlineData("missing-content")]
    [InlineData("missing-reference")]
    public void DiagnosesUnusableDeclaredNcx(string scenario)
    {
        var navigation = scenario switch
        {
            "wrong-root" => "<wrong/>",
            "empty-map" => Ncx(""),
            "missing-content" => Ncx("<navPoint><navLabel><text>Chapter</text></navLabel></navPoint>"),
            _ => Ncx("<navPoint><navLabel><text>Chapter</text></navLabel><content src='one.xhtml'/></navPoint>")
        };
        var book = NcxBook(navigation, scenario == "missing-reference" ? "missing" : "toc");
        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsAmbiguousOrWrongTypeNavigationDeclaration(bool duplicate)
    {
        var item = "<item id='nav' href='nav.xhtml' media-type='" + (duplicate ? "application/xhtml+xml" : "text/plain") + "' properties='nav'/>";
        if (duplicate) item += "<item id='nav2' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>";
        var book = EpubDocument.Open(Book(Package.Replace("</manifest>", item + "</manifest>"), ("EPUB/nav.xhtml", Navigation)));
        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEncodedControlCharacterFragmentLeavesItsEntryUnlinked(bool ncx)
    {
        var book = TargetBook(ncx, "one.xhtml#%0Aunsafe");
        Assert.Equal("", Assert.Single(book.ReadNavigation()).Target);
    }

    [Theory]
    [InlineData(false, "https://example.invalid/chapter.xhtml")]
    [InlineData(true, "https://example.invalid/chapter.xhtml")]
    [InlineData(false, "../../outside.xhtml")]
    [InlineData(true, "../../outside.xhtml")]
    [InlineData(false, "missing.xhtml#target")]
    [InlineData(true, "missing.xhtml#target")]
    [InlineData(false, "file:///C:/chapter.xhtml")]
    [InlineData(true, "//example.invalid/chapter.xhtml")]
    public void NonlocalOrMissingNavigationTargetsLeaveTheirEntryUnlinked(bool ncx, string target)
    {
        Assert.Equal("", Assert.Single(TargetBook(ncx, target).ReadNavigation()).Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsNavigationAtCountAndDepthLimits(bool ncx)
    {
        var node = ncx ? "<navPoint><navLabel><text>Chapter</text></navLabel><content src='one.xhtml'/></navPoint>"
            : "<li><a href='one.xhtml'>Chapter</a></li>";
        var content = string.Concat(Enumerable.Repeat(node, 1024));
        var book = ncx ? NcxBook(Ncx(content)) : NavigationBook(NavList(content));
        Assert.Equal(1024, book.ReadNavigation().Count);
        var entry = Assert.Single(NestedNavigation(ncx, 32).ReadNavigation());
        for (int level = 1; level < 32; level++) entry = Assert.Single(entry.Children);
        Assert.Equal("Leaf", entry.Label);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void ReturnsEmptyNavigationWhenNothingIsDeclared(string version)
    {
        var book = EpubDocument.Open(Book(Package.Replace("version=\"3.0\"", "version=\"" + version + "\"")));
        Assert.Empty(book.ReadNavigation());
        Assert.Throws<OperationCanceledException>(() => book.ReadNavigation(new CancellationToken(true)));
    }

    [Fact]
    public void NavigationHierarchyCannotBeMutatedByCallers()
    {
        var entries = NavigationBook(Navigation).ReadNavigation();
        Assert.Throws<NotSupportedException>(() => ((IList<EpubNavigationEntry>)entries).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<EpubNavigationEntry>)entries[0].Children).Clear());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDtdInNavigationDocuments(bool ncx)
    {
        const string dtd = "<!DOCTYPE html SYSTEM 'https://example.invalid/external.dtd'>";
        var book = ncx ? NcxBook(dtd + Ncx("<navPoint><navLabel><text>Chapter</text></navLabel><content src='one.xhtml'/></navPoint>"))
            : NavigationBook(dtd + Navigation);
        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
    }

    [Fact]
    public void ResolvesFragmentOnlyAndEncodedInternalPaths()
    {
        Assert.Equal("EPUB/nav.xhtml#section", Assert.Single(TargetBook(false, "#section").ReadNavigation()).Target);
        Assert.Equal("EPUB/one.xhtml#section", Assert.Single(TargetBook(true, "./one%2Exhtml#section").ReadNavigation()).Target);
    }

    private static EpubDocument TargetBook(bool ncx, string target) => ncx
        ? NcxBook(Ncx("<navPoint><navLabel><text>Chapter</text></navLabel><content src='" + target + "'/></navPoint>"))
        : NavigationBook(NavList("<li><a href='" + target + "'>Chapter</a></li>"));

    private static EpubDocument NestedNavigation(bool ncx, int levels)
    {
        var content = ncx ? "<navPoint><navLabel><text>Leaf</text></navLabel><content src='one.xhtml'/></navPoint>"
            : "<li><a href='one.xhtml'>Leaf</a></li>";
        for (int level = 1; level < levels; level++)
            content = ncx ? "<navPoint><navLabel><text>Parent</text></navLabel><content src='one.xhtml'/>" + content + "</navPoint>"
                : "<li><a href='one.xhtml'>Parent</a><ol>" + content + "</ol></li>";
        return ncx ? NcxBook(Ncx(content)) : NavigationBook(NavList(content));
    }

    private static string Ncx(string points) => "<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/'><navMap>" + points + "</navMap></ncx>";

    private static EpubDocument NcxBook(string navigation, string toc = "toc")
    {
        var package = Package.Replace("version=\"3.0\"", "version=\"2.0\"")
            .Replace("</manifest>", "<item id=\"toc\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/><item id=\"decoy\" href=\"decoy.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest>")
            .Replace("<spine ", "<spine toc=\"" + toc + "\" ");
        return EpubDocument.Open(Book(package, ("EPUB/toc.ncx", navigation), ("EPUB/decoy.ncx", "<wrong/>")));
    }

    private static EpubDocument NavigationBook(string navigation)
    {
        var package = Package.Replace("</manifest>", "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"cover-image nav\"/></manifest>");
        return EpubDocument.Open(Book(package, ("EPUB/nav.xhtml", navigation)));
    }
}
