using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOptionalContentViewEditorTests
{
    [Fact]
    public void SetInitialVisibility_CopiesDirectViewApplicationsForExplicitVisibleState()
    {
        PdfDocument original = CreateDocument(indirectSharedConfiguration: false);

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 5, true));

        Assert.False(PdfOptionalContentReader.Read(original).Groups
            .Single(group => group.ObjectNumber == 5).IsInitiallyVisible);
        Assert.True(PdfOptionalContentReader.Read(changed).Groups
            .Single(group => group.ObjectNumber == 5).IsInitiallyVisible);
        Assert.Contains(5, PdfOptionalContentReader.Read(changed)
            .ViewVisibleGroupObjectNumbers);
        PdfArray applications = Applications(changed, DefaultConfiguration(changed));
        Assert.Equal(4, applications.Count);
        Assert.Equal([6], GroupNumbers(changed, Application(changed, applications[0])));
        Assert.Equal([5], GroupNumbers(changed, Application(changed, applications[1])));
        Assert.False(Application(changed, applications[2]).ContainsKey(Name("OCGs")));
        Assert.Empty(GroupNumbers(changed, Application(changed, applications[3])));
        Assert.Equal("View", Event(changed, Application(changed, applications[3])));
        Assert.Equal(["View", "Zoom"], Categories(changed, Application(changed, applications[3])));
        Assert.Equal([5, 6], GroupNumbers(original,
            Application(original, Applications(original, DefaultConfiguration(original))[0])));
    }

    [Fact]
    public void SetInitialVisibility_CopiesIndirectViewApplicationsAndSharedDefaultForExplicitHiddenState()
    {
        PdfDocument original = CreateDocument(indirectSharedConfiguration: true);

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 6, false));

        PdfOptionalContentInfo changedInfo = PdfOptionalContentReader.Read(changed);
        Assert.False(changedInfo.Groups.Single(group => group.ObjectNumber == 6)
            .IsInitiallyVisible);
        Assert.DoesNotContain(6, changedInfo.Configurations.Single(config => config.IsDefault)
            .VisibleGroupObjectNumbers);
        Assert.Contains(6, changedInfo.Configurations.Single(config => !config.IsDefault)
            .VisibleGroupObjectNumbers);
        Assert.DoesNotContain(6, changedInfo.ViewVisibleGroupObjectNumbers);

        PdfDictionary properties = Properties(changed);
        PdfIndirectReference alternateReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(ResolveArray(changed, properties[Name("Configs")])));
        Assert.Equal(7, alternateReference.ObjectNumber);
        PdfObject defaultValue = properties[Name("D")];
        if (defaultValue is PdfIndirectReference defaultReference)
            Assert.NotEqual(alternateReference, defaultReference);

        PdfArray editedApplications = Applications(changed, DefaultConfiguration(changed));
        Assert.Equal([5], GroupNumbers(changed,
            Application(changed, editedApplications[0])));
        Assert.Equal([5], GroupNumbers(changed,
            Application(changed, editedApplications[1])));
        Assert.False(Application(changed, editedApplications[2]).ContainsKey(Name("OCGs")));
        Assert.Equal([5], GroupNumbers(changed,
            Application(changed, editedApplications[3])));

        PdfArray alternateApplications = Applications(changed,
            ResolveDictionary(changed, alternateReference));
        Assert.Equal([5, 6], GroupNumbers(changed,
            Application(changed, alternateApplications[0])));
        Assert.Equal([5, 6], GroupNumbers(changed,
            ResolveDictionary(changed, new PdfIndirectReference(9, 0))));
        Assert.Equal([5, 6], ResolveArray(changed, new PdfIndirectReference(13, 0))
            .Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber));
    }

    [Fact]
    public void SetInitialVisibility_ExplicitOppositeViewChoicesSurviveReopen()
    {
        PdfDocument original = CreateDocument(indirectSharedConfiguration: false,
            includeMixedApplication: false);
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        Assert.Contains(5, before.ViewVisibleGroupObjectNumbers);
        Assert.DoesNotContain(6, before.ViewVisibleGroupObjectNumbers);

        PdfDocument hideLeft = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 5, false));
        PdfOptionalContentInfo hidden = PdfOptionalContentReader.Read(hideLeft);
        Assert.False(hidden.Groups.Single(group => group.ObjectNumber == 5)
            .IsInitiallyVisible);
        Assert.DoesNotContain(5, hidden.ViewVisibleGroupObjectNumbers);
        Assert.Equal([6], GroupNumbers(hideLeft,
            Application(hideLeft, Applications(hideLeft, DefaultConfiguration(hideLeft))[0])));

        PdfDocument showRight = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 6, true));
        PdfOptionalContentInfo shown = PdfOptionalContentReader.Read(showRight);
        Assert.True(shown.Groups.Single(group => group.ObjectNumber == 6)
            .IsInitiallyVisible);
        Assert.Contains(6, shown.ViewVisibleGroupObjectNumbers);
        Assert.Equal([5], GroupNumbers(showRight,
            Application(showRight, Applications(showRight, DefaultConfiguration(showRight))[0])));
    }

    [Fact]
    public void SetInitialVisibility_PreservesDifferentGenerationOfTargetObject()
    {
        PdfDocument original = CreateDocument(indirectSharedConfiguration: false,
            includeMixedApplication: false, includeStaleGeneration: true);

        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 5, true));

        PdfDictionary view = Application(changed,
            Applications(changed, DefaultConfiguration(changed))[0]);
        PdfArray affected = ResolveArray(changed, view[Name("OCGs")]);
        Assert.DoesNotContain(affected, value => value is PdfIndirectReference reference
            && reference.ObjectNumber == 5 && reference.Generation == 0);
        Assert.Contains(affected, value => value is PdfIndirectReference reference
            && reference.ObjectNumber == 5 && reference.Generation == 1);
        Assert.Contains(affected, value => value is PdfIndirectReference reference
            && reference.ObjectNumber == 6 && reference.Generation == 0);
    }

    [Fact]
    public void SetInitialVisibility_RemovesViewOverrideThroughLongAcyclicAliasChain()
    {
        PdfDocument original = CreateAliasedAutoStateDocument(cyclic: false);
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        Assert.False(Assert.Single(before.Groups).IsInitiallyVisible);
        Assert.Contains(5, before.ViewVisibleGroupObjectNumbers);

        PdfDocument edited = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 5, visible: false));
        PdfOptionalContentInfo after = PdfOptionalContentReader.Read(edited);

        Assert.False(Assert.Single(after.Groups).IsInitiallyVisible);
        Assert.DoesNotContain(5, after.ViewVisibleGroupObjectNumbers);
    }

    [Fact]
    public void SetInitialVisibility_CyclicViewAliasKeepsExplicitState()
    {
        PdfDocument original = CreateAliasedAutoStateDocument(cyclic: true);
        Assert.DoesNotContain(5, PdfOptionalContentReader.Read(original)
            .ViewVisibleGroupObjectNumbers);

        PdfDocument edited = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(original, 5, visible: true));
        PdfOptionalContentInfo after = PdfOptionalContentReader.Read(edited);

        Assert.True(Assert.Single(after.Groups).IsInitiallyVisible);
        Assert.Contains(5, after.ViewVisibleGroupObjectNumbers);
    }

    private static PdfDictionary Properties(PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        return ResolveDictionary(document, catalog[Name("OCProperties")]);
    }

    private static PdfDictionary DefaultConfiguration(PdfDocument document) =>
        ResolveDictionary(document, Properties(document)[Name("D")]);

    private static PdfArray Applications(PdfDocument document, PdfDictionary configuration) =>
        ResolveArray(document, configuration[Name("AS")]);

    private static PdfDictionary Application(PdfDocument document, PdfObject value) =>
        ResolveDictionary(document, value);

    private static int[] GroupNumbers(PdfDocument document, PdfDictionary application) =>
        [.. ResolveArray(document, application[Name("OCGs")])
            .Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber)];

    private static string Event(PdfDocument document, PdfDictionary application) =>
        Assert.IsType<PdfName>(Resolve(document, application[Name("Event")])).ValueAsLatin1();

    private static string[] Categories(PdfDocument document, PdfDictionary application) =>
        [.. ResolveArray(document, application[Name("Category")])
            .Select(value => Assert.IsType<PdfName>(Resolve(document, value)).ValueAsLatin1())];

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(Resolve(document, value));

    private static PdfArray ResolveArray(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfArray>(Resolve(document, value));

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        while (value is PdfIndirectReference reference)
            value = document.Resolve(reference);
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfDocument CreateDocument(bool indirectSharedConfiguration,
        bool includeMixedApplication = true, bool includeStaleGeneration = false)
    {
        string directApplications =
            $"[<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R{(includeStaleGeneration ? " 5 1 R" : string.Empty)}] >> " +
            "<< /Event /Export /Category [/Export] /OCGs [5 0 R] >> " +
            "<< /Event /View /Category [/View] >>" +
            (includeMixedApplication
                ? " << /Event /View /Category [/View /Zoom] /OCGs [5 0 R] >>]"
                : "]");
        string configuration = indirectSharedConfiguration
            ? "7 0 R"
            : $"<< /BaseState /OFF /ON [6 0 R] /OFF [5 0 R] /AS {directApplications} >>";
        string alternates = indirectSharedConfiguration ? " /Configs [7 0 R]" : string.Empty;
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D {configuration}{alternates} >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /OCG /Name (Left) /Usage << /View << /ViewState /ON >> >> >>",
            "<< /Type /OCG /Name (Right) /Usage << /View << /ViewState /OFF >> >> >>"
        };
        if (indirectSharedConfiguration)
        {
            objects.AddRange([
                "<< /BaseState /OFF /ON [6 0 R] /OFF [5 0 R] /AS 8 0 R >>",
                "[9 0 R 10 0 R 11 0 R 12 0 R]",
                "<< /Event /View /Category [/View] /OCGs 13 0 R >>",
                "<< /Event /Export /Category [/Export] /OCGs [5 0 R] >>",
                "<< /Event /View /Category [/View] >>",
                "<< /Event /View /Category [/View /Zoom] /OCGs [5 0 R] >>",
                "[5 0 R 6 0 R]"
            ]);
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument CreateAliasedAutoStateDocument(bool cyclic)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] " +
                "/D << /BaseState /OFF /OFF [5 0 R] /AS 7 0 R >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 1 1] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /OCG /Name (Layer) /Usage << /View << /ViewState /ON >> >> >>",
            "null"
        };
        if (cyclic)
            objects.AddRange(["8 0 R", "7 0 R"]);
        else
            for (int objectNumber = 7; objectNumber <= 40; objectNumber++)
                objects.Add(objectNumber == 40
                    ? "[<< /Event /View /Category [/View] /OCGs [5 0 R] >>]"
                    : $"{objectNumber + 1} 0 R");

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>(objects.Count);
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\n" +
            $"startxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
