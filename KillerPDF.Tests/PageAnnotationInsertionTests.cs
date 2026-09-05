using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageAnnotationInsertionTests
{
    [Fact]
    public void Shift_MovesAnnotationsAtAndAfterInsertionPoint()
    {
        var before = new TextAnnotation { PageIndex = 0, Content = "before" };
        var at = new TextAnnotation { PageIndex = 2, Content = "at" };
        var after = new TextAnnotation { PageIndex = 4, Content = "after" };
        var annotations = new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [before],
            [2] = [at],
            [4] = [after]
        };

        PageAnnotationInsertion.Shift(annotations, insertionIndex: 2, pageCount: 3);

        Assert.Same(before, Assert.Single(annotations[0]));
        Assert.Same(at, Assert.Single(annotations[5]));
        Assert.Same(after, Assert.Single(annotations[7]));
        Assert.Equal(0, before.PageIndex);
        Assert.Equal(5, at.PageIndex);
        Assert.Equal(7, after.PageIndex);
    }

    [Fact]
    public void Shift_WithZeroPages_LeavesAnnotationsUntouched()
    {
        var annotation = new TextAnnotation { PageIndex = 1, Content = "same" };
        var annotations = new Dictionary<int, List<PageAnnotation>> { [1] = [annotation] };

        PageAnnotationInsertion.Shift(annotations, insertionIndex: 1, pageCount: 0);

        Assert.Same(annotation, Assert.Single(annotations[1]));
        Assert.Equal(1, annotation.PageIndex);
    }

    [Fact]
    public void Shift_PreservesPlacedImageOnItsOriginalPage()
    {
        var image = new ImageAnnotation { PageIndex = 0, ImageData = "image" };
        var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = [image] };

        PageAnnotationInsertion.Shift(annotations, insertionIndex: 1, pageCount: 1);

        Assert.Same(image, Assert.Single(annotations[0]));
        Assert.Equal(0, image.PageIndex);
    }
}
