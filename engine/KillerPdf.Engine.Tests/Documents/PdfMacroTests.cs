using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMacroTests
{
    [Fact]
    public void MacroRoundTripsDuplicatesAndReordersWithoutSharingSettings()
    {
        var macro = new PdfMacro("Archive", [
            new(PdfMacroOperation.Ocr,
                new Dictionary<string, string> { ["language"] = "en-US" }),
            new(PdfMacroOperation.Validate),
            new(PdfMacroOperation.Save)]);

        PdfMacro restored = PdfMacro.FromJson(macro.ToJson());
        PdfMacro duplicate = restored.Duplicate("Archive copy").MoveStep(2, 0);

        Assert.Equal("Archive", restored.Name);
        Assert.Equal("en-US", restored.Steps[0].Settings!["language"]);
        Assert.Equal("Archive copy", duplicate.Name);
        Assert.Equal([
            PdfMacroOperation.Save,
            PdfMacroOperation.Ocr,
            PdfMacroOperation.Validate
        ], duplicate.Steps.Select(step => step.Operation));
        Assert.Throws<NotSupportedException>(() => PdfMacro.FromJson(
            macro.ToJson().Replace("\"version\":1", "\"version\":2",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void RunnerPreservesStepOrderAndIsolatesFailedInputs()
    {
        var macro = new PdfMacro("Prepare", [
            new(PdfMacroOperation.Ocr), new(PdfMacroOperation.Optimize)]);
        ReadOnlyMemory<byte>[] inputs = [new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }];

        IReadOnlyList<PdfMacroFileResult> results = PdfMacroRunner.Run(macro, inputs,
            (step, input, _) => input.Span[0] == 2
                ? throw new InvalidOperationException("Bad input")
                : input.ToArray().Append((byte)step.Operation).ToArray());

        Assert.Equal(new byte[] { 1, 0, 1 }, results[0].Data!.Value.ToArray());
        Assert.False(results[1].Succeeded);
        Assert.Equal("Bad input", results[1].Error);
        Assert.Equal(new byte[] { 3, 0, 1 }, results[2].Data!.Value.ToArray());
    }

    [Fact]
    public void MacroStepsCanBeInsertedReplacedAndRemoved()
    {
        var macro = new PdfMacro("Prepare", [
            new(PdfMacroOperation.Ocr), new(PdfMacroOperation.Save)]);

        PdfMacro edited = macro
            .InsertStep(1, new PdfMacroStep(PdfMacroOperation.Optimize))
            .ReplaceStep(0, new PdfMacroStep(PdfMacroOperation.Validate))
            .RemoveStep(2);

        Assert.Equal([
            PdfMacroOperation.Validate,
            PdfMacroOperation.Optimize
        ], edited.Steps.Select(step => step.Operation));
        Assert.Equal([
            PdfMacroOperation.Ocr,
            PdfMacroOperation.Save
        ], macro.Steps.Select(step => step.Operation));
        Assert.Throws<InvalidOperationException>(() =>
            new PdfMacro("Single", [new(PdfMacroOperation.Save)]).RemoveStep(0));
    }

    [Fact]
    public void RunnerStopsCleanlyWhenCanceled()
    {
        var cancellation = new CancellationTokenSource();
        var macro = new PdfMacro("Stop", [new(PdfMacroOperation.Validate)]);
        ReadOnlyMemory<byte>[] inputs = [new byte[] { 1 }, new byte[] { 2 }];

        IReadOnlyList<PdfMacroFileResult> results = PdfMacroRunner.Run(macro, inputs,
            (_, input, _) => { cancellation.Cancel(); return input; }, cancellation.Token);

        Assert.Single(results);
        Assert.True(results[0].Succeeded);
    }
}
