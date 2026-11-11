using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// What the book conversion and the book link jump say when they fail is the app's own wording, in the
// language the app is set to. An English sentence written in code reaches the dialog or status line otherwise.
public sealed class NativeFailureMessagesTests
{
    private static readonly Regex EnglishThrow = new(@"throw new \w+Exception\(\s*""[A-Z]", RegexOptions.Compiled);

    [Fact]
    public void TheConversionsOwnFailuresAreAppStringsNotEnglishLiterals()
    {
        string source = ShellSource.Read("Shell", "NativeConversion.cs");
        string method = ShellSource.Between(source, "private async void ConvertNativeDocumentToPdf()", "internal void ShowDjvuConversionNote(");

        Assert.DoesNotMatch(EnglishThrow, method);
        Assert.Contains("Loc(\"Str_Convert_FontUnavailable\")", method, StringComparison.Ordinal);
        Assert.Contains("Loc(\"Str_Convert_DestinationNotPermanent\")", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ABookLinkJumpsOwnFailuresAreAppStringsNotEnglishLiterals()
    {
        string source = ShellSource.Read("Controls", "Viewer", "PdfViewer.Native.cs");

        Assert.DoesNotMatch(EnglishThrow, source);
        Assert.Contains("Loc(\"Str_Native_NavigationPageUnavailable\")", source, StringComparison.Ordinal);
        Assert.Contains("Loc(\"Str_Native_NavigationPositionInvalid\")", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Str_Convert_FontUnavailable")]
    [InlineData("Str_Convert_DestinationNotPermanent")]
    [InlineData("Str_Native_NavigationPageUnavailable")]
    [InlineData("Str_Native_NavigationPositionInvalid")]
    public void EachNewMessageIsDefinedInEnglish(string key)
    {
        string english = ShellSource.Read("Strings", "en-US.xaml");

        Assert.Matches(new Regex("x:Key=\"" + key + "\"[^>]*>[^<]{10,}</sys:String>"), english);
    }
}