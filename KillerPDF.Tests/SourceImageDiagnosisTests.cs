using System.IO;
using System.Runtime.CompilerServices;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SourceImageDiagnosisTests
{
    // Every reason the engine gives for a picture whose bounds cannot be edited, and the sentence the
    // status line shows for it. Unknown reasons fall back to the unreadable-placement sentence.
    [Theory]
    [InlineData("InlineImage", "Str_SourceImage_Inline")]
    [InlineData("NestedImage", "Str_SourceImage_Nested")]
    [InlineData("ImageMask", "Str_SourceImage_Masked")]
    [InlineData("OptionalContent", "Str_SourceImage_Layers")]
    [InlineData("PageTransparencyGroup", "Str_SourceImage_Transparency")]
    [InlineData("CompatibilityRecovery", "Str_SourceImage_Repaired")]
    [InlineData("ComplexPageState", "Str_SourceImage_Complex")]
    [InlineData("IncompletePageState", "Str_SourceImage_Complex")]
    [InlineData("NonAxisAlignedPlacement", "Str_SourceImage_Turned")]
    [InlineData("ClippedImage", "Str_SourceImage_Clipped")]
    [InlineData("MissingPlacement", "Str_SourceImage_Unreadable")]
    [InlineData("InvalidPlacement", "Str_SourceImage_Unreadable")]
    [InlineData("SomethingNew", "Str_SourceImage_Unreadable")]
    public void EachUneditablePictureReasonHasItsOwnLocalizedSentence(string reason, string key)
    {
        Assert.Equal(key, SourceImageDiagnosis.ReasonKey(reason));
        string strings = Path.Combine(RepositoryRoot(), "Strings");
        foreach (string file in Directory.GetFiles(strings, "*.xaml"))
            Assert.Contains("x:Key=\"" + key + "\"", File.ReadAllText(file));
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
