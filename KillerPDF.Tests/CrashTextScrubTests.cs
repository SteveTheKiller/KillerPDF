using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CrashTextScrubTests
{
    [Fact]
    public void FoldersAreRemovedAndTheFileNameStays()
    {
        string scrubbed = CrashTextScrub.Paths(@"Could not find file 'C:\Users\jose\Clients\Smith divorce.pdf'.");
        Assert.Equal(@"Could not find file '<path>\Smith divorce.pdf'.", scrubbed);
        Assert.DoesNotContain("jose", scrubbed);
    }

    [Fact]
    public void StackFramesAndSharesAreScrubbed()
    {
        string stack = "   at KillerPDF.X() in D:\\build\\KillerPDF\\X.cs:line 12\n   at Y() \\\\server\\share\\dept\\a.pdf";
        string scrubbed = CrashTextScrub.Paths(stack);
        Assert.DoesNotContain("build", scrubbed);
        Assert.DoesNotContain("server", scrubbed);
        Assert.Contains("a.pdf", scrubbed);
    }

    [Fact]
    public void TextWithoutPathsIsUnchanged() =>
        Assert.Equal("Object reference not set.", CrashTextScrub.Paths("Object reference not set."));
}
