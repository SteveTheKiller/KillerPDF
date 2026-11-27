using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The Compare PDFs list of open documents fills its icon column: each document row carries KillerPDF's own document
/// icon and Choose another PDF carries a glyph, where both used to leave the column empty.
/// </summary>
public sealed class CompareMenuIconTests
{
    [Fact]
    public void EveryCompareChoiceHasAnIcon()
    {
        string source = ShellSource.Read("Shell", "PdfComparison.cs");
        Assert.Contains("item.Icon = Services.ShellIcons.MenuIcon(Services.ShellIcons.DocumentIcon(tab.OriginalPath), this);", source);
        Assert.Contains("MakeMenuItem(Loc(\"Str_Compare_Browse\"), (_, _) => BrowseForComparison(source), glyph: GlyphText(0xE8E5))", source);
    }

    [Fact]
    public void TheDocumentIconComesFromTheEmbeddedFileIcon()
    {
        // The icon is the one the installer registers, embedded in the app, not the default PDF app's.
        Assert.Contains("<EmbeddedResource Include=\"Resources\\pdf-file.ico\" />", ShellSource.Read("KillerPDF.csproj"));
        string icons = ShellSource.Read("Services", "ShellIcons.cs");
        Assert.Contains("n => n.Contains(\"pdf-file\", StringComparison.OrdinalIgnoreCase)", icons);
        Assert.Contains("new IconBitmapDecoder(stream", icons);
    }
}
