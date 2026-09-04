using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOptionalContentReaderTests
{
    [Fact]
    public void ReadReturnsNamedGroupsAndDefaultVisibility()
    {
        var hidden = new PdfOptionalContentGroup("Measurements", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup(
            "Artwork", visibleWhenPrinting: false, visibleWhenExporting: true);
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(hidden).Rectangle(0, 0, 10, 10).Stroke().EndMarkedContent()
            .BeginOptionalContent(visible).Rectangle(20, 20, 10, 10).Fill().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());

        PdfOptionalContentInfo result = PdfOptionalContentReader.Read(document);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal("Artwork", result.Groups[0].Name);
        Assert.False(result.Groups[0].IsVisibleWhenPrinting);
        Assert.True(result.Groups[0].IsVisibleWhenExporting);
        Assert.True(result.Groups[0].IsInitiallyVisible);
        Assert.Equal("Measurements", result.Groups[1].Name);
        Assert.False(result.Groups[1].IsInitiallyVisible);
        PdfOptionalContentConfigurationInfo configuration = Assert.Single(result.Configurations);
        Assert.True(configuration.IsDefault);
        Assert.Equal(PdfOptionalContentBaseState.On, configuration.BaseState);
        Assert.Contains(result.Groups[0].ObjectNumber, configuration.VisibleGroupObjectNumbers);
        Assert.DoesNotContain(result.Groups[1].ObjectNumber, configuration.VisibleGroupObjectNumbers);
        Assert.Equal(result.Groups.Select(group => group.ObjectNumber),
            configuration.DisplayOrderGroupObjectNumbers);
        using JsonDocument json = JsonDocument.Parse(result.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("Artwork",
            json.RootElement.GetProperty("groups")[0].GetProperty("name").GetString());
        Assert.Equal("on", json.RootElement.GetProperty("configurations")[0]
            .GetProperty("baseState").GetString());
        string text = result.ToText();
        Assert.Contains("Layers: 2", text, StringComparison.Ordinal);
        Assert.Contains("Artwork (object ", text, StringComparison.Ordinal);
        Assert.Contains("visible, unlocked, print hidden, export visible", text,
            StringComparison.Ordinal);
        Assert.Contains("Measurements (object ", text, StringComparison.Ordinal);
        Assert.Contains("hidden, unlocked, print unspecified, export unspecified", text,
            StringComparison.Ordinal);
        Assert.Contains("Configurations: 1", text, StringComparison.Ordinal);
        Assert.Contains(", base state On", text, StringComparison.Ordinal);
        Assert.Contains("Display order:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReturnsEmptyModelWhenDocumentHasNoLayers()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfOptionalContentInfo result = PdfOptionalContentReader.Read(document);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Configurations);
        Assert.Equal("Layers: 0\r\nConfigurations: 0", result.ToText());
    }

    [Fact]
    public void RenameGroupPreservesLayerStateAndOriginalDocument()
    {
        var layer = new PdfOptionalContentGroup("Original", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        PdfOptionalContentGroupInfo originalGroup = Assert.Single(
            PdfOptionalContentReader.Read(original).Groups);

        PdfDocument renamed = PdfDocument.Open(PdfOptionalContentEditor.RenameGroup(
            original, originalGroup.ObjectNumber, "Résumé"));
        PdfOptionalContentGroupInfo renamedGroup = Assert.Single(
            PdfOptionalContentReader.Read(renamed).Groups);

        Assert.Equal("Résumé", renamedGroup.Name);
        Assert.False(renamedGroup.IsInitiallyVisible);
        Assert.True(renamedGroup.IsVisibleWhenPrinting);
        Assert.False(renamedGroup.IsVisibleWhenExporting);
        Assert.Equal("Original", Assert.Single(PdfOptionalContentReader.Read(original).Groups).Name);
    }

    [Fact]
    public void UsageVisibilityCanBeChangedAndClearedIndependently()
    {
        var layer = new PdfOptionalContentGroup("Artwork",
            visibleWhenPrinting: false, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetPrintVisibility(original, objectNumber, true));
        PdfOptionalContentGroupInfo changedGroup = Assert.Single(
            PdfOptionalContentReader.Read(changed).Groups);
        Assert.True(changedGroup.IsVisibleWhenPrinting);
        Assert.False(changedGroup.IsVisibleWhenExporting);

        PdfDocument cleared = PdfDocument.Open(
            PdfOptionalContentEditor.SetPrintVisibility(changed, objectNumber, null));
        PdfOptionalContentGroupInfo clearedGroup = Assert.Single(
            PdfOptionalContentReader.Read(cleared).Groups);
        Assert.Null(clearedGroup.IsVisibleWhenPrinting);
        Assert.False(clearedGroup.IsVisibleWhenExporting);
    }

    [Fact]
    public void InitialVisibilityCanBeChangedWithoutChangingUsageState()
    {
        var layer = new PdfOptionalContentGroup("Artwork", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument visible = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, objectNumber, true));
        PdfOptionalContentGroupInfo visibleGroup = Assert.Single(
            PdfOptionalContentReader.Read(visible).Groups);
        Assert.True(visibleGroup.IsInitiallyVisible);
        Assert.True(visibleGroup.IsVisibleWhenPrinting);
        Assert.False(visibleGroup.IsVisibleWhenExporting);

        PdfDocument hidden = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(visible, objectNumber, false));
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(hidden).Groups)
            .IsInitiallyVisible);
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(original).Groups)
            .IsInitiallyVisible);
    }

    [Fact]
    public void LayerLockCanBeEnabledAndDisabled()
    {
        var layer = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(original).Groups).ObjectNumber;

        PdfDocument locked = PdfDocument.Open(
            PdfOptionalContentEditor.SetLocked(original, objectNumber, true));
        Assert.True(Assert.Single(PdfOptionalContentReader.Read(locked).Groups).IsLocked);

        PdfDocument unlocked = PdfDocument.Open(
            PdfOptionalContentEditor.SetLocked(locked, objectNumber, false));
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(unlocked).Groups).IsLocked);
        Assert.False(Assert.Single(PdfOptionalContentReader.Read(original).Groups).IsLocked);
    }

    [Fact]
    public void DisplayOrderCanBeReplaced()
    {
        var first = new PdfOptionalContentGroup("First");
        var second = new PdfOptionalContentGroup("Second");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(first).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(second).Rectangle(20, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        int[] reversed = [.. before.Configurations.Single()
            .DisplayOrderGroupObjectNumbers.Reverse()];

        PdfDocument reordered = PdfDocument.Open(
            PdfOptionalContentEditor.SetDisplayOrder(original, reversed));

        Assert.Equal(reversed, PdfOptionalContentReader.Read(reordered)
            .Configurations.Single().DisplayOrderGroupObjectNumbers);
        Assert.Throws<ArgumentException>(() =>
            PdfOptionalContentEditor.SetDisplayOrder(original, [reversed[0]]));
    }

    [Fact]
    public void DisplayOrderCanBeSavedAsNestedNamedFolders()
    {
        var first = new PdfOptionalContentGroup("First");
        var second = new PdfOptionalContentGroup("Second");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(first).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(second).Rectangle(20, 0, 10, 10).Fill().EndMarkedContent())
            .Build());
        int[] groups = [.. PdfOptionalContentReader.Read(original).Groups
            .Select(group => group.ObjectNumber)];

        PdfDocument grouped = PdfDocument.Open(PdfOptionalContentEditor.SetDisplayOrderTree(
            original,
            [PdfOptionalContentOrderItem.Folder("Press layers",
                PdfOptionalContentOrderItem.Layer(groups[1]),
                PdfOptionalContentOrderItem.Layer(groups[0]))]));

        Assert.Equal(groups.Reverse(), PdfOptionalContentReader.Read(grouped)
            .Configurations.Single().DisplayOrderGroupObjectNumbers);
        Assert.Throws<ArgumentException>(() => PdfOptionalContentEditor.SetDisplayOrderTree(
            original, [PdfOptionalContentOrderItem.Layer(groups[0])]));
    }

    [Fact]
    public void DefaultConfigurationMetadataCanBeSetAndCleared()
    {
        var layer = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent())
            .Build());

        PdfDocument named = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultConfigurationMetadata(
                original, "Press review", "KillerPDF"));
        PdfOptionalContentConfigurationInfo configuration =
            Assert.Single(PdfOptionalContentReader.Read(named).Configurations);

        Assert.Equal("Press review", configuration.Name);
        Assert.Equal("KillerPDF", configuration.Creator);
        PdfDocument cleared = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultConfigurationMetadata(named, null, null));
        PdfOptionalContentConfigurationInfo clearedConfiguration =
            Assert.Single(PdfOptionalContentReader.Read(cleared).Configurations);
        Assert.Null(clearedConfiguration.Name);
        Assert.Null(clearedConfiguration.Creator);
    }

    [Fact]
    public void DefaultBaseStateCanBeChangedWithoutMutatingTheSource()
    {
        var visible = new PdfOptionalContentGroup("Visible");
        var hidden = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(visible).Rectangle(0, 0, 10, 10).Fill().EndMarkedContent()
                .BeginOptionalContent(hidden).Rectangle(20, 0, 10, 10).Fill().EndMarkedContent())
            .Build());

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultBaseState(
                original, PdfOptionalContentBaseState.Off));
        PdfOptionalContentConfigurationInfo configuration =
            Assert.Single(PdfOptionalContentReader.Read(changed).Configurations);

        Assert.Equal(PdfOptionalContentBaseState.Off, configuration.BaseState);
        Assert.Empty(configuration.VisibleGroupObjectNumbers);
        Assert.Single(PdfOptionalContentReader.Read(original).Configurations);
        Assert.Equal(PdfOptionalContentBaseState.On,
            PdfOptionalContentReader.Read(original).Configurations[0].BaseState);
    }

    [Fact]
    public void GroupCanBeCreatedInDocumentWithoutLayers()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());

        PdfDocument changed = PdfDocument.Open(PdfOptionalContentEditor.AddGroup(
            original, "Review", initiallyVisible: false, locked: true,
            printVisible: true, exportVisible: false));
        PdfOptionalContentGroupInfo group = Assert.Single(
            PdfOptionalContentReader.Read(changed).Groups);

        Assert.Equal("Review", group.Name);
        Assert.False(group.IsInitiallyVisible);
        Assert.True(group.IsLocked);
        Assert.True(group.IsVisibleWhenPrinting);
        Assert.False(group.IsVisibleWhenExporting);
        Assert.Empty(PdfOptionalContentReader.Read(original).Groups);
    }

    [Fact]
    public void GroupCanBeAppendedWithoutChangingExistingConfigurationMetadata()
    {
        var artwork = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(artwork).Rectangle(0, 0, 10, 10).Fill()
                .EndMarkedContent()).Build());
        PdfDocument named = PdfDocument.Open(
            PdfOptionalContentEditor.SetDefaultConfigurationMetadata(
                original, "Press review", "KillerPDF"));

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.AddGroup(named, "Notes"));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(changed);

        Assert.Equal(["Artwork", "Notes"], info.Groups.Select(group => group.Name));
        Assert.Equal("Press review", info.Configurations.Single().Name);
        Assert.Equal("KillerPDF", info.Configurations.Single().Creator);
        Assert.Equal(info.Groups.Select(group => group.ObjectNumber),
            info.Configurations.Single().DisplayOrderGroupObjectNumbers);
        Assert.Throws<ArgumentException>(() =>
            PdfOptionalContentEditor.AddGroup(changed, "Notes"));
    }

    [Fact]
    public void GroupDefinitionCanBeDuplicatedWithIndependentIdentityAndSettings()
    {
        var sourceLayer = new PdfOptionalContentGroup("Source",
            initiallyVisible: false, visibleWhenPrinting: true,
            visibleWhenExporting: false);
        PdfDocument unlocked = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(sourceLayer).Rectangle(0, 0, 10, 10).Fill()
                .EndMarkedContent()).Build());
        int sourceObjectNumber = Assert.Single(
            PdfOptionalContentReader.Read(unlocked).Groups).ObjectNumber;
        PdfDocument original = PdfDocument.Open(PdfOptionalContentEditor.SetLocked(
            unlocked, sourceObjectNumber, true));
        PdfOptionalContentGroupInfo source = Assert.Single(
            PdfOptionalContentReader.Read(original).Groups);

        PdfDocument changed = PdfDocument.Open(PdfOptionalContentEditor.DuplicateGroup(
            original, source.ObjectNumber, "Copy"));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(changed);
        PdfOptionalContentGroupInfo copy = info.Groups.Single(group => group.Name == "Copy");

        Assert.NotEqual(source.ObjectNumber, copy.ObjectNumber);
        Assert.False(copy.IsInitiallyVisible);
        Assert.True(copy.IsLocked);
        Assert.True(copy.IsVisibleWhenPrinting);
        Assert.False(copy.IsVisibleWhenExporting);
        Assert.Equal(["Source", "Copy"], info.Configurations.Single()
            .DisplayOrderGroupObjectNumbers.Select(number =>
                info.Groups.Single(group => group.ObjectNumber == number).Name));
        Assert.Throws<ArgumentException>(() =>
            PdfOptionalContentEditor.DuplicateGroup(changed, source.ObjectNumber, "Copy"));
    }

    [Fact]
    public void UnusedGroupCanBeRemovedButReferencedGroupIsProtected()
    {
        var artwork = new PdfOptionalContentGroup("Artwork");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(artwork).Rectangle(0, 0, 10, 10).Fill()
                .EndMarkedContent()).Build());
        PdfDocument withUnused = PdfDocument.Open(
            PdfOptionalContentEditor.AddGroup(original, "Unused",
                initiallyVisible: false, locked: true));
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(withUnused);
        int unusedObjectNumber = before.Groups.Single(group => group.Name == "Unused")
            .ObjectNumber;
        int artworkObjectNumber = before.Groups.Single(group => group.Name == "Artwork")
            .ObjectNumber;
        PdfDocument nested = PdfDocument.Open(PdfOptionalContentEditor.SetDisplayOrderTree(
            withUnused,
            [PdfOptionalContentOrderItem.Layer(artworkObjectNumber),
                PdfOptionalContentOrderItem.Folder("Temporary",
                    PdfOptionalContentOrderItem.Layer(unusedObjectNumber))]));

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.RemoveUnusedGroup(nested, unusedObjectNumber));
        PdfOptionalContentInfo after = PdfOptionalContentReader.Read(changed);

        Assert.Equal("Artwork", Assert.Single(after.Groups).Name);
        Assert.Equal([artworkObjectNumber], after.Configurations.Single()
            .DisplayOrderGroupObjectNumbers);
        Assert.Throws<InvalidOperationException>(() =>
            PdfOptionalContentEditor.RemoveUnusedGroup(nested, artworkObjectNumber));
    }

    [Fact]
    public void RemovingOnlyUnusedGroupRemovesOptionalContentProperties()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument layered = PdfDocument.Open(
            PdfOptionalContentEditor.AddGroup(original, "Empty"));
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(layered).Groups)
            .ObjectNumber;

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.RemoveUnusedGroup(layered, objectNumber));

        Assert.Empty(PdfOptionalContentReader.Read(changed).Groups);
    }

    [Fact]
    public void WholePageContentCanBeAssignedToARegisteredLayer()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 50).ShowLatin1Text("Visible").EndText())
            .Build());
        PdfDocument layered = PdfDocument.Open(
            PdfOptionalContentEditor.AddGroup(original, "Review"));
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(layered).Groups)
            .ObjectNumber;

        PdfDocument assigned = PdfDocument.Open(
            PdfOptionalContentEditor.SetPageContentGroup(layered, 0, objectNumber));
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
            new PdfPageContentReader(assigned).ReadInstructions(0);

        Assert.Equal("Visible", new PdfPageContentReader(assigned).Read(0).Text);
        Assert.Equal("BDC", instructions[0].Operator);
        Assert.Equal("OC", Assert.IsType<PdfName>(instructions[0].Operands[0])
            .ValueAsLatin1());
        Assert.Equal("EMC", instructions[^1].Operator);
        Assert.Throws<InvalidOperationException>(() =>
            PdfOptionalContentEditor.RemoveUnusedGroup(assigned, objectNumber));
    }

    [Fact]
    public void TopLevelInstructionRangeCanBeAssignedAndFlattened()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 70).ShowLatin1Text("First").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 40).ShowLatin1Text("Second").EndText())
            .Build());
        PdfDocument layered = PdfDocument.Open(PdfOptionalContentEditor.AddGroup(
            original, "Hidden", initiallyVisible: false));
        int objectNumber = Assert.Single(PdfOptionalContentReader.Read(layered).Groups)
            .ObjectNumber;
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
            new PdfPageContentReader(layered).ReadInstructions(0);
        int secondTextStart = instructions.Select((instruction, index) =>
                (instruction, index))
            .Where(item => item.instruction.Operator == "BT")
            .Skip(1).Single().index;
        int secondTextEnd = instructions.Select((instruction, index) =>
                (instruction, index))
            .First(item => item.index > secondTextStart
                && item.instruction.Operator == "ET").index;

        PdfDocument assigned = PdfDocument.Open(
            PdfOptionalContentEditor.SetPageInstructionRangeGroup(layered, 0,
                secondTextStart, secondTextEnd - secondTextStart + 1, objectNumber));
        PdfDocument flattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(assigned));

        Assert.Equal("First", new PdfPageContentReader(flattened).Read(0).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOptionalContentEditor.SetPageInstructionRangeGroup(
                layered, 0, secondTextStart, 0, objectNumber));
    }

    [Fact]
    public void PageLayersFlattenToTheSelectedVisibleResult()
    {
        var visibleLayer = new PdfOptionalContentGroup("Visible");
        var hiddenLayer = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        PdfContentStreamBuilder content = new PdfContentStreamBuilder()
            .BeginOptionalContent(visibleLayer)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 70).ShowLatin1Text("Keep").EndText()
            .EndMarkedContent()
            .BeginOptionalContent(hiddenLayer)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 40).ShowLatin1Text("Drop").EndText()
            .EndMarkedContent();
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        int hiddenObjectNumber = before.Groups.Single(group => group.Name == "Hidden")
            .ObjectNumber;

        PdfDocument defaultResult = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(original));
        PdfDocument explicitResult = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(
                original, [hiddenObjectNumber]));

        Assert.Equal("Keep", new PdfPageContentReader(defaultResult).Read(0).Text);
        Assert.Equal("Drop", new PdfPageContentReader(explicitResult).Read(0).Text);
        Assert.Empty(PdfOptionalContentReader.Read(defaultResult).Groups);
        Assert.DoesNotContain(new PdfPageContentReader(defaultResult).ReadInstructions(0),
            instruction => instruction.Operator is "BDC" or "EMC");
    }

    [Fact]
    public void FlatteningReportsOptionalContentInsideFormXObjects()
    {
        var layer = new PdfOptionalContentGroup("Nested");
        var form = new PdfFormXObject(20, 20, new PdfContentStreamBuilder()
            .BeginOptionalContent(layer).Rectangle(0, 0, 20, 20).Fill()
            .EndMarkedContent());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .DrawForm(form, 10, 10)).Build());

        Assert.Throws<NotSupportedException>(() =>
            PdfOptionalContentEditor.FlattenPageContent(document));
    }

    [Fact]
    public void MembershipPoliciesAndExpressionsFlattenToTheirSelectedResult()
    {
        PdfDocument policy = MembershipDocument("/OCGs [5 0 R 6 0 R] /P /AllOn");
        PdfDocument expression = MembershipDocument("/VE [/Or 5 0 R 6 0 R]");
        int[] allVisible = PdfOptionalContentReader.Read(policy).Groups
            .Select(group => group.ObjectNumber).ToArray();

        PdfDocument hidden = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(policy));
        PdfDocument visibleByPolicy = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(policy, allVisible));
        PdfDocument visibleByExpression = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(expression));

        Assert.Empty(new PdfPageContentReader(hidden).Read(0).Paths);
        Assert.Empty(PdfLinkReader.ReadPage(hidden, 0));
        Assert.Single(new PdfPageContentReader(visibleByPolicy).Read(0).Paths);
        Assert.Single(PdfLinkReader.ReadPage(visibleByPolicy, 0));
        Assert.Single(new PdfPageContentReader(visibleByExpression).Read(0).Paths);
        Assert.Single(PdfLinkReader.ReadPage(visibleByExpression, 0));
        Assert.Empty(PdfOptionalContentReader.Read(visibleByExpression).Groups);
    }

    [Fact]
    public void PageContentAndAnnotationsCanBeMergedIntoAnotherLayer()
    {
        var sourceLayer = new PdfOptionalContentGroup("Source");
        var targetLayer = new PdfOptionalContentGroup("Target");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(sourceLayer)
                    .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(10, 50).ShowLatin1Text("Content").EndText()
                .EndMarkedContent()
                .BeginOptionalContent(targetLayer).Rectangle(0, 0, 5, 5).Fill()
                .EndMarkedContent())
            .AddUriLink(0, 10, 10, 20, 10, "https://example.com")
            .Build());
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        int sourceObjectNumber = before.Groups.Single(group => group.Name == "Source")
            .ObjectNumber;
        int targetObjectNumber = before.Groups.Single(group => group.Name == "Target")
            .ObjectNumber;
        int annotationObjectNumber = Assert.Single(PdfLinkReader.ReadPage(original, 0))
            .ObjectNumber!.Value;
        PdfDocument assigned = PdfDocument.Open(PdfOptionalContentEditor.SetAnnotationGroup(
            original, annotationObjectNumber, sourceObjectNumber));

        PdfDocument merged = PdfDocument.Open(PdfOptionalContentEditor.MergeGroups(
            assigned, sourceObjectNumber, targetObjectNumber));
        PdfOptionalContentGroupInfo remaining = Assert.Single(
            PdfOptionalContentReader.Read(merged).Groups);
        PdfDictionary annotation = Assert.IsType<PdfDictionary>(merged.Resolve(
            new PdfIndirectReference(annotationObjectNumber, 0)));

        Assert.Equal("Target", remaining.Name);
        Assert.Equal("Content", new PdfPageContentReader(merged).Read(0).Text);
        Assert.Equal(targetObjectNumber, Assert.IsType<PdfIndirectReference>(
            annotation[new PdfName("OC"u8)]).ObjectNumber);
        Assert.Throws<ArgumentException>(() => PdfOptionalContentEditor.MergeGroups(
            original, sourceObjectNumber, sourceObjectNumber));
    }

    [Fact]
    public void AnnotationCanBeAssignedToLayerAndCleared()
    {
        var review = new PdfOptionalContentGroup("Review");
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(review).Rectangle(0, 0, 10, 10).Fill()
                .EndMarkedContent())
            .AddUriLink(0, 10, 10, 20, 10, "https://example.com")
            .Build());
        int groupObjectNumber = Assert.Single(
            PdfOptionalContentReader.Read(original).Groups).ObjectNumber;
        int annotationObjectNumber = Assert.Single(
            PdfLinkReader.ReadPage(original, 0)).ObjectNumber!.Value;

        PdfDocument assigned = PdfDocument.Open(PdfOptionalContentEditor.SetAnnotationGroup(
            original, annotationObjectNumber, groupObjectNumber));
        PdfDictionary annotation = Assert.IsType<PdfDictionary>(assigned.Resolve(
            new PdfIndirectReference(annotationObjectNumber, 0)));
        PdfIndirectReference layer = Assert.IsType<PdfIndirectReference>(
            annotation[new PdfName("OC"u8)]);
        Assert.Equal(groupObjectNumber, layer.ObjectNumber);
        PdfDocument visibleFlattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(assigned));
        PdfDocument hiddenFlattened = PdfDocument.Open(
            PdfOptionalContentEditor.FlattenPageContent(assigned, []));
        Assert.False(Assert.IsType<PdfDictionary>(visibleFlattened.Resolve(
                new PdfIndirectReference(annotationObjectNumber, 0)))
            .ContainsKey(new PdfName("OC"u8)));
        Assert.Single(PdfLinkReader.ReadPage(visibleFlattened, 0));
        Assert.Empty(PdfLinkReader.ReadPage(hiddenFlattened, 0));

        PdfDocument cleared = PdfDocument.Open(PdfOptionalContentEditor.SetAnnotationGroup(
            assigned, annotationObjectNumber, null));
        PdfDictionary clearedAnnotation = Assert.IsType<PdfDictionary>(cleared.Resolve(
            new PdfIndirectReference(annotationObjectNumber, 0)));
        Assert.False(clearedAnnotation.ContainsKey(new PdfName("OC"u8)));
        Assert.False(Assert.IsType<PdfDictionary>(original.Resolve(
            new PdfIndirectReference(annotationObjectNumber, 0)))
            .ContainsKey(new PdfName("OC"u8)));
    }

    private static PdfDocument MembershipDocument(string membershipEntries)
    {
        const string content = "/OC /LayerSet BDC 0 0 10 10 re f EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Properties << /LayerSet 7 0 R >> >> /Contents 4 0 R /Annots [8 0 R] >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /OCG /Name (Visible) >>",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /OCMD {membershipEntries} >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 30 20] /A << /S /URI /URI (https://example.com) >> /OC 7 0 R >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
