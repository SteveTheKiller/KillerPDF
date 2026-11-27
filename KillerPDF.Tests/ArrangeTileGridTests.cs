using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The markup card's Arrange tile opens a tile grid (KillerUI CardBuilder FlyoutTiles), not menu rows (#504): six rows
/// of three, every arrange action exactly once, each with its own glyph written as a code point.
/// </summary>
public sealed class ArrangeTileGridTests
{
    [Fact]
    public void ArrangeOpensSixRowsOfThreeTilesCoveringEveryAction()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Shell", "AnnotationCard.cs"));
        Assert.Contains("TileGridTile(", source);
        Assert.DoesNotContain("ArrangeRows", source);

        int start = source.IndexOf("IEnumerable<CardTile>[] ArrangeTiles(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ArrangeTiles not found");
        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        string body = source[start..end];

        var tiles = Regex.Matches(body, @"Tile\(0x([0-9A-F]{4}), ""Str_Arrange_\w+"", Services\.AnnotationArrange\.Action\.(\w+)\)")
            .Select(m => (Glyph: m.Groups[1].Value, Action: m.Groups[2].Value)).ToList();
        Assert.Equal(18, tiles.Count);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Glyph).Distinct().Count());
        Assert.Equal(Enum.GetNames(typeof(KillerPDF.Services.AnnotationArrange.Action)).OrderBy(n => n),
            tiles.Select(t => t.Action).OrderBy(n => n));

        // Six groups, three tiles each: one group per row of the three-across grid.
        var groups = Regex.Matches(body, @"\[\s*(Tile\([^\]]*?\),?\s*){3}\]");
        Assert.Equal(6, groups.Count);
    }

    [Fact]
    public void TheKitOpensATileGridFromAFlyoutTile()
    {
        string kit = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "CardBuilder.cs"));
        Assert.Contains("public IReadOnlyList<IReadOnlyList<CardTile>>? FlyoutTiles", kit);
        Assert.Contains("TileGridFlyout(card, button, caption, groups)", kit);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shell", "AnnotationCard.cs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
