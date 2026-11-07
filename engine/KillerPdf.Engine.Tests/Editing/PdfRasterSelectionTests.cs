using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfRasterSelectionTests
{
    [Fact]
    public void ConnectedSelectionKeepsSeparateRedIslandAndTransparentHoleUnselected()
    {
        float[] pixels =
        [
            1,0,0,1, 1,0,0,.25f, 0,0,1,1, 1,0,0,1, 1,0,0,1,
            1,0,0,1, 1,0,0,0,    0,0,1,1, 1,0,0,1, 1,0,0,1,
            0,0,1,1, 0,0,1,1,    0,0,1,1, 1,0,0,1, 1,0,0,0
        ];
        float[] original = pixels.ToArray();
        var frame = new PdfRenderedFrame(5, 3, pixels, PdfFrameColorEncoding.LinearSrgbD65);

        var selection = PdfRasterSelection.SelectConnected(frame, 0, 0, 0);

        Assert.Equal(5, selection.Width);
        Assert.Equal(3, selection.Height);
        Assert.Equal(new byte[] { 255,255,0,0,0, 255,0,0,0,0, 0,0,0,0,0 }, selection.Coverage.ToArray());
        Assert.Equal(original, frame.Pixels.ToArray());
        Assert.Equal(original, pixels);
    }
    [Fact]
    public void ToleranceIsInclusiveInDeclaredEncodingWithoutDiagonalOrTransparentBridges()
    {
        float[] pixels =
        [
            .25f,.5f,-.25f,1, .5f,.5f,-.25f,.001f, .50000006f,.5f,-.25f,1,
            0,0,1,1,          .25f,.5f,-.25f,0,    .25f,.5f,-.25f,1
        ];
        var frame = new PdfRenderedFrame(3, 2, pixels, PdfFrameColorEncoding.SrgbD65);
        var selection = PdfRasterSelection.SelectConnected(frame, 0, 0, .25f);
        Assert.Equal(new byte[] { 255,255,0, 0,0,0 }, selection.Coverage.ToArray());
        Assert.Equal(new byte[] { 0,0,0, 0,0,0 }, PdfRasterSelection.SelectConnected(frame, 1, 1, 100).Coverage.ToArray());
        Assert.Equal(pixels, frame.Pixels.ToArray());
    }
    [Fact]
    public void RectangleSelectsOnlyItsLiteralTopLeftPixels()
    {
        var selection = PdfRasterSelection.SelectRectangle(5, 3, 1, 1, 3, 2);
        Assert.Equal(5, selection.Width);
        Assert.Equal(3, selection.Height);
        Assert.Equal(new byte[] { 0,0,0,0,0, 0,255,255,255,0, 0,255,255,255,0 }, selection.Coverage.ToArray());
    }
    [Fact]
    public void AddUnionsOverlappingSelectionsWithoutChangingEitherInput()
    {
        var left = PdfRasterSelection.SelectRectangle(5, 3, 0, 0, 2, 3);
        var middle = PdfRasterSelection.SelectRectangle(5, 3, 1, 1, 3, 1);
        var union = left.Add(middle);
        Assert.Equal(new byte[] { 255,255,0,0,0, 255,255,255,255,0, 255,255,0,0,0 }, union.Coverage.ToArray());
        Assert.Equal(new byte[] { 255,255,0,0,0, 255,255,0,0,0, 255,255,0,0,0 }, left.Coverage.ToArray());
        Assert.Equal(new byte[] { 0,0,0,0,0, 0,255,255,255,0, 0,0,0,0,0 }, middle.Coverage.ToArray());
        Assert.NotSame(left, union);
        Assert.NotSame(middle, union);
    }
    [Fact]
    public void SubtractCutsAnInteriorHoleThatAddCanRefineWithoutMutatingInputs()
    {
        var full = PdfRasterSelection.SelectRectangle(5, 3, 0, 0, 5, 3);
        var hole = PdfRasterSelection.SelectRectangle(5, 3, 1, 1, 3, 1);
        var cut = full.Subtract(hole);
        Assert.Equal(new byte[] { 255,255,255,255,255, 255,0,0,0,255, 255,255,255,255,255 }, cut.Coverage.ToArray());
        var refined = cut.Add(PdfRasterSelection.SelectRectangle(5, 3, 2, 1, 1, 1));
        Assert.Equal(new byte[] { 255,255,255,255,255, 255,0,255,0,255, 255,255,255,255,255 }, refined.Coverage.ToArray());
        Assert.Equal(new byte[] { 255,255,255,255,255, 255,255,255,255,255, 255,255,255,255,255 }, full.Coverage.ToArray());
        Assert.Equal(new byte[] { 0,0,0,0,0, 0,255,255,255,0, 0,0,0,0,0 }, hole.Coverage.ToArray());
        Assert.NotSame(full, cut);
        Assert.NotSame(cut, refined);
    }
    [Fact]
    public void AdjacentFloatLevelsAndOwnedCopiesRetainSourceAndSelectionPrecision()
    {
        float[] pixels = [ .5f,0,0,1, .50000006f,0,0,.125f ];
        var frame = new PdfRenderedFrame(2, 1, pixels, PdfFrameColorEncoding.LinearSrgbD65);
        pixels[0] = 0;
        var selection = PdfRasterSelection.SelectConnected(frame, 0, 0, 0);
        Assert.Equal(new byte[] { 255,0 }, selection.Coverage.ToArray());
        byte[] disposableCopy = selection.Coverage.ToArray();
        disposableCopy[0] = 0;
        Assert.Equal(new byte[] { 255,0 }, selection.Coverage.ToArray());
        Assert.Equal(new byte[] { 255,0 }, selection.Add(selection).Coverage.ToArray());
        Assert.Equal(new byte[] { 0,0 }, selection.Subtract(selection).Coverage.ToArray());
        Assert.Equal(new float[] { .5f,0,0,1, .50000006f,0,0,.125f }, frame.Pixels.ToArray());
    }
    [Fact]
    public void InvalidInputsAndMismatchedDimensionsAreRejectedWithoutClamping()
    {
        var frame = new PdfRenderedFrame(2, 1, new float[] { 1,0,0,1, 1,0,0,1 }, PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Throws<ArgumentNullException>(() => PdfRasterSelection.SelectConnected(null!, 0, 0, 0));
        foreach (var seed in new[] { (-1,0), (2,0), (0,-1), (0,1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectConnected(frame, seed.Item1, seed.Item2, 0));
        foreach (float tolerance in new[] { -.001f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectConnected(frame, 0, 0, tolerance));
        foreach (var rectangle in new[] { (-1,0,1,1), (0,-1,1,1), (2,0,1,1), (0,2,1,1),
                     (0,0,0,1), (0,0,1,0), (1,0,2,1), (0,1,1,2), (1,0,int.MaxValue,1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectRectangle(2, 2,
                rectangle.Item1, rectangle.Item2, rectangle.Item3, rectangle.Item4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectRectangle(0, 2, 0, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectRectangle(2, -1, 0, 0, 1, 1));
        var horizontal = PdfRasterSelection.SelectRectangle(2, 1, 0, 0, 1, 1);
        var vertical = PdfRasterSelection.SelectRectangle(1, 2, 0, 0, 1, 1);
        Assert.Throws<ArgumentNullException>(() => horizontal.Add(null!));
        Assert.Throws<ArgumentNullException>(() => horizontal.Subtract(null!));
        Assert.Throws<ArgumentException>(() => horizontal.Add(vertical));
        Assert.Throws<ArgumentException>(() => horizontal.Subtract(vertical));
        Assert.Equal(new byte[] { 255,0 }, horizontal.Coverage.ToArray());
        Assert.Equal(new byte[] { 255,0 }, vertical.Coverage.ToArray());
    }
    [Fact]
    public void CombinedBudgetsRejectSmallLimitsAndHugeDimensionsBeforeLargeBuffers()
    {
        var frame = new PdfRenderedFrame(16, 16, new float[1024], PdfFrameColorEncoding.LinearSrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(16, 16, 0, 0, 1, 1);
        foreach (long invalid in new[] { -1L, 0L, 256L * 1024 * 1024 + 1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectConnected(frame, 0, 0, 0, invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterSelection.SelectRectangle(16, 16, 0, 0, 1, 1, invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => selection.Add(selection, invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => selection.Subtract(selection, invalid));
        }
        Assert.Throws<ArgumentException>(() => PdfRasterSelection.SelectConnected(frame, 0, 0, 0, 2048));
        Assert.Throws<ArgumentException>(() => PdfRasterSelection.SelectRectangle(16, 16, 0, 0, 1, 1, 512));
        Assert.Throws<ArgumentException>(() => selection.Add(selection, 1024));
        Assert.Throws<ArgumentException>(() => selection.Subtract(selection, 1024));
        Assert.Throws<ArgumentException>(() => PdfRasterSelection.SelectRectangle(int.MaxValue, int.MaxValue, 0, 0, 1, 1, 1024));
        Assert.Equal((byte)255, selection.Coverage[0]);
        Assert.All(selection.Coverage[1..].ToArray(), value => Assert.Equal((byte)0, value));
        Assert.All(frame.Pixels.ToArray(), value => Assert.Equal(0f, value));
    }
    [Fact]
    public void CancellationStopsEveryPublicOperationWithoutChangingInputs()
    {
        float[] pixels = [ 1,0,0,.25f, 1,0,0,1 ];
        var frame = new PdfRenderedFrame(2, 1, pixels, PdfFrameColorEncoding.LinearSrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(2, 1, 0, 0, 1, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var connected = Assert.Throws<OperationCanceledException>(() => PdfRasterSelection.SelectConnected(frame, 0, 0, 0,
            cancellationToken: cancellation.Token));
        var rectangle = Assert.Throws<OperationCanceledException>(() => PdfRasterSelection.SelectRectangle(2, 1, 0, 0, 1, 1,
            cancellationToken: cancellation.Token));
        var add = Assert.Throws<OperationCanceledException>(() => selection.Add(selection, cancellationToken: cancellation.Token));
        var subtract = Assert.Throws<OperationCanceledException>(() => selection.Subtract(selection, cancellationToken: cancellation.Token));
        foreach (var error in new[] { connected, rectangle, add, subtract }) Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(new byte[] { 255,0 }, selection.Coverage.ToArray());
        Assert.Equal(pixels, frame.Pixels.ToArray());
    }
}
