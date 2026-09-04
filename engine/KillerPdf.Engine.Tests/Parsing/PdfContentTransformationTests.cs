using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfContentTransformationTests
{
    [Fact]
    public void RemoveTextObjectsRemovesOnlySelectedCompleteObjects()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "q BT /F1 10 Tf (first) Tj ET 7 FutureOp BT /F2 12 Tf (second) Tj ET Q"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.RemoveTextObjects(source, [0]);
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal(["q", "FutureOp", "BT", "Tf", "Tj", "ET", "Q"],
            reopened.Select(instruction => instruction.Operator));
        Assert.Equal("second", System.Text.Encoding.Latin1.GetString(
            Assert.IsType<PdfString>(reopened[4].Operands[0]).Bytes.Span));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemoveTextObjects(source, [0, 0]));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemoveTextObjects(source, [2]));
        Assert.Throws<FormatException>(() => PdfContentTransformation.RemoveTextObjects(
            PdfContentStreamReader.Read("BT (open) Tj"u8.ToArray()), [0]));
        Assert.Throws<FormatException>(() => PdfContentTransformation.RemoveTextObjects(
            PdfContentStreamReader.Read("BT q (open) Tj ET Q"u8.ToArray()), [0]));
    }

    [Fact]
    public void RemovePaintedPathsPreservesOtherArtworkAndRejectsClipping()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "1 0 0 rg 0 0 10 10 re f 0 0 1 RG 20 20 m 30 30 l S 7 FutureOp"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.RemovePaintedPaths(source, [0]);
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal(["rg", "RG", "m", "l", "S", "FutureOp"],
            reopened.Select(instruction => instruction.Operator));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemovePaintedPaths(source, [2]));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemovePaintedPaths(source, [1, 1]));
        Assert.Throws<NotSupportedException>(() =>
            PdfContentTransformation.RemovePaintedPaths(
                PdfContentStreamReader.Read("0 0 10 10 re W n"u8.ToArray()), [0]));
    }

    [Fact]
    public void TransformPaintedPathsWrapsOnlySelectedArtwork()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "0 0 10 10 re f 20 20 m 30 30 l S"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.TransformPaintedPaths(source, [1],
                PdfContentTransformMatrix.Translation(12, 34));

        Assert.Equal(["re", "f", "q", "cm", "m", "l", "S", "Q"],
            result.Select(instruction => instruction.Operator));
        Assert.Equal([12d, 34d], result[3].Operands.Skip(4)
            .Cast<PdfReal>().Select(value => value.Value));
        Assert.Throws<NotSupportedException>(() =>
            PdfContentTransformation.TransformPaintedPaths(
                PdfContentStreamReader.Read("0 0 10 10 re W n"u8.ToArray()),
                [0], PdfContentTransformMatrix.Scale(2, 2)));
    }

    [Fact]
    public void RewriteRemovesAndReplacesSelectedInstructions()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "1 Keep 2 Remove 3 Replace"u8.ToArray());
        var replacement = new PdfContentInstruction("Changed", 0, [new PdfInteger(4)]);

        IReadOnlyList<PdfContentInstruction> result = PdfContentTransformation.Rewrite(source,
            new Dictionary<int, PdfContentInstruction?> { [1] = null, [2] = replacement });

        Assert.Equal(["Keep", "Changed"], result.Select(item => item.Operator));
        Assert.Equal("1 Keep\n4 Changed\n"u8.ToArray(), PdfContentStreamWriter.Write(result));
        Assert.Equal("Remove", source[1].Operator);
    }

    [Fact]
    public void TransformAllPreservesUnknownOperatorsAndInlineImageBytes()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "7 FutureOp BI /W 1 /H 1 /BPC 8 /CS /RGB ID abc EI"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> transformed = PdfContentTransformation.TransformAll(
            source, PdfContentTransformMatrix.Translation(12, 34));
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(transformed));

        Assert.Equal(["q", "cm", "FutureOp", "BI", "Q"], reopened.Select(item => item.Operator));
        Assert.Equal("abc"u8.ToArray(), reopened[3].InlineImageData?.ToArray());
        Assert.Equal([12d, 34d], reopened[1].Operands.Skip(4).Cast<PdfReal>().Select(item => item.Value));
    }

    [Fact]
    public void MatrixFactoriesValidateAndCalculateRotation()
    {
        PdfContentTransformMatrix rotation = PdfContentTransformMatrix.Rotation(90);

        Assert.Equal(0, rotation.A, 10);
        Assert.Equal(1, rotation.B, 10);
        Assert.Equal(-1, rotation.C, 10);
        Assert.Equal(0, rotation.D, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentTransformMatrix.Scale(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentTransformation.Rewrite([],
            new Dictionary<int, PdfContentInstruction?> { [0] = null }));
    }

    [Fact]
    public void TransformRangeChangesOnlySelectedInstructions()
    {
        IReadOnlyList<PdfContentInstruction> source =
            PdfContentStreamReader.Read("1 A 2 B 3 C 4 D"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.TransformRange(
                source, 1, 2, PdfContentTransformMatrix.Translation(10, 20));

        Assert.Equal(["A", "q", "cm", "B", "C", "Q", "D"],
            result.Select(instruction => instruction.Operator));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfContentTransformation.TransformRange(source, 3, 2,
                PdfContentTransformMatrix.Translation(0, 0)));
    }

    [Fact]
    public void TransformXObjectPlacementsSelectsResourceOccurrences()
    {
        var image = new PdfName("Im1"u8);
        var form = new PdfName("Fm1"u8);
        PdfContentInstruction[] source =
        [
            new("Do", 0, [image]),
            new("Do", 4, [form]),
            new("Do", 8, [image])
        ];

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.TransformXObjectPlacements(
                source, image, [1], PdfContentTransformMatrix.Translation(12, 34));

        Assert.Equal(["Do", "Do", "q", "cm", "Do", "Q"],
            result.Select(instruction => instruction.Operator));
        Assert.Equal([12d, 34d], result[3].Operands.Skip(4)
            .Cast<PdfReal>().Select(value => value.Value));
        Assert.Equal(8, result[4].Offset);
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.TransformXObjectPlacements(
                source, image, [2], PdfContentTransformMatrix.Scale(2, 2)));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.TransformXObjectPlacements(
                source, image, [0, 0], PdfContentTransformMatrix.Scale(2, 2)));
    }

    [Fact]
    public void RemoveXObjectPlacementsSelectsOnlyRequestedOccurrences()
    {
        var image = new PdfName("Im1"u8);
        var form = new PdfName("Fm1"u8);
        PdfContentInstruction[] source =
        [
            new("Do", 0, [image]),
            new("Do", 4, [form]),
            new("Do", 8, [image])
        ];

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.RemoveXObjectPlacements(source, image, [0]);

        Assert.Equal([form, image], result.Select(instruction =>
            Assert.IsType<PdfName>(Assert.Single(instruction.Operands))));
        Assert.Equal([4, 8], result.Select(instruction => instruction.Offset));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemoveXObjectPlacements(source, image, [2]));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemoveXObjectPlacements(source, image, [1, 1]));
    }

    [Fact]
    public void ClipXObjectPlacementsSelectsOnlyRequestedOccurrences()
    {
        var image = new PdfName("Im1"u8);
        var form = new PdfName("Fm1"u8);
        PdfContentInstruction[] source =
        [
            new("Do", 0, [image]),
            new("Do", 4, [form]),
            new("Do", 8, [image])
        ];

        IReadOnlyList<PdfContentInstruction> result =
            PdfContentTransformation.ClipXObjectPlacements(source, image, [1],
                new PdfContentClipRectangle(10, 20, 30, 40), evenOdd: true);

        Assert.Equal(["Do", "Do", "q", "re", "W*", "n", "Do", "Q"],
            result.Select(instruction => instruction.Operator));
        Assert.Equal([10d, 20d, 30d, 40d],
            result[3].Operands.Cast<PdfReal>().Select(value => value.Value));
        Assert.Equal(8, result[6].Offset);
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.ClipXObjectPlacements(source, image, [2],
                new PdfContentClipRectangle(0, 0, 1, 1)));
    }

    [Fact]
    public void TransformAndRemoveShadingPlacementsSelectOccurrencesByResource()
    {
        var first = new PdfName("Shade1"u8);
        var second = new PdfName("Shade2"u8);
        PdfContentInstruction[] source =
        [
            new("sh", 0, [first]),
            new("sh", 8, [second]),
            new("sh", 16, [first])
        ];

        IReadOnlyList<PdfContentInstruction> transformed =
            PdfContentTransformation.TransformShadingPlacements(
                source, first, [1], PdfContentTransformMatrix.Translation(20, 30));
        IReadOnlyList<PdfContentInstruction> removed =
            PdfContentTransformation.RemoveShadingPlacements(source, first, [0]);

        Assert.Equal(["sh", "sh", "q", "cm", "sh", "Q"],
            transformed.Select(item => item.Operator));
        Assert.Equal([20d, 30d], transformed[3].Operands.Skip(4)
            .Cast<PdfReal>().Select(value => value.Value));
        Assert.Equal([second, first], removed.Select(item =>
            Assert.IsType<PdfName>(Assert.Single(item.Operands))));
        Assert.Throws<ArgumentException>(() =>
            PdfContentTransformation.RemoveShadingPlacements(source, first, [2]));
    }

    [Fact]
    public void ClipRangeChangesOnlySelectedInstructionsAndRoundTrips()
    {
        IReadOnlyList<PdfContentInstruction> source =
            PdfContentStreamReader.Read("1 A 2 B 3 C"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result = PdfContentTransformation.ClipRange(
            source, 1, 1, new PdfContentClipRectangle(10, 20, 30, 40), evenOdd: true);
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal(["A", "q", "re", "W*", "n", "B", "Q", "C"],
            reopened.Select(instruction => instruction.Operator));
        Assert.Equal([10d, 20d, 30d, 40d],
            reopened[2].Operands.Cast<PdfReal>().Select(item => item.Value));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentTransformation.ClipRange(
            source, 3, 1, new PdfContentClipRectangle(0, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfContentClipRectangle(0, 0, 0, 1));
    }

    [Fact]
    public void RecolorDeviceRangeChangesSelectedFillAndStrokeColorsOnly()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "0 g 0 G 0 1 0 rg 0 1 0 RG 0 0 0 1 k 0 0 0 1 K /Spot cs 0.5 scn 7 FutureOp"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result = PdfContentTransformation.RecolorDeviceRange(
            source, 1, 7, new PdfDeviceRgbColor(0.25, 0.5, 0.75));
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal("g", reopened[0].Operator);
        Assert.Equal(["RG", "rg", "RG", "rg", "RG", "cs", "scn"],
            reopened.Skip(1).Take(7).Select(item => item.Operator));
        Assert.All(reopened.Skip(1).Take(5), item => Assert.Equal(
            [0.25, 0.5, 0.75], item.Operands.Cast<PdfReal>().Select(value => value.Value)));
        Assert.Equal("FutureOp", reopened[^1].Operator);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfDeviceRgbColor(0, 1.1, 0));
        Assert.Throws<ArgumentException>(() => PdfContentTransformation.RecolorDeviceRange(
            source, 0, 1, new PdfDeviceRgbColor(0, 0, 0), fill: false, stroke: false));
    }

    [Fact]
    public void ResizeTextRangePreservesFontResourcesAndUntouchedInstructions()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "/F1 10 Tf (first) Tj 7 FutureOp /F2 12 Tf (second) Tj"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result = PdfContentTransformation.ResizeTextRange(
            source, 0, 3, 18);
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal("F1", Assert.IsType<PdfName>(reopened[0].Operands[0]).ValueAsLatin1());
        Assert.Equal(18, Assert.IsType<PdfReal>(reopened[0].Operands[1]).Value);
        Assert.Equal("FutureOp", reopened[2].Operator);
        Assert.Equal(12, Assert.IsType<PdfInteger>(reopened[3].Operands[1]).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfContentTransformation.ResizeTextRange(source, 0, source.Count, 0));
    }

    [Fact]
    public void SubstituteFontRangePreservesSizesAndUntouchedInstructions()
    {
        IReadOnlyList<PdfContentInstruction> source = PdfContentStreamReader.Read(
            "/F1 10 Tf (first) Tj 7 FutureOp /F2 12.5 Tf (second) Tj"u8.ToArray());

        IReadOnlyList<PdfContentInstruction> result = PdfContentTransformation.SubstituteFontRange(
            source, 0, 3, new PdfName("Replacement"u8));
        IReadOnlyList<PdfContentInstruction> reopened = PdfContentStreamReader.Read(
            PdfContentStreamWriter.Write(result));

        Assert.Equal("Replacement",
            Assert.IsType<PdfName>(reopened[0].Operands[0]).ValueAsLatin1());
        Assert.Equal(10, Assert.IsType<PdfInteger>(reopened[0].Operands[1]).Value);
        Assert.Equal("FutureOp", reopened[2].Operator);
        Assert.Equal("F2", Assert.IsType<PdfName>(reopened[3].Operands[0]).ValueAsLatin1());
        Assert.Equal(12.5, Assert.IsType<PdfReal>(reopened[3].Operands[1]).Value);
        Assert.Throws<ArgumentException>(() => PdfContentTransformation.SubstituteFontRange(
            source, 0, source.Count, new PdfName([])));
    }
}
