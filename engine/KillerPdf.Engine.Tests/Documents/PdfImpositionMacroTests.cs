using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImpositionMacroTests
{
    [Fact]
    public void NUpStepRoundTripsAndExportsPresetSheetGeometry()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(200, 300)
            .AddBlankPage(300, 200)
            .AddBlankPage(200, 300)
            .Build();
        var preset = new PdfImpositionPreset(
            "Two-up", 2, 1, 792, 612, margin: 18, gutter: 12, duplex: true,
            includeFoldMarks: true, includeColorBars: true,
            includePageInformation: true,
            bindingEdge: PdfImpositionBindingEdge.Short);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro(
            "Impose", [PdfImpositionMacro.NUpStep(preset)]).ToJson());

        ReadOnlyMemory<byte> output = PdfImpositionMacro.Execute(
            Assert.Single(macro.Steps), source);
        PdfDocument imposed = PdfDocument.Open(output);
        IReadOnlyList<PdfPageBoxInformation> pages =
            PdfPageBoxInformation.Read(imposed);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Equal(
            new PdfPageBoxBounds(0, 0, 792, 612), page.MediaBox));
        Assert.Equal("Sheet 1 front", new PdfPageContentReader(imposed).Read(0).Text);
        Assert.Equal(PdfDuplexMode.DuplexFlipShortEdge,
            PdfDocumentInformation.Read(imposed).InitialView.ViewerPreferences.Duplex);
        Assert.Throws<ArgumentException>(() => PdfImpositionMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
    }

    [Fact]
    public void BookletStepRoundTripsBoundedSignaturesAndExportsDuplexSheets()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 10; page++) builder.AddBlankPage(200, 300);
        byte[] source = builder.Build();
        var preset = new PdfImpositionPreset(
            "Booklet", 2, 1, 792, 612, margin: 18, gutter: 12,
            duplex: true, includePageInformation: true);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro(
            "Booklet", [PdfImpositionMacro.BookletStep(preset, 8)]).ToJson());

        PdfMacroStep step = Assert.Single(macro.Steps);
        PdfDocument imposed = PdfDocument.Open(
            PdfImpositionMacro.Execute(step, source));

        Assert.Equal(PdfMacroOperation.ImposeBooklet, step.Operation);
        Assert.Equal(6, PdfPageBoxInformation.Read(imposed).Count);
        Assert.Equal(PdfDuplexMode.DuplexFlipLongEdge,
            PdfDocumentInformation.Read(imposed).InitialView.ViewerPreferences.Duplex);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpositionMacro.BookletStep(
            preset with { }, 6));
        Assert.Throws<ArgumentException>(() => PdfImpositionMacro.BookletStep(
            new PdfImpositionPreset("Simplex", 2, 1, 792, 612), 8));
    }

    [Fact]
    public void StepAndRepeatRoundTripsAndExportsRequestedCopies()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(200, 300)
            .AddBlankPage(300, 200)
            .Build();
        var preset = new PdfImpositionPreset(
            "Labels", 2, 1, 792, 612, duplex: true,
            includePageInformation: true);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Repeat",
            [PdfImpositionMacro.StepAndRepeatStep(preset, 1, 5)]).ToJson());

        PdfMacroStep step = Assert.Single(macro.Steps);
        PdfDocument imposed = PdfDocument.Open(
            PdfImpositionMacro.Execute(step, source));

        Assert.Equal(PdfMacroOperation.ImposeStepAndRepeat, step.Operation);
        Assert.Equal(3, PdfPageBoxInformation.Read(imposed).Count);
        Assert.Equal("Sheet 1 front", new PdfPageContentReader(imposed).Read(0).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionMacro.Execute(
                PdfImpositionMacro.StepAndRepeatStep(preset, 2, 1), source));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionMacro.StepAndRepeatStep(preset, 0, -1));
    }

    [Fact]
    public void CutStackStepRoundTripsAndExportsSimplexSheets()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 5; page++) builder.AddBlankPage(200, 300);
        byte[] source = builder.Build();
        var preset = new PdfImpositionPreset(
            "Cut stack", 2, 2, 792, 612, margin: 18, gutter: 12,
            includePageInformation: true);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Cut stack",
            [PdfImpositionMacro.CutStackStep(preset)]).ToJson());

        PdfMacroStep step = Assert.Single(macro.Steps);
        PdfDocument imposed = PdfDocument.Open(
            PdfImpositionMacro.Execute(step, source));

        Assert.Equal(PdfMacroOperation.ImposeCutStack, step.Operation);
        Assert.Equal(2, PdfPageBoxInformation.Read(imposed).Count);
        Assert.Equal("Sheet 1 front", new PdfPageContentReader(imposed).Read(0).Text);
        Assert.Equal(PdfDuplexMode.Simplex,
            PdfDocumentInformation.Read(imposed).InitialView.ViewerPreferences.Duplex);
        var duplexPreset = new PdfImpositionPreset(
            "Duplex cut stack", 2, 2, 792, 612, margin: 18, gutter: 12,
            duplex: true, includePageInformation: true);
        Assert.Throws<ArgumentException>(() => PdfImpositionMacro.CutStackStep(
            duplexPreset));
        Assert.Throws<ArgumentException>(() => PdfImpositionMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.ImposeCutStack,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["preset"] = duplexPreset.ToJson()
                }), source));
    }

    [Fact]
    public void ManualSequenceStepRoundTripsOrderBlanksAndDuplexSides()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 300, Text("One"))
            .AddPage(200, 300, Text("Two"))
            .AddPage(200, 300, Text("Three"))
            .AddPage(200, 300, Text("Four"))
            .AddPage(200, 300, Text("Five"))
            .Build();
        var preset = new PdfImpositionPreset(
            "Manual", 3, 1, 792, 612, duplex: true);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Manual",
            [PdfImpositionMacro.ManualSequenceStep(
                preset, [4, null, 0, 3, 1])]).ToJson());

        PdfMacroStep step = Assert.Single(macro.Steps);
        PdfDocument imposed = PdfDocument.Open(
            PdfImpositionMacro.Execute(step, source));
        var reader = new PdfPageContentReader(imposed);

        Assert.Equal(PdfMacroOperation.ImposeManualSequence, step.Operation);
        Assert.Equal(2, PdfPageBoxInformation.Read(imposed).Count);
        Assert.Equal("Five One", reader.Read(0).Text);
        Assert.Equal("Four Two", reader.Read(1).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionMacro.ManualSequenceStep(preset, [0, -1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpositionMacro.Execute(
            PdfImpositionMacro.ManualSequenceStep(preset, [5]), source));

        static PdfContentStreamBuilder Text(string value) =>
            new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 10).MoveText(20, 150)
                .ShowLatin1Text(value).EndText();
    }
}
