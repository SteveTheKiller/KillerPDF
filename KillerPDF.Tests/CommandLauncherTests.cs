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
        var launchers = new List<List<string>>();
        foreach (string file in new[] { "DataExchange.cs", "DocumentRepairs.cs", "ProductionTools.cs" })
        {
            string text = File.ReadAllText(Path.Combine(Root(), "Shell", file));
            foreach (Match call in Regex.Matches(text, @"OpenExchangeCommands\(""[^""]+""(?<args>.*?)\);", RegexOptions.Singleline))
            {
                var launcher = new List<string>();
                foreach (Match key in Regex.Matches(call.Groups["args"].Value, @"\(""(?<key>Str_[A-Za-z_]+)"""))
                    launcher.Add(key.Groups["key"].Value);
                keys.UnionWith(launcher);
                // A Loc("Str_...") call inside an action matches too, so one key can show up twice.
                launchers.Add([.. launcher.Distinct()]);
            }
        }
        // The metadata launcher builds its commands from the empty fields.
        string[] metadata = ["Str_DocInfo_Title", "Str_DocInfo_Author", "Str_DocInfo_Subject", "Str_DocInfo_Keywords"];
        keys.UnionWith(metadata);
        launchers.Add([.. metadata]);
        Assert.True(keys.Count >= 25, "found " + keys.Count);
        foreach (string key in keys)
        {
            Assert.True(CommandLauncher.HasGlyph(key), key + " has no icon");
            // 0xE8A5 is the generic document glyph, a plain square on a tile.
            Assert.NotEqual(0xE8A5, CommandLauncher.CodePointFor(key));
        }
        // Tiles side by side in one launcher (Form data's export and import, the page boxes) never share an icon.
        foreach (List<string> launcher in launchers.Where(launcher => launcher.Count > 1))
            Assert.True(launcher.Select(CommandLauncher.CodePointFor).Distinct().Count() == launcher.Count,
                string.Join(", ", launcher) + " share an icon");
    }

    [Fact]
    public void ACommandWithoutItsOwnIconGetsTheReportIcon()
    {
        Assert.Equal(0xE9F9, CommandLauncher.CodePointFor("Str_Not_A_Launcher_Command"));
        Assert.Equal(0xE9F9, CommandLauncher.CodePointFor("Str_DocumentTools_Preflight"));
    }

    [Theory]
    [InlineData(2, 0)]  // Bookmark data: a full row keeps Close on its own footer row
    [InlineData(5, 1)]  // Form data: Close takes the sixth cell
    [InlineData(6, 0)]  // Preflight
    [InlineData(7, 1)]  // Production layouts: Close takes the eighth cell
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    public void CloseTakesTheLastRowsOpenCell(int commands, int open)
    {
        Assert.Equal(open, CommandLauncher.OpenCells(commands, CommandLauncher.Columns(commands)));
    }

    [Fact]
    public void CloseMovesIntoTheLastRowOnlyWhenItHasRoom()
    {
        string dialog = File.ReadAllText(Path.Combine(Root(), "Controls", "DocumentCommandDialog.cs"));
        Assert.Contains("CommandLauncher.OpenCells(commands.Length, columns)", dialog);
        Assert.Contains("choices.Children.Add(close);", dialog);
        Assert.Contains("UiKit.ButtonRow(close)", dialog);
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
