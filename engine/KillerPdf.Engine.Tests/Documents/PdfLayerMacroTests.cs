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
    public void MacroAssignsWholePageContentToNamedLayer()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 50).ShowLatin1Text("Visible").EndText())
            .Build());
        ReadOnlyMemory<byte> source = PdfOptionalContentEditor.AddGroup(
            original, "Review", initiallyVisible: false);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Assign", [
            PdfLayerMacro.PageContentStep("Review", 0)
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfDocument flattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(PdfDocument.Open(source)));

        Assert.Equal(string.Empty, new PdfPageContentReader(flattened).Read(0).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfLayerMacro.PageContentStep("Review", -1));
    }

    [Fact]
    public void MacroAssignsInstructionRangeToNamedLayer()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 70).ShowLatin1Text("Keep").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 40).ShowLatin1Text("Drop").EndText())
            .Build());
        ReadOnlyMemory<byte> source = PdfOptionalContentEditor.AddGroup(
            original, "Hidden", initiallyVisible: false);
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
            new PdfPageContentReader(PdfDocument.Open(source)).ReadInstructions(0);
        int start = instructions.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Operator == "BT").Skip(1).Single().index;
        int end = instructions.Select((instruction, index) => (instruction, index))
            .First(item => item.index > start && item.instruction.Operator == "ET").index;
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Range", [
            PdfLayerMacro.InstructionRangeStep("Hidden", 0, start, end - start + 1)
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfDocument flattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(PdfDocument.Open(source)));

        Assert.Equal("Keep", new PdfPageContentReader(flattened).Read(0).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfLayerMacro.InstructionRangeStep("Hidden", 0, 0, 0));
    }

    [Fact]
    public void MacroAssignsAnnotationToNamedLayer()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(100, 100)
            .AddUriLink(0, 10, 10, 20, 10, "https://example.com")
            .Build());
        int annotationObjectNumber = Assert.Single(
            PdfLinkReader.ReadPage(original, 0)).ObjectNumber!.Value;
        ReadOnlyMemory<byte> source = PdfOptionalContentEditor.AddGroup(
            original, "Hidden", initiallyVisible: false);
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Annotation", [
            PdfLayerMacro.AnnotationStep("Hidden", annotationObjectNumber)
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfDocument flattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(PdfDocument.Open(source)));

        Assert.Empty(PdfLinkReader.ReadPage(flattened, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfLayerMacro.AnnotationStep("Hidden", 0));
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

    [Fact]
    public void MacroSetsDefaultConfigurationBaseState()
    {
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginOptionalContent(
                new PdfOptionalContentGroup("Artwork"))
                .Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Base state", [
            PdfLayerMacro.BaseStateStep(PdfOptionalContentBaseState.Off)
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);

        Assert.Equal(PdfOptionalContentBaseState.Off,
            PdfOptionalContentReader.Read(PdfDocument.Open(source))
                .Configurations.Single().BaseState);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfLayerMacro.BaseStateStep((PdfOptionalContentBaseState)99));
    }

    [Fact]
    public void MacroReordersEveryLayerByStableName()
    {
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder()
                .BeginOptionalContent(new PdfOptionalContentGroup("Artwork"))
                .Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(new PdfOptionalContentGroup("Notes"))
                .Rectangle(20, 0, 10, 10).Fill().EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Order", [
            PdfLayerMacro.DisplayOrderStep(["Notes", "Artwork"])
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(PdfDocument.Open(source));
        Dictionary<int, string> names = info.Groups.ToDictionary(
            group => group.ObjectNumber, group => group.Name);

        Assert.Equal(["Notes", "Artwork"], info.Configurations.Single()
            .DisplayOrderGroupObjectNumbers.Select(number => names[number]));
        Assert.Throws<ArgumentException>(() =>
            PdfLayerMacro.DisplayOrderStep(["Artwork", "Artwork"]));
    }

    [Fact]
    public void MacroSavesNestedDisplayOrderByStableName()
    {
        ReadOnlyMemory<byte> source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder()
                .BeginOptionalContent(new PdfOptionalContentGroup("Artwork"))
                .Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(new PdfOptionalContentGroup("Notes"))
                .Rectangle(20, 0, 10, 10).Fill().EndMarkedContent()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Folders", [
            PdfLayerMacro.DisplayOrderTreeStep([
                PdfLayerOrderItem.Folder("Production",
                    PdfLayerOrderItem.Layer("Notes"),
                    PdfLayerOrderItem.Layer("Artwork"))
            ])
        ]).ToJson());

        source = PdfLayerMacro.Execute(Assert.Single(macro.Steps), source);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(PdfDocument.Open(source));
        Dictionary<int, string> names = info.Groups.ToDictionary(
            group => group.ObjectNumber, group => group.Name);

        Assert.Equal(["Notes", "Artwork"], info.Configurations.Single()
            .DisplayOrderGroupObjectNumbers.Select(number => names[number]));
        Assert.Throws<ArgumentException>(() => PdfLayerMacro.DisplayOrderTreeStep([
            PdfLayerOrderItem.Folder("Duplicate",
                PdfLayerOrderItem.Layer("Artwork"),
                PdfLayerOrderItem.Layer("Artwork"))
        ]));
    }
}
