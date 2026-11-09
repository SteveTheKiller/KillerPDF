using System.Xml;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A chapter keeps one record per distinct computed style: elements whose computed styles are equal share it, however
// their declarations were written, and elements whose styles differ keep their own.
public sealed class EpubStyleInterningTests
{
    [Fact]
    public void EqualComputedStylesFromSeparateStyleAttributesShareOneRecord()
    {
        var styles = ParagraphStyles("<p style='font-family:serif'>A</p><p style='font-family: serif'>A</p>");

        Assert.Same(styles[0], styles[1]);
    }

    [Fact]
    public void EqualComputedStylesFromAClassRuleShareOneRecord()
    {
        var styles = ParagraphStyles("<p class='x'>A</p><p class='x'>A</p><p>A</p>",
            ".x { font-family: \"Georgia\", serif; letter-spacing: 1px }");

        Assert.Same(styles[0], styles[1]);
        Assert.NotSame(styles[0], styles[2]);
    }

    [Fact]
    public void DifferentComputedStylesKeepTheirOwnRecords()
    {
        var styles = ParagraphStyles("<p style='font-family:serif'>A</p><p style='font-family:sans-serif'>A</p>"
            + "<p style='color:#c00000'>A</p><p>A</p>");

        Assert.Equal(4, styles.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.NotEqual(styles[0], styles[1]);
    }

    // The computed style of each paragraph, in order, as the chapter reader builds them.
    private static List<EpubNativeContent.Style> ParagraphStyles(string body, string css = "")
    {
        var content = new EpubNativeContent(16, 600);
        if (css.Length > 0) content.AddStylesheet(css, CancellationToken.None);
        var styles = new List<EpubNativeContent.Style>();
        using var xml = XmlReader.Create(new StringReader(
            "<html xmlns='http://www.w3.org/1999/xhtml'><body>" + body + "</body></html>"));
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                bool empty = xml.IsEmptyElement;
                content.Enter(xml);
                if (empty) content.Exit();
            }
            else if (xml.NodeType == XmlNodeType.EndElement) content.Exit();
            else if (xml.NodeType == XmlNodeType.Text)
            {
                var node = content.TextNode(xml.Value);
                if (node.Owner.Name == "p") styles.Add(node.Owner.Style);
            }
        }
        return styles;
    }
}
