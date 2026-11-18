using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

/// <summary>
/// PDF/UA-2 rule checks. Each implemented rule has a passing fixture (the conforming document, or
/// a variant that keeps the rule satisfied) and a failing one, built by hand as PDF 2.0.
/// </summary>
public sealed class PdfUa2ValidatorTests
{
    [Fact]
    public void ConformingPdfUa2DocumentRunsTheUa2RuleSetAndFailsNothing()
    {
        PdfUaReport report = Validate(Conforming());

        Assert.Equal("PDF/UA-2", report.Standard);
        Assert.Contains("veraPDF", report.RuleSet);
        Assert.Equal(2, report.DeclaredPart);
        Assert.Empty(report.Failures);
        Assert.Equal(PdfUaVerdict.Incomplete, report.Verdict);
        Assert.Equal(["5", "6", "8.2", "8.4", "8.6", "8.7", "8.8", "8.9", "8.10", "8.11", "8.14", "R"],
            report.Checkpoints.Select(checkpoint => checkpoint.Number));
        Assert.Equal(PdfUaCheckpointStatus.Passed, Checkpoint(report, "5").Status);
        Assert.Equal(PdfUaCheckpointStatus.Passed, Checkpoint(report, "6").Status);
        Assert.Equal(PdfUaCheckpointStatus.NeedsHumanReview, Checkpoint(report, "R").Status);
        // Rules not implemented are listed by their veraPDF ID, never passed silently.
        Assert.Contains("8.2.5.26-1", Checkpoint(report, "8.2").UncheckedConditions);
        Assert.Contains("ISO 32005 Table 5 containment", Checkpoint(report, "8.2").UncheckedConditions);
        Assert.Contains("8.4.5.9-1", Checkpoint(report, "8.4").UncheckedConditions);
        Assert.DoesNotContain(report.Checkpoints.SelectMany(checkpoint => checkpoint.UncheckedConditions),
            id => id is "8.2.5.12-1" or "6.1-1" or "8.9.3.3-1");
    }

    [Fact]
    public void ReportTextAndJsonNameTheUa2RuleSet()
    {
        PdfUaReport report = Validate(Patch(Conforming(), (document, update, catalog) =>
            catalog.Remove(Name("ViewerPreferences"))));

        string text = report.ToText();
        Assert.StartsWith("PDF/UA-2 result: Failed", text);
        Assert.Contains("Rule set: veraPDF PDF/UA-2 profile rule IDs", text);
        Assert.Contains("8.11.2-1 (UA2:8.11.2)", text);
        Assert.Contains("8.11 Metadata: Failed", text);
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("PDF/UA-2", json.RootElement.GetProperty("standard").GetString());
        Assert.StartsWith("veraPDF", json.RootElement.GetProperty("protocol").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("declaredPart").GetInt32());
    }

    [Fact]
    public void Ua2FindingsNeverCarryMatterhornIds()
    {
        PdfUaReport report = Validate(Patch(Conforming(), (document, update, catalog) =>
        {
            catalog.Remove(Name("Lang"));
            catalog.Remove(Name("MarkInfo"));
        }));

        Assert.NotEmpty(report.Failures);
        Assert.All(report.Failures, failure => Assert.StartsWith("UA2:", failure.Clause));
        Assert.All(report.Failures, failure => Assert.DoesNotMatch(@"^\d\d-\d\d\d$", failure.Condition));
    }

    [Theory]
    [InlineData(null, "2024", null)]
    [InlineData("pdfuaid", "2023", "5-5")]
    [InlineData("pdfuaid", null, "5-5")]
    [InlineData("ua", "2024", "5-3,5-4")]
    public void IdentificationNeedsThePdfuaidPrefixAndRevision2024(string? prefix, string? rev, string? expected)
    {
        byte[] pdf = prefix is null ? Conforming() : WithXmp(Conforming(), Xmp(prefix, "2", rev, "Report"));

        string[] identification = Conditions(Validate(pdf), "5");

        Assert.Equal(expected?.Split(',') ?? [], identification.Order());
    }

    [Fact]
    public void RevPropertyWithAnotherPrefixFailsFiveFour()
    {
        string xmp = Xmp("pdfuaid", "2", null, "Report").Replace("</rdf:Description>",
            "</rdf:Description><rdf:Description rdf:about=\"\" xmlns:id2=\"http://www.aiim.org/pdfua/ns/id/\">"
            + "<id2:rev>2024</id2:rev></rdf:Description>", StringComparison.Ordinal);

        Assert.Equal(["5-4"], Conditions(Validate(WithXmp(Conforming(), xmp)), "5"));
    }

