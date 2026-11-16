using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// #498: New folder in the open and save dialogs. The typed name is checked before anything is created, and the box
// starts with a name that is free, the way Explorer does it.
public sealed class FolderNameRuleTests
{
    [Theory]
    [InlineData("Invoices 2026")]
    [InlineData("a")]
    [InlineData("名前")]
    public void AnOrdinaryNameIsAccepted(string name) =>
        Assert.Equal(FolderNameProblem.None, FolderNameRule.Check(name, _ => false));

    [Theory]
    [InlineData("", FolderNameProblem.Empty)]
    [InlineData("   ", FolderNameProblem.Empty)]
    [InlineData("a/b", FolderNameProblem.InvalidCharacters)]
    [InlineData("what?", FolderNameProblem.InvalidCharacters)]
    [InlineData("pipe|name", FolderNameProblem.InvalidCharacters)]
    [InlineData("CON", FolderNameProblem.Reserved)]
    [InlineData("nul.txt", FolderNameProblem.Reserved)]
    [InlineData("Com7", FolderNameProblem.Reserved)]
    [InlineData("LPT1", FolderNameProblem.Reserved)]
    [InlineData("ends with dot.", FolderNameProblem.TrailingDotOrSpace)]
    [InlineData(".", FolderNameProblem.TrailingDotOrSpace)]
    [InlineData("..", FolderNameProblem.TrailingDotOrSpace)]
    public void AnUnusableNameIsRefusedWithItsReason(string name, FolderNameProblem expected) =>
        Assert.Equal(expected, FolderNameRule.Check(name, _ => false));

    [Fact]
    public void ANameLongerThanWindowsAllowsIsRefused() =>
        Assert.Equal(FolderNameProblem.TooLong, FolderNameRule.Check(new string('a', 256), _ => false));

    [Fact]
    public void ANameAlreadyInTheFolderIsRefused() =>
        Assert.Equal(FolderNameProblem.Exists, FolderNameRule.Check("Reports", name => name == "Reports"));

    [Fact]
    public void SurroundingSpacesAreTrimmedBeforeTheCheck()
    {
        Assert.Equal(FolderNameProblem.None, FolderNameRule.Check("  Reports", _ => false));
        Assert.Equal("Reports", FolderNameRule.Clean("  Reports  "));
    }

    [Fact]
    public void TheSuggestedNameIsTheFirstOneFree()
    {
        Assert.Equal("New folder", FolderNameRule.FirstFree("New folder", _ => false));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "New folder", "new folder (2)" };
        Assert.Equal("New folder (3)", FolderNameRule.FirstFree("New folder", taken.Contains));
    }
}
