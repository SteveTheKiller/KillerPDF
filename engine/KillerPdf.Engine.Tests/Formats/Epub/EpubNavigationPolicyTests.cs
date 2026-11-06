using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE html>")]
    public void NavigationDiagnosesEmbeddedScripts(string declaration)
    {
        var book = NavigationBook(declaration + Navigation.Replace("<body>",
            "<body><script>throw 'must never execute';</script>"));

        Assert.Throws<NotSupportedException>(() => book.ReadNavigation());
    }

    [Theory]
    [InlineData("onclick")]
    [InlineData("ONLOAD")]
    public void NavigationDiagnosesScriptEventAttributes(string attribute)
    {
        var book = NavigationBook(Navigation.Replace("<body>",
            "<body " + attribute + "='execute()'>"));

        Assert.Throws<NotSupportedException>(() => book.ReadNavigation());
    }

    [Fact]
    public void NavigationDiagnosesDeclaredScriptedResource()
    {
        string package = Package.Replace("</manifest>",
            "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav scripted'/></manifest>");
        var book = EpubDocument.Open(Book(package, ("EPUB/nav.xhtml", Navigation)));

        Assert.Throws<NotSupportedException>(() => book.ReadNavigation());
    }
}