    [Fact]
    public void PdfVersionBelow20FailsSixOneOneUnlessTheCatalogSays20()
    {
        byte[] pdf17 = Conforming(pdf17: true);
        Assert.Contains("6.1-1", Conditions(Validate(pdf17), "6"));

        byte[] declared = Patch(pdf17, (document, update, catalog) => catalog[Name("Version")] = Name("2.0"));
        Assert.DoesNotContain("6.1-1", Conditions(Validate(declared), "6"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MarkInfoMarkedMustBeTrue(bool marked, bool fails)
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
            catalog[Name("MarkInfo")] = Dictionary(("Marked", new PdfBoolean(marked))));

        Assert.Equal(fails, Conditions(Validate(pdf), "6").Contains("6.2-1"));
    }

    [Fact]
    public void MissingStructTreeRootFailsEightTwoOneOne()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) => catalog.Remove(Name("StructTreeRoot")));

        Assert.Contains("8.2.1-1", Conditions(Validate(pdf), "8.2"));
        Assert.DoesNotContain("8.2.1-1", Conditions(Validate(Conforming()), "8.2"));
    }

    [Fact]
    public void ElementWithoutParentEntryFailsEightTwoOneTwo()
    {
        byte[] pdf = WithDocumentKids(Element("P", pdf20: true, parent: false));

        Assert.Contains("8.2.1-2", Conditions(Validate(pdf), "8.2"));
        Assert.DoesNotContain("8.2.1-2", Conditions(Validate(WithDocumentKids(Element("P", pdf20: true))), "8.2"));
    }

    [Fact]
    public void UntaggedContentFailsEightTwoTwoOne()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            (PdfIndirectReference reference, Dictionary<PdfName, PdfObject> page) = FirstPage(document, catalog);
            byte[] drawing = Encoding.ASCII.GetBytes("0 0 5 5 re f");
            page[Name("Contents")] = update.AddObject(new PdfStream(
                Dictionary(("Length", new PdfInteger(drawing.Length))), drawing));
            update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page));
        });

        PdfUaFailure failure = Assert.Single(Validate(pdf).Failures, item => item.Condition == "8.2.2-1");
        Assert.Equal("UA2:8.2.2", failure.Clause);
        Assert.Equal(0, failure.PageIndex);
    }

    [Fact]
    public void TypeThatReachesNoStandardNamespaceFailsEightTwoFourOne()
    {
        byte[] pdf = WithDocumentKids(Element("Chapter"));
        byte[] mapped = WithRoleMap(WithDocumentKids(Element("Chapter")), ("Chapter", Name("Sect")));

        Assert.Contains("8.2.4-1", Conditions(Validate(pdf), "8.2"));
        Assert.DoesNotContain("8.2.4-1", Conditions(Validate(mapped), "8.2"));
    }

    [Fact]
    public void CircularRoleMapFailsEightTwoFourTwo()
    {
        byte[] pdf = WithRoleMap(WithDocumentKids(Element("Chapter")),
            ("Chapter", Name("Section")), ("Section", Name("Chapter")));

        Assert.Contains("8.2.4-2", Conditions(Validate(pdf), "8.2"));
    }

    [Fact]
    public void MappingWithinItsOwnExplicitNamespaceFailsEightTwoFourThree()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            PdfIndirectReference pdf20 = Pdf20Namespace(document, catalog);
            // Two dictionaries for one namespace URI: Chapter maps to Section in that same namespace.
            PdfIndirectReference same = update.AddObject(Dictionary(
                ("Type", Name("Namespace")),
                ("NS", Latin1("https://example.com/tags")),
                ("RoleMapNS", Dictionary(("Section", new PdfArray([Name("Sect"), pdf20]))))));
            PdfIndirectReference custom = update.AddObject(Dictionary(
                ("Type", Name("Namespace")),
                ("NS", Latin1("https://example.com/tags")),
                ("RoleMapNS", Dictionary(("Chapter", new PdfArray([Name("Section"), same]))))));
            ReplaceDocumentKids(document, update, catalog, Element("Chapter", ns: custom));
        });

        Assert.Contains("8.2.4-3", Conditions(Validate(pdf), "8.2"));
    }

    [Fact]
    public void RemappedStandardTypeFailsEightTwoFourFour()
    {
        byte[] pdf = WithRoleMap(WithDocumentKids(Element("P")), ("P", Name("Span")));

        Assert.Contains("8.2.4-4", Conditions(Validate(pdf), "8.2"));
        Assert.DoesNotContain("8.2.4-4", Conditions(Validate(WithDocumentKids(Element("P"))), "8.2"));
    }

    [Fact]
    public void Pdf17NoteMappedOntoPdf20FENotePassesBothNoteAndRemapRules()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            PdfIndirectReference pdf20 = Pdf20Namespace(document, catalog);
            PdfIndirectReference pdf17 = update.AddObject(Dictionary(
                ("Type", Name("Namespace")),
                ("NS", Latin1("http://iso.org/pdf/ssn")),
                ("RoleMapNS", Dictionary(("Note", new PdfArray([Name("FENote"), pdf20]))))));
            ReplaceDocumentKids(document, update, catalog, Element("Note", ns: pdf17));
        });

        string[] structure = Conditions(Validate(pdf), "8.2");
        Assert.DoesNotContain("8.2.5.14-1", structure);
        Assert.DoesNotContain("8.2.4-4", structure);
        Assert.Contains("8.2.5.14-1", Conditions(Validate(WithDocumentKids(Element("Note"))), "8.2"));
    }

    [Fact]
    public void RootWithoutOneDocumentElementFailsEightTwoFiveTwoOne()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            var rootReference = (PdfIndirectReference)catalog[Name("StructTreeRoot")];
            var root = ((PdfDictionary)document.Resolve(rootReference)).ToDictionary(item => item.Key, item => item.Value);
            root[Name("K")] = new PdfArray([root[Name("K")], Element("Part", pdf20: true)]);
            update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root));
        });

        Assert.Contains("8.2.5.2-1", Conditions(Validate(pdf), "8.2"));
    }

    [Fact]
    public void Pdf17DocumentElementFailsEightTwoFiveTwoTwo()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            PdfIndirectReference documentReference = DocumentElement(document, catalog);
            var element = ((PdfDictionary)document.Resolve(documentReference)).ToDictionary(item => item.Key, item => item.Value);
            element.Remove(Name("NS"));
            update.ReplaceObject(documentReference.ObjectNumber, new PdfDictionary(element));
        });

        string[] structure = Conditions(Validate(pdf), "8.2");
        Assert.Contains("8.2.5.2-2", structure);
        Assert.DoesNotContain("8.2.5.2-1", structure);
    }

    [Fact]
    public void TociWithoutRefFailsEightTwoFiveEightOne()
    {
        PdfDictionary withRef = Element("TOCI", extra: [("Ref", new PdfArray([]))]);

        Assert.Contains("8.2.5.8-1", Conditions(Validate(WithDocumentKids(Element("TOC", kids: [Element("TOCI")]))), "8.2"));
        Assert.DoesNotContain("8.2.5.8-1", Conditions(Validate(WithDocumentKids(Element("TOC", kids: [withRef]))), "8.2"));
    }

    [Fact]
    public void PlainHHeadingFailsEightTwoFiveTwelveOne()
    {
        Assert.Contains("8.2.5.12-1", Conditions(Validate(WithDocumentKids(Element("H", pdf20: true))), "8.2"));
        Assert.DoesNotContain("8.2.5.12-1", Conditions(Validate(WithDocumentKids(Element("H7", pdf20: true))), "8.2"));
    }

    [Theory]
    [InlineData("Footnote", false)]
    [InlineData("Endnote", false)]
    [InlineData("Sidebar", true)]
    public void FENoteTypeMustBeFootnoteEndnoteOrNone(string noteType, bool fails)
    {
        PdfDictionary note = Element("FENote", pdf20: true, extra:
            [("A", Dictionary(("O", Name("Layout")), ("NoteType", Name(noteType))))]);

        Assert.Equal(fails, Conditions(Validate(WithDocumentKids(note)), "8.2").Contains("8.2.5.14-4"));
    }

    [Fact]
    public void Pdf20TitleAsideAndFENoteAreStandardTypes()
    {
        byte[] pdf = WithDocumentKids(Element("Title", pdf20: true), Element("Aside", pdf20: true),
            Element("FENote", pdf20: true), Element("Em", pdf20: true), Element("DocumentFragment", pdf20: true));

        Assert.Empty(Validate(pdf).Failures);
    }

    [Fact]
    public void RubyAndWarichuNeedTheirExactChildSequences()
    {
        byte[] good = WithDocumentKids(
            Element("Ruby", pdf20: true, kids: [Element("RB", pdf20: true), Element("RT", pdf20: true)]),
            Element("Warichu", pdf20: true, kids:
                [Element("WP", pdf20: true), Element("WT", pdf20: true), Element("WP", pdf20: true)]));
        byte[] bad = WithDocumentKids(
            Element("Ruby", pdf20: true, kids: [Element("RB", pdf20: true)]),
            Element("Warichu", pdf20: true, kids: [Element("WT", pdf20: true)]));

        Assert.Empty(Conditions(Validate(good), "8.2"));
        Assert.Equal(["8.2.5.23-1", "8.2.5.24-1"], Conditions(Validate(bad), "8.2").Order());
    }

    [Theory]
    [InlineData("Ordered", false)]
    [InlineData("None", true)]
    [InlineData(null, true)]
    public void ListWithLabelsNeedsListNumberingOtherThanNone(string? numbering, bool fails)
    {
        (string, PdfObject)[] attributes = numbering is null ? []
            : [("A", Dictionary(("O", Name("List")), ("ListNumbering", Name(numbering))))];
        PdfDictionary list = Element("L", pdf20: true, extra: attributes, kids:
            [Element("LI", pdf20: true, kids: [Element("Lbl", pdf20: true), Element("LBody", pdf20: true)])]);

        Assert.Equal(fails, Conditions(Validate(WithDocumentKids(list)), "8.2").Contains("8.2.5.25-1"));
    }

    [Fact]
    public void ListItemHoldingContentDirectlyFailsEightTwoFiveTwentyFiveTwo()
    {
        PdfDictionary item = Element("LI", pdf20: true, kids: [new PdfInteger(7)]);

        Assert.Contains("8.2.5.25-2", Conditions(Validate(WithDocumentKids(Element("L", pdf20: true, kids: [item]))), "8.2"));
    }

    [Fact]
    public void CaptionBetweenSiblingsFailsEightTwoFiveTwentySevenOne()
    {
        PdfDictionary first = Element("Table", pdf20: true, kids: [Element("Caption", pdf20: true), Element("TR", pdf20: true), Element("TR", pdf20: true)]);
        PdfDictionary middle = Element("Table", pdf20: true, kids: [Element("TR", pdf20: true), Element("Caption", pdf20: true), Element("TR", pdf20: true)]);

        Assert.DoesNotContain("8.2.5.27-1", Conditions(Validate(WithDocumentKids(first)), "8.2"));
        Assert.Contains("8.2.5.27-1", Conditions(Validate(WithDocumentKids(middle)), "8.2"));
    }

    [Fact]
    public void FigureWithoutAltFailsEightTwoFiveTwentyEightTwoOne()
    {
        PdfDictionary described = Element("Figure", pdf20: true, extra: [("Alt", Latin1("A chart"))]);

        Assert.Contains("8.2.5.28.2-1", Conditions(Validate(WithDocumentKids(Element("Figure", pdf20: true))), "8.2"));
        Assert.DoesNotContain("8.2.5.28.2-1", Conditions(Validate(WithDocumentKids(described)), "8.2"));
    }

    [Fact]
    public void MathOutsideFormulaFailsEightTwoFiveTwentyNineOne()
    {
        byte[] Build(bool inFormula) => Patch(Conforming(), (document, update, catalog) =>
        {
            PdfIndirectReference mathml = update.AddObject(Dictionary(
                ("Type", Name("Namespace")), ("NS", Latin1("http://www.w3.org/1998/Math/MathML"))));
            PdfDictionary math = Element("math", ns: mathml, kids: [Element("mi", ns: mathml)]);
            ReplaceDocumentKids(document, update, catalog,
                inFormula ? Element("Formula", pdf20: true, extra: [("Alt", Latin1("x"))], kids: [math]) : math);
        });

        Assert.DoesNotContain("8.2.5.29-1", Conditions(Validate(Build(true)), "8.2"));
        Assert.Contains("8.2.5.29-1", Conditions(Validate(Build(false)), "8.2"));
        Assert.DoesNotContain("8.2.4-1", Conditions(Validate(Build(false)), "8.2"));
    }

    [Theory]
    [InlineData(null, "8.4.4-1")]
    [InlineData("en US", "8.4.4-2")]
    [InlineData("en-US", null)]
    public void CatalogLanguageMustBePresentAndATag(string? language, string? expected)
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            if (language is null) catalog.Remove(Name("Lang"));
            else catalog[Name("Lang")] = Latin1(language);
        });

        string[] text = Conditions(Validate(pdf), "8.4");
        if (expected is null) Assert.Empty(text);
        else Assert.Equal(expected, Assert.Single(text));
    }

    [Fact]
    public void StructureElementLanguageMustBeATag()
    {
        byte[] pdf = WithDocumentKids(Element("P", pdf20: true, extra: [("Lang", Latin1("fr_FR"))]));

        Assert.Contains("8.4.4-2", Conditions(Validate(pdf), "8.4"));
    }

    [Fact]
    public void UnembeddedFontFailsEightFourFiveFiveOneOne()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Paragraph, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100).ShowLatin1Text("Hi").EndText()
            .EndMarkedContent();
        // The PDF/UA-2 authoring mode refuses an unembedded font, so this file declares part 2 itself.
        byte[] pdf = WithXmp(Shell(content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
            .Build(), Xmp("pdfuaid", "2", "2024", "Report"));

        Assert.Contains("8.4.5.5.1-1", Conditions(Validate(pdf), "8.4"));
        Assert.DoesNotContain("8.4.5.5.1-1", Conditions(Validate(Conforming()), "8.4"));
    }

    [Theory]
    [InlineData(false, false, new string[0])]
    [InlineData(false, true, new[] { "8.7-2" })]
    [InlineData(true, false, new[] { "8.7-1" })]
    [InlineData(true, true, new[] { "8.7-1", "8.7-2" })]
    public void OptionalContentConfigurationsNeedNamesOnceConfigsExistAndNeverAs(
        bool alternates, bool autoState, string[] expected)
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
        {
            PdfIndirectReference group = update.AddObject(Dictionary(("Type", Name("OCG")), ("Name", Latin1("Layer"))));
            var properties = new List<(string, PdfObject)>
            {
                ("OCGs", new PdfArray([group])),
                ("D", autoState ? Dictionary(("AS", new PdfArray([]))) : Dictionary())
            };
            if (alternates) properties.Add(("Configs", new PdfArray([Dictionary(("Name", Latin1("Print")))])));
            catalog[Name("OCProperties")] = Dictionary([.. properties]);
        });

        Assert.Equal(expected, Conditions(Validate(pdf), "8.7").Order());
    }

    [Theory]
    [InlineData("S", false)]
    [InlineData("A", false)]
    [InlineData("W", false)]
    [InlineData("R", true)]
    [InlineData(null, true)]
    public void PagesWithAnnotationsNeedTabsOfAWOrS(string? tabs, bool fails)
    {
        byte[] pdf = WithAnnotation(Dictionary(("Subtype", Name("Ink")), ("Contents", Latin1("Mark")),
            ("Rect", Rect(0, 0, 10, 10))), tabs);

        Assert.Equal(fails, Conditions(Validate(pdf), "8.9").Contains("8.9.3.3-1"));
    }

    [Theory]
    [InlineData("Sound", "8.9.2.4.11-1")]
    [InlineData("Movie", "8.9.2.4.11-2")]
    [InlineData("TrapNet", "8.9.2.4.15-1")]
    [InlineData("Screen", "8.9.2.4.12-1")]
    [InlineData("3D", "8.9.2.4.19-1")]
    [InlineData("RichMedia", "8.9.2.4.19-2")]
    [InlineData("Ink", "8.9.2.4.8-1")]
    [InlineData("Stamp", "8.9.2.4.7-1")]
    public void AnnotationSubtypeRules(string subtype, string rule)
    {
        byte[] bare = WithAnnotation(Dictionary(("Subtype", Name(subtype)), ("Rect", Rect(0, 0, 10, 10))), "S");

        Assert.Contains(rule, Conditions(Validate(bare), "8.9"));
        if (subtype is "Sound" or "Movie" or "TrapNet") return;
        byte[] described = WithAnnotation(Dictionary(("Subtype", Name(subtype)), ("Rect", Rect(0, 0, 10, 10)),
            ("Contents", Latin1("Described"))), "S");
        Assert.DoesNotContain(rule, Conditions(Validate(described), "8.9"));
    }

    [Fact]
    public void FileAttachmentSpecificationNeedsAFRelationship()
    {
        PdfDictionary Annotation(bool related) => Dictionary(("Subtype", Name("FileAttachment")),
            ("Rect", Rect(0, 0, 10, 10)), ("Contents", Latin1("Data")),
            ("FS", related
                ? Dictionary(("Type", Name("Filespec")), ("F", Latin1("a.txt")), ("AFRelationship", Name("Data")))
                : Dictionary(("Type", Name("Filespec")), ("F", Latin1("a.txt")))));

        Assert.Contains("8.9.2.4.10-1", Conditions(Validate(WithAnnotation(Annotation(false), "S")), "8.9"));
        Assert.DoesNotContain("8.9.2.4.10-1", Conditions(Validate(WithAnnotation(Annotation(true), "S")), "8.9"));
    }

    [Theory]
    [InlineData("Text", "Span", "8.9.2.3-1")]
    [InlineData("Text", "Annot", null)]
    [InlineData("Link", "Span", "8.2.5.20-1")]
    [InlineData("Link", "Link", null)]
    [InlineData("Popup", "Annot", "8.9.2.4.9-1")]
    [InlineData("PrinterMark", "Annot", "8.9.2.4.14-1")]
    [InlineData("Watermark", "Span", "8.9.2.4.16-1")]
    [InlineData("Watermark", "Annot", null)]
    public void TaggedAnnotationsSitInTheRightStructureElement(string subtype, string parent, string? rule)
    {
        byte[] pdf = WithTaggedAnnotation(subtype, parent, Dictionary(("Contents", Latin1("Note"))));

        string[] conditions = [.. Validate(pdf).Failures.Select(failure => failure.Condition)];
        string[] placement = ["8.9.2.3-1", "8.2.5.20-1", "8.9.2.4.9-1", "8.9.2.4.14-1", "8.9.2.4.16-1"];
        if (rule is null) Assert.Empty(conditions.Intersect(placement));
        else Assert.Equal(rule, Assert.Single(conditions.Intersect(placement)));
    }

    [Theory]
    [InlineData(1, "8.9.2.2-1")]
    [InlineData(32, "8.9.2.2-2")]
    [InlineData(32 | 256, null)]
    [InlineData(4, null)]
    public void HiddenAnnotationsInTheStructureTreeFail(int flags, string? rule)
    {
        byte[] pdf = WithTaggedAnnotation("Text", "Annot",
            Dictionary(("Contents", Latin1("Note")), ("F", new PdfInteger(flags))));

        string[] hidden = [.. Conditions(Validate(pdf), "8.9").Where(id => id.StartsWith("8.9.2.2", StringComparison.Ordinal))];
        if (rule is null) Assert.Empty(hidden);
        else Assert.Equal(rule, Assert.Single(hidden));
    }

    [Fact]
    public void AnnotationContentsMustMatchTheEnclosingAlt()
    {
        byte[] same = WithTaggedAnnotation("Text", "Annot", Dictionary(("Contents", Latin1("Note"))), alt: "Note");
        byte[] different = WithTaggedAnnotation("Text", "Annot", Dictionary(("Contents", Latin1("Note"))), alt: "Other");

        Assert.DoesNotContain("8.9.4.2-1", Conditions(Validate(same), "8.9"));
        Assert.Contains("8.9.4.2-1", Conditions(Validate(different), "8.9"));
    }

    [Fact]
    public void WidgetRulesCoverFormParentActionsSizeAndCount()
    {
        PdfDictionary Field(bool actions) => actions
            ? Dictionary(("FT", Name("Tx")), ("T", Latin1("name")), ("AA", Dictionary()))
            : Dictionary(("FT", Name("Tx")), ("T", Latin1("name")), ("Contents", Latin1("Name")));

        Assert.Empty(Conditions(Validate(WithTaggedAnnotation("Widget", "Form", Field(false))), "8.10"));
        Assert.Contains("8.10.1-1", Conditions(Validate(WithTaggedAnnotation("Widget", "Span", Field(false))), "8.10"));
        Assert.Contains("8.10.2.3-2", Conditions(Validate(WithTaggedAnnotation("Widget", "Form", Field(true))), "8.10"));
        Assert.Contains("8.9.2.4.13-1", Conditions(Validate(
            WithTaggedAnnotation("Widget", "Form", Field(false), rect: Rect(5, 5, 5, 5))), "8.9"));
        Assert.Contains("8.10.1-2", Conditions(Validate(
            WithTaggedAnnotation("Widget", "Form", Field(false), widgets: 2)), "8.10"));
    }

    [Fact]
    public void AnyXfaFailsEightTenOneThree()
    {
        byte[] pdf = Patch(Conforming(), (document, update, catalog) =>
            catalog[Name("AcroForm")] = Dictionary(("Fields", new PdfArray([])), ("XFA", new PdfArray([]))));

        Assert.Contains("8.10.1-3", Conditions(Validate(pdf), "8.10"));
        Assert.DoesNotContain("8.10.1-3", Conditions(Validate(Conforming()), "8.10"));
    }

    [Fact]
    public void MetadataAndDisplayDocTitleRules()
    {
        Assert.Equal(["8.11.1-1"], Conditions(Validate(WithXmp(Conforming(), Xmp("pdfuaid", "2", "2024", null))), "8.11"));
        byte[] titleOff = Patch(Conforming(), (document, update, catalog) =>
            catalog[Name("ViewerPreferences")] = Dictionary(("DisplayDocTitle", new PdfBoolean(false))));
        Assert.Equal(["8.11.2-1"], Conditions(Validate(titleOff), "8.11"));
    }

    [Fact]
    public void EmbeddedFileSpecificationNeedsDesc()
    {
        byte[] Build(bool described) => Patch(Conforming(), (document, update, catalog) =>
        {
            PdfDictionary specification = described
                ? Dictionary(("Type", Name("Filespec")), ("F", Latin1("a.txt")), ("UF", Latin1("a.txt")), ("Desc", Latin1("Data")))
                : Dictionary(("Type", Name("Filespec")), ("F", Latin1("a.txt")), ("UF", Latin1("a.txt")));
            catalog[Name("Names")] = Dictionary(("EmbeddedFiles",
                Dictionary(("Names", new PdfArray([Latin1("a.txt"), update.AddObject(specification)])))));
        });

        Assert.Contains("8.14.1-1", Conditions(Validate(Build(false)), "8.14"));
        Assert.Empty(Conditions(Validate(Build(true)), "8.14"));
    }

    [Fact]
    public void DocumentDeclaringPart1StillRunsTheMatterhornCheckpoints()
    {
        byte[] pdf = WithXmp(Conforming(), Xmp("pdfuaid", "1", null, "Report"));

        PdfUaReport report = Validate(pdf);

        Assert.Equal("PDF/UA-1", report.Standard);
        Assert.Equal("Matterhorn Protocol 1.1", report.RuleSet);
        Assert.Equal(31, report.Checkpoints.Count);
        Assert.StartsWith("PDF/UA-1 result:", report.ToText());
        Assert.DoesNotContain("Rule set:", report.ToText());
    }

    // ---- Fixtures ----

    private static PdfUaReport Validate(byte[] pdf) => PdfUaValidator.Validate(PdfDocument.Open(pdf));

    private static string[] Conditions(PdfUaReport report, string checkpoint) =>
        [.. Checkpoint(report, checkpoint).Failures.Select(failure => failure.Condition)];

    private static PdfUaCheckpointResult Checkpoint(PdfUaReport report, string number) =>
        Assert.Single(report.Checkpoints, checkpoint => checkpoint.Number == number);

    private static PdfDocumentBuilder Shell(PdfContentStreamBuilder content, PdfVersion? version = null) =>
        new PdfDocumentBuilder(version ?? PdfVersion.Pdf20)
            .SetMetadata(new PdfDocumentMetadata { Language = "en-US", Title = "Report" })
            .SetViewerPreferences(new PdfViewerPreferences { DisplayDocumentTitle = true })
            .AddPage(300, 300, content);

    // A tagged PDF 2.0 document built with the engine's PDF/UA-2 authoring mode: Document, H1,
    // P and Figure in the PDF 2.0 namespace, an embedded font, and pdfuaid part 2, rev 2024.
    private static byte[] Conforming(bool pdf17 = false)
    {
        var content = new PdfContentStreamBuilder()
            .BeginArtifact().Rectangle(0, 290, 300, 2).Fill().EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Heading1, 0)
            .BeginText().SetFont(LiberationSans(), 18).MoveText(20, 250).ShowUnicodeText("Report").EndText()
            .EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Paragraph, 1)
            .BeginText().SetFont(LiberationSans(), 12).MoveText(20, 220).ShowUnicodeText("Sales rose.").EndText()
            .EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Figure, 2)
            .Rectangle(20, 100, 40, 40).Fill()
            .EndMarkedContent();
        // The authoring mode refuses PDF 1.7, so the 1.7 variant declares part 2 in its own XMP.
        PdfDocumentBuilder builder = Shell(content, pdf17 ? PdfVersion.Pdf17 : null);
        if (!pdf17) builder.EnablePdfUa2Conformance();
        byte[] pdf = builder
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Heading1, 0, 0, 1)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 1, 1)
            .AddStructureElement(PdfStructureType.Figure, 0, 2, 1, alternateDescription: "A black square")
            .Build();
        return pdf17 ? WithXmp(pdf, Xmp("pdfuaid", "2", "2024", "Report")) : pdf;
    }

    private static TrueTypeFont LiberationSans()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        byte[] font = new byte[resource.Length];
        resource.ReadExactly(font);
        return TrueTypeFont.Load(font);
    }

    private static string Xmp(string prefix, string part, string? rev, string? title)
    {
        string titleXml = title is null ? "" :
            $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>";
        string revXml = rev is null ? "" : $"<{prefix}:rev>{rev}</{prefix}:rev>";
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\""
            + $" xmlns:{prefix}=\"http://www.aiim.org/pdfua/ns/id/\">"
            + titleXml + $"<{prefix}:part>{part}</{prefix}:part>" + revXml
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    private static byte[] WithXmp(byte[] pdf, string xmp) =>
        Patch(pdf, (document, update, catalog) =>
        {
            byte[] data = Encoding.UTF8.GetBytes(xmp);
            catalog[Name("Metadata")] = update.AddObject(new PdfStream(Dictionary(
                ("Type", Name("Metadata")), ("Subtype", Name("XML")), ("Length", new PdfInteger(data.Length))), data));
        });

    private static PdfIndirectReference DocumentElement(PdfDocument document, Dictionary<PdfName, PdfObject> catalog)
    {
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("StructTreeRoot")]);
        PdfObject kids = root[Name("K")];
        return kids is PdfArray array ? (PdfIndirectReference)array[0] : (PdfIndirectReference)kids;
    }

    private static PdfIndirectReference Pdf20Namespace(PdfDocument document, Dictionary<PdfName, PdfObject> catalog)
    {
        var element = (PdfDictionary)document.Resolve(DocumentElement(document, catalog));
        return (PdfIndirectReference)element[Name("NS")];
    }

    // Replaces the Document element's children with the given elements, each pointing back to it.
    private static void ReplaceDocumentKids(PdfDocument document, PdfIncrementalUpdateBuilder update,
        Dictionary<PdfName, PdfObject> catalog, params PdfObject[] kids)
    {
        PdfIndirectReference documentReference = DocumentElement(document, catalog);
        var element = ((PdfDictionary)document.Resolve(documentReference)).ToDictionary(item => item.Key, item => item.Value);
        PdfIndirectReference pdf20 = (PdfIndirectReference)element[Name("NS")];
        element[Name("K")] = new PdfArray([.. kids.Select(kid => Bind(kid, documentReference, pdf20))]);
        update.ReplaceObject(documentReference.ObjectNumber, new PdfDictionary(element));
    }

    // Fills in the parent and the PDF 2.0 namespace placeholders of hand-built elements.
    private static PdfObject Bind(PdfObject value, PdfIndirectReference parent, PdfIndirectReference pdf20)
    {
        if (value is not PdfDictionary element || !element.ContainsKey(Name("S"))) return value;
        var entries = element.ToDictionary(item => item.Key, item => item.Value);
        if (entries.Remove(Name("P_")) ) entries[Name("P")] = parent;
        if (entries.TryGetValue(Name("NS"), out PdfObject? ns) && ns is PdfName { } marker && marker.ValueAsLatin1() == "PDF2")
            entries[Name("NS")] = pdf20;
        if (entries.TryGetValue(Name("K"), out PdfObject? kids) && kids is PdfArray array)
            entries[Name("K")] = new PdfArray([.. array.Select(kid => Bind(kid, parent, pdf20))]);
        return new PdfDictionary(entries);
    }

    private static PdfDictionary Element(string type, bool pdf20 = false, bool parent = true,
        PdfIndirectReference? ns = null, (string, PdfObject)[]? extra = null, PdfObject[]? kids = null)
    {
        var entries = new List<(string, PdfObject)> { ("Type", Name("StructElem")), ("S", Name(type)) };
        if (parent) entries.Add(("P_", new PdfBoolean(true)));
        if (ns is not null) entries.Add(("NS", ns));
        else if (pdf20) entries.Add(("NS", Name("PDF2")));
        if (extra is not null) entries.AddRange(extra);
        if (kids is not null) entries.Add(("K", new PdfArray(kids)));
        return Dictionary([.. entries]);
    }

    private static byte[] WithDocumentKids(params PdfObject[] kids) =>
        Patch(Conforming(), (document, update, catalog) => ReplaceDocumentKids(document, update, catalog, kids));

    private static byte[] WithRoleMap(byte[] pdf, params (string, PdfObject)[] entries) =>
        Patch(pdf, (document, update, catalog) =>
        {
            var rootReference = (PdfIndirectReference)catalog[Name("StructTreeRoot")];
            var root = ((PdfDictionary)document.Resolve(rootReference)).ToDictionary(item => item.Key, item => item.Value);
            root[Name("RoleMap")] = Dictionary(entries);
            update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root));
        });

    private static (PdfIndirectReference, Dictionary<PdfName, PdfObject>) FirstPage(
        PdfDocument document, Dictionary<PdfName, PdfObject> catalog)
    {
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        return (reference, ((PdfDictionary)document.Resolve(reference)).ToDictionary(item => item.Key, item => item.Value));
    }

    private static byte[] WithAnnotation(PdfDictionary annotation, string? tabs) =>
        Patch(Conforming(), (document, update, catalog) =>
        {
            (PdfIndirectReference reference, Dictionary<PdfName, PdfObject> page) = FirstPage(document, catalog);
            page[Name("Annots")] = new PdfArray([update.AddObject(annotation)]);
            if (tabs is null) page.Remove(Name("Tabs"));
            else page[Name("Tabs")] = Name(tabs);
            update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page));
        });

    // Adds annotations of one subtype to page 1 and tags them under a PDF 2.0 element of the given
    // type, through OBJR references.
    private static byte[] WithTaggedAnnotation(string subtype, string parentType, PdfDictionary extra,
        string? alt = null, PdfArray? rect = null, int widgets = 1) =>
        Patch(Conforming(), (document, update, catalog) =>
        {
            (PdfIndirectReference pageReference, Dictionary<PdfName, PdfObject> page) = FirstPage(document, catalog);
            var annotations = new List<PdfObject>();
            var objrs = new List<PdfObject>();
            for (int index = 0; index < widgets; index++)
            {
                var entries = extra.ToDictionary(item => item.Key, item => item.Value);
                entries[Name("Type")] = Name("Annot");
                entries[Name("Subtype")] = Name(subtype);
                entries[Name("Rect")] = rect ?? Rect(10, 10, 60, 30);
                PdfIndirectReference annotation = update.AddObject(new PdfDictionary(entries));
                annotations.Add(annotation);
                objrs.Add(Dictionary(("Type", Name("OBJR")), ("Pg", pageReference), ("Obj", annotation)));
            }
            page[Name("Annots")] = new PdfArray(annotations);
            page[Name("Tabs")] = Name("S");
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page));
            (string, PdfObject)[] altEntry = alt is null ? [] : [("Alt", Latin1(alt))];
            ReplaceDocumentKids(document, update, catalog,
                Element(parentType, pdf20: true, extra: altEntry, kids: [.. objrs]));
        });

    private static PdfArray Rect(int x1, int y1, int x2, int y2) =>
        new([new PdfInteger(x1), new PdfInteger(y1), new PdfInteger(x2), new PdfInteger(y2)]);

    private static byte[] Patch(byte[] pdf,
        Action<PdfDocument, PdfIncrementalUpdateBuilder, Dictionary<PdfName, PdfObject>> edit)
    {
        PdfDocument document = PdfDocument.Open(pdf);
        PdfIndirectReference catalogReference =
            Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        var catalog = ((PdfDictionary)document.Resolve(catalogReference))
            .ToDictionary(item => item.Key, item => item.Value);
        var update = new PdfIncrementalUpdateBuilder(document);
        edit(document, update, catalog);
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog));
        return update.Build();
    }

    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] entries) =>
        new([.. entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Key), entry.Value))]);

    private static PdfString Latin1(string value) => new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
