using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLayerMacroTests
{
    [Fact]
    public void MacroRoundTripsAndFlattensExplicitVisibleLayerNames()
    {
        var hidden = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup("Visible");
        byte[] source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder()
                .BeginOptionalContent(hidden).BeginText()
                    .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 20)
                    .ShowLatin1Text("Hidden text").EndText().EndMarkedContent()
                .BeginOptionalContent(visible).BeginText()
                    .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 40)
                    .ShowLatin1Text("Visible text").EndText().EndMarkedContent()).Build();
        var macro = new PdfMacro("Layers", [PdfLayerMacro.FlattenStep(["Hidden"])]);
        PdfMacroStep step = Assert.Single(PdfMacro.FromJson(macro.ToJson()).Steps);

        PdfDocument output = PdfDocument.Open(PdfLayerMacro.Execute(step, source));

        Assert.Equal("Hidden text", new PdfPageContentReader(output).Read(0).Text);
        Assert.Empty(PdfOptionalContentReader.Read(output).Groups);
        Assert.Throws<ArgumentException>(() => PdfLayerMacro.Execute(
            PdfLayerMacro.FlattenStep(["Missing"]), source));
    }

    [Fact]
    public void MacroEditsLayerNamesVisibilityLocksAndMergesByStableName()
    {
        var artwork = new PdfOptionalContentGroup("Artwork");
        var notes = new PdfOptionalContentGroup("Notes", initiallyVisible: false);
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder()
                .BeginOptionalContent(artwork).Rectangle(0, 0, 10, 10).Fill()
                    .EndMarkedContent()
                .BeginOptionalContent(notes).Rectangle(20, 0, 10, 10).Fill()
                    .EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Edit layers",
        [
            PdfLayerMacro.RenameStep("Artwork", "Print"),
            PdfLayerMacro.VisibilityStep("Notes", true),
            PdfLayerMacro.LockStep("Notes", true),
            PdfLayerMacro.MergeStep("Print", "Notes")
        ]).ToJson());

        foreach (PdfMacroStep step in macro.Steps)
            source = PdfLayerMacro.Execute(step, source);
        PdfOptionalContentGroupInfo remaining = Assert.Single(
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Groups);

        Assert.Equal("Notes", remaining.Name);
        Assert.True(remaining.IsInitiallyVisible);
        Assert.True(remaining.IsLocked);
    }

    [Fact]
    public void MacroRemovesOnlyAnUnusedNamedLayer()
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        ReadOnlyMemory<byte> layered = PdfOptionalContentEditor.AddGroup(blank, "Temporary");

        ReadOnlyMemory<byte> output = PdfLayerMacro.Execute(
            PdfLayerMacro.RemoveUnusedStep("Temporary"), layered);

        Assert.Empty(PdfOptionalContentReader.Read(PdfDocument.Open(output)).Groups);
        Assert.Throws<ArgumentException>(() => PdfLayerMacro.Execute(
            PdfLayerMacro.RenameStep("Missing", "Other"), output));
    }

    [Fact]
    public void MacroSetsAndClearsIndependentPrintAndExportVisibility()
    {
        var layer = new PdfOptionalContentGroup("Artwork");
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginOptionalContent(layer)
                .Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Usage", [
            PdfLayerMacro.PrintVisibilityStep("Artwork", false),
            PdfLayerMacro.ExportVisibilityStep("Artwork", true)
        ]).ToJson());

        foreach (PdfMacroStep step in macro.Steps)
            source = PdfLayerMacro.Execute(step, source);
        PdfOptionalContentGroupInfo changed = Assert.Single(
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Groups);
        Assert.False(changed.IsVisibleWhenPrinting);
        Assert.True(changed.IsVisibleWhenExporting);

        source = PdfLayerMacro.Execute(
            PdfLayerMacro.PrintVisibilityStep("Artwork", null), source);
        source = PdfLayerMacro.Execute(
            PdfLayerMacro.ExportVisibilityStep("Artwork", null), source);
        PdfOptionalContentGroupInfo cleared = Assert.Single(
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Groups);
        Assert.Null(cleared.IsVisibleWhenPrinting);
        Assert.Null(cleared.IsVisibleWhenExporting);
    }

    [Fact]
    public void MacroCreatesAndDuplicatesLayersWithSavedState()
    {
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Create layers", [
            PdfLayerMacro.CreateStep("Review", initiallyVisible: false,
                locked: true, printVisible: false, exportVisible: true),
            PdfLayerMacro.DuplicateStep("Review", "Review copy")
        ]).ToJson());

        foreach (PdfMacroStep step in macro.Steps)
            source = PdfLayerMacro.Execute(step, source);
        PdfOptionalContentGroupInfo[] groups = [..
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Groups];

        Assert.Equal(["Review", "Review copy"], groups.Select(group => group.Name));
        Assert.All(groups, group =>
        {
            Assert.False(group.IsInitiallyVisible);
            Assert.True(group.IsLocked);
            Assert.False(group.IsVisibleWhenPrinting);
            Assert.True(group.IsVisibleWhenExporting);
        });
    }

    [Fact]
    public void MacroSetsAndClearsDefaultConfigurationMetadata()
    {
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginOptionalContent(
                new PdfOptionalContentGroup("Artwork"))
                .Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Configuration", [
            PdfLayerMacro.ConfigurationMetadataStep("Press", "KillerPDF")
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfOptionalContentConfigurationInfo changed = Assert.Single(
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Configurations);

        Assert.Equal("Press", changed.Name);
        Assert.Equal("KillerPDF", changed.Creator);

        source = PdfLayerMacro.Execute(
            PdfLayerMacro.ConfigurationMetadataStep(null, null), source);
        PdfOptionalContentConfigurationInfo cleared = Assert.Single(
            PdfOptionalContentReader.Read(PdfDocument.Open(source)).Configurations);
        Assert.Null(cleared.Name);
        Assert.Null(cleared.Creator);
    }
}
