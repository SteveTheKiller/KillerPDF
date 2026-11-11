using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AnnotationScaleTests
{
    [Theory]
    [InlineData(0, 100, 100, 100, "sourceWidth")]
    [InlineData(100, 0, 100, 100, "sourceHeight")]
    [InlineData(100, 100, 0, 100, "targetWidth")]
    [InlineData(100, 100, 100, 0, "targetHeight")]
    public void InvalidCanvasDimensionNamesTheActualParameter(int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight, string parameter)
    {
        var annotation = new TextAnnotation();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnnotationScale.Remap(annotation, sourceWidth, sourceHeight, targetWidth, targetHeight));
        Assert.Equal(parameter, error.ParamName);
    }
}
