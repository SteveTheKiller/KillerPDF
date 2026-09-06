using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrLanguageModelTests
{
    [Fact]
    public void DecodeUsesContextToResolveVisualAmbiguity()
    {
        PdfOcrLanguageModel model = PdfOcrLanguageModel.Train(
            Enumerable.Repeat("GOOD", 20));
        IReadOnlyList<IReadOnlyList<PdfOcrLanguageCandidate>> positions =
        [
            [new("G", 1)],
            [new("0", 1.1), new("O", 1)],
            [new("0", 1.1), new("O", 1)],
            [new("D", 1)]
        ];

        Assert.Equal(["G", "O", "O", "D"], model.Decode(positions));
    }

    [Fact]
    public void DecodeIsDeterministicAndHonorsVisualScoresWithoutContext()
    {
        PdfOcrLanguageModel model = PdfOcrLanguageModel.Train(["AB", "CD"]);
        IReadOnlyList<PdfOcrLanguageCandidate>[] positions =
        [
            [new("X", 2), new("Y", 1)],
            [new("Z", 2), new("W", 1)]
        ];

        Assert.Equal(["X", "Z"], model.Decode(positions, languageWeight: 0));
        Assert.Equal(model.Decode(positions), model.Decode(positions));
        byte[] saved = model.Save();
        Assert.Equal(saved, PdfOcrLanguageModel.Load(saved).Save());
        Assert.Equal(model.Decode(positions),
            PdfOcrLanguageModel.Load(saved).Decode(positions));
    }

    [Fact]
    public void TrainStartsNewSequencesAtWordBoundaries()
    {
        PdfOcrLanguageModel model = PdfOcrLanguageModel.Train(
            Enumerable.Repeat("AB CD", 20));

        Assert.Equal(["B", "A"], model.Decode(
        [
            [new("B", 0)],
            [new("C", -0.1), new("A", 0)]
        ]));
    }

    [Fact]
    public void CombineMergesTransitionsDeterministically()
    {
        PdfOcrLanguageModel first = PdfOcrLanguageModel.Train(["AB"]);
        PdfOcrLanguageModel second = PdfOcrLanguageModel.Train(["CD"]);

        Assert.Equal(PdfOcrLanguageModel.Combine([first, second]).Save(),
            PdfOcrLanguageModel.Combine([second, first]).Save());
        Assert.Throws<ArgumentException>(() => PdfOcrLanguageModel.Combine([]));
    }

    [Fact]
    public void TrainAndDecodeRejectInvalidInputAndHonorCancellation()
    {
        Assert.Throws<ArgumentException>(() => PdfOcrLanguageModel.Train([""]));
        PdfOcrLanguageModel model = PdfOcrLanguageModel.Train(["AB"]);
        Assert.Throws<ArgumentException>(() => model.Decode([[]]));
        Assert.Throws<ArgumentException>(() => model.Decode(
            Enumerable.Repeat<IReadOnlyList<PdfOcrLanguageCandidate>>(
                [new("A", 1)], 4_097).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Decode(
            [[new("A", 1)]], double.NaN));
        Assert.Throws<InvalidDataException>(() =>
            PdfOcrLanguageModel.Load(new byte[] { 1, 2, 3 }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfOcrLanguageModel.Train(["AB"], canceled.Token));
    }

    [Fact]
    public void LoadRejectsLargeInvalidInputWithoutCopyingIt()
    {
        var source = new byte[8 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<InvalidDataException>(() =>
            PdfOcrLanguageModel.Load(source));

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1_000_000);
    }

    [Fact]
    public void DecodeKeepsLongCandidateSequencesWithinLinearMemory()
    {
        PdfOcrLanguageModel model = PdfOcrLanguageModel.Train(["AB"]);
        IReadOnlyList<IReadOnlyList<PdfOcrLanguageCandidate>> positions =
            Enumerable.Repeat<IReadOnlyList<PdfOcrLanguageCandidate>>(
                [new("A", 1), new("B", 0)], 1_024).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();

        IReadOnlyList<string> decoded = model.Decode(positions, languageWeight: 0);

        Assert.Equal(1_024, decoded.Count);
        Assert.All(decoded, label => Assert.Equal("A", label));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 5_000_000);
    }
}
