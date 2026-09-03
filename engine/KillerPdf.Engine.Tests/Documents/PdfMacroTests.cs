using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMacroTests
{
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
