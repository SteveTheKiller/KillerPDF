using System.IO;
using System.Text.RegularExpressions;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The document tool launchers (Production layouts, Form data, Preflight and the rest) are a grid of icon tiles, not a
// column of identical buttons: every command has its own icon, and the grid never strands one tile on its last row.
public sealed class CommandLauncherTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void EveryLauncherCommandHasItsOwnIcon()
    {
        var keys = new HashSet<string>();
        foreach (string file in new[] { "DataExchange.cs", "DocumentRepairs.cs", "ProductionTools.cs" })
        {
            string text = File.ReadAllText(Path.Combine(Root(), "Shell", file));
            foreach (Match call in Regex.Matches(text, @"OpenExchangeCommands\(""[^""]+""(?<args>.*?)\);", RegexOptions.Singleline))
                foreach (Match key in Regex.Matches(call.Groups["args"].Value, @"\(""(?<key>Str_[A-Za-z_]+)"""))
                    keys.Add(key.Groups["key"].Value);
        }
        // The metadata launcher builds its commands from the empty fields.
        keys.UnionWith(["Str_DocInfo_Title", "Str_DocInfo_Author", "Str_DocInfo_Subject", "Str_DocInfo_Keywords"]);
        Assert.True(keys.Count >= 25, "found " + keys.Count);
        foreach (string key in keys)
            Assert.True(CommandLauncher.HasGlyph(key), key + " has no icon");
    }

    [Fact]
    public void TheLauncherIsAGridOfCardTiles()
    {
        string dialog = File.ReadAllText(Path.Combine(Root(), "Controls", "DocumentCommandDialog.cs"));
        Assert.Contains("\"CardQuickButton\"", dialog);
        Assert.Contains("new UniformGrid { Columns = CommandLauncher.Columns(commands.Length) }", dialog);
        Assert.Contains("CommandLauncher.GlyphFor(command.Label)", File.ReadAllText(Path.Combine(Root(), "Shell", "DataExchange.cs")));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    public void TheGridNeverStrandsOneTile(int commands, int columns)
    {
        Assert.Equal(columns, CommandLauncher.Columns(commands));
        if (commands > columns) Assert.NotEqual(1, commands % columns);
    }
}
