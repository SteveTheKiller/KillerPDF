using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfContentTransformationTests
{
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
}
