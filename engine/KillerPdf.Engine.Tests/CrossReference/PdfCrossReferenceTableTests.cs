using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.CrossReference;

public sealed class PdfCrossReferenceTableTests
{
    [Fact]
    public void Read_CompatibilityRecoveryRebuildsAnEmptyMalformedTable()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        source.Append("xref\n0 \ntrailer\n<< /Size 0 /Root 1 0 R >>\n");
        source.Append("startxref\n0\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Throws<PdfSyntaxException>(() => PdfCrossReferenceTable.Read(bytes));

        PdfCrossReferenceTable recovered = PdfCrossReferenceTable.Read(
            bytes, compatibilityRecovery: true);

        Assert.Equal(PdfCrossReferenceEntryType.InUse, recovered[1].Type);
        Assert.Equal(objectOffset, recovered[1].Field1);
        Assert.IsType<PdfIndirectReference>(recovered.MergedTrailer[Name("Root")]);
    }

    [Fact]
    public void Read_CompatibilityRecoveryFindsFinalTableWhenStartxrefIsMisspelled()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\ntrailer\n<< /Size 0 /Root 1 0 R >>\n");
        source.Append("startref\n0\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Throws<PdfSyntaxException>(() => PdfCrossReferenceTable.Read(bytes));

        PdfCrossReferenceTable recovered = PdfCrossReferenceTable.Read(
            bytes, compatibilityRecovery: true);

        Assert.Equal(xrefOffset, recovered.StartXref.Offset);
        Assert.Equal(objectOffset, recovered[1].Field1);
    }

    [Fact]
    public void Read_MergesIncrementalRevisionsNewestFirstAndInheritsTrailerValues()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj\n(old)\nendobj\n");

        int oldXrefOffset = source.Length;
        source.Append("xref\n0 2\n");
        source.Append("0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Root 1 0 R >>\n");

        int newObjectOffset = source.Length;
        source.Append("1 0 obj\n(new)\nendobj\n");
        int newXrefOffset = source.Length;
        source.Append("xref\n1 1\n");
        source.Append($"{newObjectOffset:0000000000} 00000 n\n");
        source.Append($"trailer\n<< /Size 2 /Prev {oldXrefOffset} >>\n");
        source.Append($"startxref\n{newXrefOffset}\n%%EOF\n");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(PdfVersion.Pdf20, table.Header.Version);
        Assert.Equal(2, table.Sections.Count);
        Assert.Equal(newObjectOffset, table[1].Field1);
        Assert.True(table.TryGetTrailerValue(Name("Root"), out PdfObject root));
        Assert.Equal(1, Assert.IsType<PdfIndirectReference>(root).ObjectNumber);
    }

    [Fact]
    public void Read_RejectsRevisionCycles()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /Prev {xrefOffset} >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsPreviousOffsetThatPointsForward()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int firstOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 /Prev 0000000000 >>\n");
        int laterOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {laterOffset:0000000000}");
        source.Append("xref\n0 1\n0000000000 65535 f\ntrailer\n<< /Size 1 >>\n");
        source.Append($"startxref\n{firstOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections.Count);
    }

    [Fact]
    public void Read_CompatibilityRecoveryStopsAtPreviousOffsetPastEndOfFile()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 /Prev 999999 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));
        Assert.Single(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections);
    }

    [Fact]
    public void Read_CompatibilityRecoveryFindsFinalXrefBeforePastEndStartOffset()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 >>\n");
        source.Append("startxref\n999999\n%%EOF\n");

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));
        PdfCrossReferenceTable recovered = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true);

        Assert.Single(recovered.Sections);
        Assert.Equal(xrefOffset, recovered.Sections[0].Offset);
    }

    [Fact]
    public void Read_CompatibilityRecoveryTreatsZeroPreviousOffsetAsNoPreviousRevision()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 /Prev 0 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));
        Assert.Single(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections);
    }

    [Fact]
    public void Read_CompatibilityRecoveryUsesPreviousReferenceObjectNumberAsOffset()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int olderOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 >>\n");
        int latestOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /Prev {olderOffset} 0 R >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));
        Assert.Equal(2, PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections.Count);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Read_AllowsLinearizedFirstPageSectionToPointForwardToMainSection(
        string headerLineEnding)
    {
        var source = new StringBuilder($"%PDF-1.7{headerLineEnding}");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00001 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(2, table.Sections.Count);
        Assert.Equal(firstPageOffset, table.Sections[0].Offset);
        Assert.Equal(mainOffset, table.Sections[1].Offset);
    }

    [Fact]
    public void Read_RejectsLinearizationParameterObjectWithNonzeroGeneration()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 1 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00001 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00001 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsUnregisteredLinearizationParameterObject()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("primary-before-dictionary")]
    [InlineData("primary-at-first-page-xref")]
    [InlineData("primary-after-first-page")]
    [InlineData("overflow-before-first-page-end")]
    [InlineData("overflow-after-main-xref")]
    public void Read_RejectsLinearizationHintRangesOutsideTheirLayoutRegion(
        string invalidRange)
    {
        const string hintPlaceholder =
            "/H [0000000001 0 0000000002 0]";
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append($"1 0 obj << /Linearized 1 /L 0000000000 {hintPlaceholder} " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        int firstPageEnd = mainOffset - 1;
        (int primary, int overflow) = invalidRange switch
        {
            "primary-before-dictionary" => (1, firstPageEnd),
            "primary-at-first-page-xref" =>
                (firstPageOffset, firstPageEnd),
            "primary-after-first-page" => (mainOffset, firstPageEnd),
            "overflow-before-first-page-end" =>
                (firstPageOffset + 1, firstPageOffset),
            "overflow-after-main-xref" =>
                (firstPageOffset + 1, mainOffset + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidRange))
        };
        source.Replace(hintPlaceholder,
            $"/H [{primary:0000000000} 0 {overflow:0000000000} 0]");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {firstPageEnd:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsLinearizedFirstPageEndAtMainXrefHint()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AllowsLinearizedMainXrefHintImmediatelyBeforePreviousTarget()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 2:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset - 1:0000000000}");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(2, table.Sections.Count);
    }

    [Fact]
    public void Read_RejectsLinearizedForwardPreviousOffsetAwayFromMainXrefHint()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        Assert.True(mainOffset + 65 < source.Length);
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset + 65:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsLinearizationDictionaryThatExtendsBeyondFirstKilobyte()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000");
        source.Append(' ', 1_024);
        source.Append(">> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DoesNotRecognizeLinearizationAfterPrefixedBytes()
    {
        var source = new StringBuilder("prefix\n%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AllowsAppendedRevisionAfterDeclaredLinearizedLength()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00001 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        int originalLength = source.Length;
        source.Replace("/L 0000000000", $"/L {originalLength:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");
        int appendedOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 2 /Prev {firstPageOffset} >>\n");
        source.Append($"startxref\n{appendedOffset}\n%%EOF\n");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(3, table.Sections.Count);
        Assert.Equal(appendedOffset, table.Sections[0].Offset);
        Assert.Equal(firstPageOffset, table.Sections[1].Offset);
        Assert.Equal(mainOffset, table.Sections[2].Offset);
    }

    [Fact]
    public void Read_RejectsLinearizedLengthWithoutCompleteOriginalEof()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int firstPageOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{firstPageOffset + 1:0000000000} 0]");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 /Prev 0000000000 >>\n");
        int mainOffset = source.Length;
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00001 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{firstPageOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length - 2:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Prev must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsHybridStreamOffsetThatPointsForward()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int tableOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 /XRefStm 0000000000 >>\n");
        int streamOffset = source.Length;
        source.Replace("/XRefStm 0000000000", $"/XRefStm {streamOffset:0000000000}");
        source.Append("1 0 obj << /Type /XRef /Size 2 /W [1 1 1] /Index [0 1] /Length 3 >> stream\n");
        source.Append('\0').Append('\0').Append((char)255);
        source.Append("\nendstream endobj\n");
        source.Append($"startxref\n{tableOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.Latin1.GetBytes(source.ToString())));

        Assert.Contains("/XRefStm must point to an earlier", error.Message,
            StringComparison.Ordinal);
        Assert.Single(PdfCrossReferenceTable.Read(
            Encoding.Latin1.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections);
    }

    [Fact]
    public void Read_AllowsLinearizedFirstPageTableToPointForwardToHybridStream()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [0000000000 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int tableOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("0000000000 00000 n\n");
        source.Append("trailer\n<< /Size 3 /Prev 0000000000 /XRefStm 0000000000 >>\n");
        int streamOffset = source.Length;
        source.Replace("/XRefStm 0000000000", $"/XRefStm {streamOffset:0000000000}");
        source.Replace("0000000000 00000 n\ntrailer",
            $"{streamOffset:0000000000} 00000 n\ntrailer");
        source.Append("2 0 obj << /Type /XRef /Size 2 /W [1 2 2] " +
            "/Index [1 1] /Length 5 >> stream\n");
        source.Append((char)1).Append((char)(objectOffset >> 8))
            .Append((char)(objectOffset & 0xFF)).Append('\0').Append('\0');
        source.Append("\nendstream endobj\n");
        int mainOffset = source.Length;
        source.Replace("/H [0000000000 0]", $"/H [{mainOffset - 1:0000000000} 0]");
        source.Replace("/Prev 0000000000", $"/Prev {mainOffset:0000000000}");
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append("0000000000 00000 n\n0000000000 00000 n\n");
        source.Append("trailer\n<< /Size 3 >>\n");
        source.Append($"startxref\n{tableOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {mainOffset - 1:0000000000}");
        source.Replace("/T 0000000000", $"/T {mainOffset:0000000000}");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.Latin1.GetBytes(source.ToString()));

        Assert.Equal(2, table.Sections.Count);
        Assert.Equal(objectOffset, table[1].Field1);
    }

    [Fact]
    public void Read_RejectsLinearizedForwardHybridStreamWithoutMainLink()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Linearized 1 /L 0000000000 /H [1 0] " +
            "/O 1 /E 0000000000 /N 1 /T 0000000000 >> endobj\n");
        int tableOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("0000000000 00000 n\n");
        source.Append("trailer\n<< /Size 3 /XRefStm 0000000000 >>\n");
        int streamOffset = source.Length;
        source.Replace("/XRefStm 0000000000", $"/XRefStm {streamOffset:0000000000}");
        source.Replace("0000000000 00000 n\ntrailer",
            $"{streamOffset:0000000000} 00000 n\ntrailer");
        source.Append("2 0 obj << /Type /XRef /Size 2 /W [1 2 2] " +
            "/Index [1 1] /Length 5 >> stream\n");
        source.Append((char)1).Append((char)(objectOffset >> 8))
            .Append((char)(objectOffset & 0xFF)).Append('\0').Append('\0');
        source.Append("\nendstream endobj\n");
        int endOffset = source.Length;
        source.Append($"startxref\n{tableOffset}\n%%EOF\n");
        source.Replace("/L 0000000000", $"/L {source.Length:0000000000}");
        source.Replace("/E 0000000000", $"/E {endOffset:0000000000}");
        source.Replace("/T 0000000000", $"/T {endOffset + 1:0000000000}");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.Latin1.GetBytes(source.ToString())));

        Assert.Contains("/XRefStm must point to an earlier", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsRevisionChainsBeyondConfiguredLimit()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int? previousOffset = null;
        int latestOffset = 0;
        for (int revision = 0;
             revision <= PdfCrossReferenceTable.MaximumRevisionCount;
             revision++)
        {
            latestOffset = source.Length;
            source.Append("xref\n0 1\n0000000000 65535 f\n");
            source.Append("trailer\n<< /Size 1");
            if (previousOffset.HasValue)
                source.Append($" /Prev {previousOffset.Value}");
            source.Append(" >>\n");
            previousOffset = latestOffset;
        }
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("too many incremental revisions",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsTrailerSizeThatDecreasesAcrossRevisions()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int oldOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\ntrailer\n<< /Size 20 >>\n");
        int latestOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /Prev {oldOffset} >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/Size cannot decrease", error.Message,
            StringComparison.Ordinal);
        Assert.NotEmpty(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true));
    }

    [Fact]
    public void Read_RejectsObjectGenerationThatDecreasesAcrossRevisions()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int oldObjectOffset = source.Length;
        source.Append("1 5 obj\n(old)\nendobj\n");
        int oldOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{oldObjectOffset:0000000000} 00005 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");

        int newObjectOffset = source.Length;
        source.Append("1 4 obj\n(new)\nendobj\n");
        int latestOffset = source.Length;
        source.Append("xref\n1 1\n");
        source.Append($"{newObjectOffset:0000000000} 00004 n\n");
        source.Append($"trailer\n<< /Size 2 /Prev {oldOffset} >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("generation cannot decrease", error.Message,
            StringComparison.Ordinal);
        Assert.Equal(4, PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)[1].Field2);
    }

    [Fact]
    public void Read_AcceptsChangedPermanentIdentifierAcrossRevisions()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int oldOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer\n<< /Size 1 /ID [<01> <02>] >>\n");
        int latestOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /ID [<03> <04>] /Prev {oldOffset} >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()));

        Assert.True(table.TryGetTrailerValue(Name("ID"), out _));
    }

    [Theory]
    [InlineData("<01>")]
    [InlineData("[<01>]")]
    [InlineData("[<01> <02> <03>]")]
    [InlineData("[<01> 2]")]
    public void Read_RejectsMalformedTrailerIdentifiers(string identifiers)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /ID {identifiers} >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("/ID must be an array of two strings", error.Message,
            StringComparison.Ordinal);
        Assert.NotEmpty(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true));
    }

    [Fact]
    public void Read_RejectsEncryptionIntroducedByIncrementalRevision()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int firstEncryptionOffset = source.Length;
        source.Append("1 0 obj\n<< /Filter /Standard >>\nendobj\n");
        int secondEncryptionOffset = source.Length;
        source.Append("2 0 obj\n<< /Filter /Standard >>\nendobj\n");
        int oldOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append($"{firstEncryptionOffset:0000000000} 00000 n\n");
        source.Append($"{secondEncryptionOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 3 >>\n");
        int latestOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 3 /Encrypt 2 0 R /Prev {oldOffset} >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("cannot be introduced", error.Message,
            StringComparison.Ordinal);
        Assert.NotEmpty(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true));
    }

    [Fact]
    public void Read_RejectsActiveObjectGenerationThatJumpsAcrossRevisions()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int oldObjectOffset = source.Length;
        source.Append("1 0 obj\n(old)\nendobj\n");
        int oldOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{oldObjectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");

        int newObjectOffset = source.Length;
        source.Append("1 1 obj\n(new)\nendobj\n");
        int latestOffset = source.Length;
        source.Append("xref\n1 1\n");
        source.Append($"{newObjectOffset:0000000000} 00001 n\n");
        source.Append($"trailer\n<< /Size 2 /Prev {oldOffset} >>\n");
        source.Append($"startxref\n{latestOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("invalid generation transition", error.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)[1].Field2);
    }

    [Fact]
    public void Read_RejectsHybridReferenceThatReusesPrimaryOffset()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append($"trailer\n<< /Size 1 /XRefStm {xrefOffset} >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source.ToString())));

        Assert.Contains("hybrid cross-reference chain reuses an offset",
            error.Message, StringComparison.Ordinal);
        Assert.Single(PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()), compatibilityRecovery: true)
            .Sections);
    }

    [Fact]
    public void Read_IgnoresInvalidMergedFreeListTopology()
    {
        Assert.Equal(PdfCrossReferenceEntryType.Free,
            PdfCrossReferenceTable.Read(InvalidFreeListPdf(cyclic: false))[0].Type);
        Assert.Equal(PdfCrossReferenceEntryType.Free,
            PdfCrossReferenceTable.Read(InvalidFreeListPdf(cyclic: true))[0].Type);
    }

    [Fact]
    public void Read_SynthesizesObjectZeroWhenRevisionHistoryOmitsIt()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj\ntrue\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n1 1\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfCrossReferenceEntry zero = PdfCrossReferenceTable.Read(
            Encoding.ASCII.GetBytes(source.ToString()))[0];

        Assert.Equal(PdfCrossReferenceEntryType.Free, zero.Type);
        Assert.Equal(65_535, zero.Field2);
    }

    [Fact]
    public void Read_RejectsStartxrefThatDoesNotPointToCrossReferenceData()
    {
        string source = "%PDF-2.0\n1 0 obj true endobj\nstartxref\n9\n%%EOF\n";

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(Encoding.ASCII.GetBytes(source)));
    }

    [Fact]
    public void Read_RecoveryFindsNearbyClassicCrossReferenceTable()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj\ntrue\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n");
        source.Append("0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{xrefOffset - 7}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Throws<PdfSyntaxException>(() => PdfCrossReferenceTable.Read(bytes));
        PdfCrossReferenceTable recovered = PdfCrossReferenceTable.Read(
            bytes, compatibilityRecovery: true);

        Assert.Equal(objectOffset, recovered[1].Field1);
        Assert.Equal(xrefOffset, Assert.Single(recovered.Sections).Offset);
    }

    [Fact]
    public void Read_AcceptsUnregisteredStandaloneCrossReferenceStream()
    {
        using var source = new MemoryStream();
        Write("%PDF-2.0\n");
        int streamOffset = checked((int)source.Position);
        byte[] row = [0, 0, 0, 255, 255];
        Write("9 0 obj << /Type /XRef /Size 10 /W [1 2 2] " +
            "/Index [0 1] /Length 5 >> stream\n");
        source.Write(row);
        Write($"\nendstream endobj\nstartxref\n{streamOffset}\n%%EOF\n");

        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(source.ToArray());

        Assert.Equal(PdfCrossReferenceEntryType.Free, table[0].Type);

        void Write(string value) => source.Write(Encoding.ASCII.GetBytes(value));
    }

    [Fact]
    public void Read_MergedTrailerPrefersPrimaryOverHybridStreamInSameRevision()
    {
        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            HybridReferencePdf(hybridHasPreviousOffset: false));

        PdfDictionary state = Assert.IsType<PdfDictionary>(
            table.MergedTrailer[Name("PrivateState")]);
        Assert.True(Assert.IsType<PdfBoolean>(state[Name("Enabled")]).Value);
        Assert.True(table.TryGetTrailerValue(Name("PrivateState"), out PdfObject value));
        Assert.True(Assert.IsType<PdfBoolean>(
            Assert.IsType<PdfDictionary>(value)[Name("Enabled")]).Value);

    }

    [Fact]
    public void Read_RejectsPreviousOffsetInHybridCrossReferenceStream()
    {
        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(
                HybridReferencePdf(hybridHasPreviousOffset: true)));

        Assert.Contains("hybrid cross-reference stream cannot contain /Prev",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RejectsHybridRevisionSizeAboveItsTrailer()
    {
        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceTable.Read(
                HybridReferencePdf(hybridHasPreviousOffset: false, hybridSize: 4)));

        Assert.Contains("stream /Size cannot exceed", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AllowsHybridRevisionSizeBelowItsTrailer()
    {
        // Microsoft Excel writes the hybrid stream as the file's last object and
        // indexes only the objects before it, so the stream's /Size is one less
        // than the classic trailer's. Both are correct under ISO 32000-1 Table 17,
        // and the classic table still registers the stream object itself.
        PdfCrossReferenceTable table = PdfCrossReferenceTable.Read(
            HybridReferencePdf(
                hybridHasPreviousOffset: false, hybridSize: 2, hybridIndexCount: 2));

        Assert.Equal(PdfCrossReferenceEntryType.InUse, table[2].Type);
    }

    [Fact]
    public void Read_AllowsHybridStreamToReviveObjectsRetiredByItsClassicTable()
    {
        // Microsoft Office retires object-stream-resident objects at generation
        // 65535 in the classic tables so legacy readers skip them, and lists the
        // same objects as live in the companion stream (ISO 32000-1 7.5.8.4).
        // That is a compatibility convention, not a generation regression.
        PdfCrossReferenceTable table =
            PdfCrossReferenceTable.Read(OfficeStyleHybridPdf());

        Assert.Equal(PdfCrossReferenceEntryType.InUse, table[2].Type);
    }

    private static byte[] OfficeStyleHybridPdf()
    {
        using var source = new MemoryStream();
        Write("%PDF-1.7\n");
        int catalogOffset = checked((int)source.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int streamOffset = checked((int)source.Position);
        byte[] rows =
        [
            1, (byte)(catalogOffset >> 24), (byte)(catalogOffset >> 16),
                (byte)(catalogOffset >> 8), (byte)catalogOffset, 0, 0,
            1, (byte)(streamOffset >> 24), (byte)(streamOffset >> 16),
                (byte)(streamOffset >> 8), (byte)streamOffset, 0, 0
        ];
        Write("2 0 obj\n<< /Type /XRef /Size 3 /W [1 4 2] /Index [1 2] " +
            $"/Length {rows.Length} /Root 1 0 R >>\nstream\n");
        source.Write(rows);
        Write("\nendstream\nendobj\n");

        // Revision A: the legacy-visible table, which retires object 2.
        int firstTableOffset = checked((int)source.Position);
        Write("xref\n0 3\n");
        Write("0000000002 65535 f\n");
        Write($"{catalogOffset:0000000000} 00000 n\n");
        Write("0000000000 65535 f\n");
        Write("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        Write($"startxref\n{firstTableOffset}\n%%EOF\n");

        // Revision B: an empty table whose trailer points at both companions.
        int secondTableOffset = checked((int)source.Position);
        Write("xref\n0 0\n");
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Prev {firstTableOffset} " +
            $"/XRefStm {streamOffset} >>\n");
        Write($"startxref\n{secondTableOffset}\n%%EOF\n");
        return source.ToArray();

        void Write(string value) => source.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] HybridReferencePdf(
        bool hybridHasPreviousOffset, int hybridSize = 3,
        int hybridIndexCount = 3)
    {
        using var source = new MemoryStream();
        Write("%PDF-2.0\n");
        int catalogOffset = checked((int)source.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int streamOffset = checked((int)source.Position);
        byte[] rows =
        [
            0, 0, 0, 0, 0, 255, 255,
            1, (byte)(catalogOffset >> 24), (byte)(catalogOffset >> 16),
                (byte)(catalogOffset >> 8), (byte)catalogOffset, 0, 0,
            1, (byte)(streamOffset >> 24), (byte)(streamOffset >> 16),
                (byte)(streamOffset >> 8), (byte)streamOffset, 0, 0
        ];
        rows = rows[..(hybridIndexCount * 7)];
        Write($"2 0 obj\n<< /Type /XRef /Size {hybridSize} /W [1 4 2] " +
            $"/Index [0 {hybridIndexCount}] /Length {rows.Length} " +
            (hybridHasPreviousOffset ? "/Prev 0 " : string.Empty) +
            "/PrivateState << /Enabled false >> >>\nstream\n");
        source.Write(rows);
        Write("\nendstream\nendobj\n");
        int tableOffset = checked((int)source.Position);
        Write("xref\n0 3\n");
        Write("0000000000 65535 f\n");
        Write($"{catalogOffset:0000000000} 00000 n\n");
        Write($"{streamOffset:0000000000} 00000 n\n");
        Write($"trailer\n<< /Size 3 /Root 1 0 R /XRefStm {streamOffset} " +
            "/PrivateState << /Enabled true >> >>\n");
        Write($"startxref\n{tableOffset}\n%%EOF\n");
        return source.ToArray();

        void Write(string value) => source.Write(Encoding.ASCII.GetBytes(value));
    }

    private static byte[] InvalidFreeListPdf(bool cyclic)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        if (!cyclic)
            source.Append("1 0 obj\ntrue\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n");
        source.Append("0000000001 65535 f\n");
        source.Append(cyclic
            ? "0000000001 00000 f\n"
            : $"{objectOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 2 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
