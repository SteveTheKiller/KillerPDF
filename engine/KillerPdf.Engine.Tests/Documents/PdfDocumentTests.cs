using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentTests
{
    [Fact]
    public void Open_ResolvesClassicObjectsAndIndirectStreamLengths()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int lengthOffset = source.Length;
        source.Append("1 0 obj 5 endobj\n");
        int lengthAliasOffset = source.Length;
        source.Append("3 0 obj 1 0 R endobj\n");
        int streamOffset = source.Length;
        source.Append("2 0 obj << /Length 3 0 R >> stream\nHello\nendstream endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 4\n");
        source.Append("0000000000 65535 f\n");
        source.Append($"{lengthOffset:0000000000} 00000 n\n");
        source.Append($"{streamOffset:0000000000} 00000 n\n");
        source.Append($"{lengthAliasOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 4 /Root 2 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString()));

        Assert.Equal(PdfVersion.Pdf20, document.Header.Version);
        Assert.Equal(5, Assert.IsType<PdfInteger>(document.Resolve(1)).Value);
        var stream = Assert.IsType<PdfStream>(document.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal("Hello", Encoding.ASCII.GetString(stream.EncodedData.Span));
    }

    [Fact]
    public void OpenWithCompatibilityRecoveryFindsObjectAtMalformedXrefOffset()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 0 obj << /Type /Catalog >> endobj\n");
        source.Append(new string('x', 200)).Append('\n');
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset + 100:0000000000} 00000 n\n");
        source.Append("trailer << /Size 2 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        PdfDocument strict = PdfDocument.Open(bytes);
        Assert.Throws<PdfSyntaxException>(() => strict.Resolve(1));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Equal(Name("Catalog"),
            Assert.IsType<PdfDictionary>(recovered.Resolve(1))[Name("Type")]);
    }

    [Fact]
    public void OpenWithCompatibilityRecoveryFindsObjectMissingFromXref()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        source.Append("1 0 obj << /Type /Catalog >> endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 1\n0000000000 65535 f\n");
        source.Append("trailer << /Size 2 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Same(PdfNull.Instance,
            PdfDocument.Open(bytes).Resolve(new PdfIndirectReference(1, 0)));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Equal(Name("Catalog"), Assert.IsType<PdfDictionary>(
            recovered.Resolve(new PdfIndirectReference(1, 0)))[Name("Type")]);
    }

    [Fact]
    public void OpenWithCompatibilityRecoveryFindsReferencedObjectMarkedFree()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        source.Append("1 0 obj << /Type /Catalog >> endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n0000000000 00000 f\n");
        source.Append("trailer << /Size 2 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Same(PdfNull.Instance,
            PdfDocument.Open(bytes).Resolve(new PdfIndirectReference(1, 0)));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Equal(Name("Catalog"), Assert.IsType<PdfDictionary>(
            recovered.Resolve(new PdfIndirectReference(1, 0)))[Name("Type")]);
    }

    [Fact]
    public void OpenWithCompatibilityRecoveryAllowsCatalogWithoutType()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj << /Pages 2 0 R >> endobj\n");
        int pagesOffset = source.Length;
        source.Append("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        int pageOffset = source.Length;
        source.Append("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n");
        int contentOffset = source.Length;
        source.Append("4 0 obj << /Length 0 >> stream\n\nendstream endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 5\n0000000000 65535 f\n");
        source.Append($"{catalogOffset:0000000000} 00000 n\n");
        source.Append($"{pagesOffset:0000000000} 00000 n\n");
        source.Append($"{pageOffset:0000000000} 00000 n\n");
        source.Append($"{contentOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 5 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Throws<InvalidOperationException>(() =>
            new PdfPageContentReader(PdfDocument.Open(bytes)));
        Assert.Equal(1, new PdfPageContentReader(
            PdfDocument.OpenWithCompatibilityRecovery(bytes)).PageCount);
    }

    [Theory]
    [InlineData(" /Parent 9 0 R", "")]
    [InlineData("", "")]
    [InlineData("", " /Parent 9 0 R")]
    public void OpenWithCompatibilityRecoveryIgnoresBrokenPageTreeParents(
        string rootParent, string pageParent)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        int pagesOffset = source.Length;
        source.Append($"2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1{rootParent} >> endobj\n");
        int pageOffset = source.Length;
        source.Append($"3 0 obj << /Type /Page{pageParent} /MediaBox [0 0 10 10] >> endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 4\n0000000000 65535 f\n");
        source.Append($"{catalogOffset:0000000000} 00000 n\n");
        source.Append($"{pagesOffset:0000000000} 00000 n\n");
        source.Append($"{pageOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 4 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(source.ToString());

        Assert.Throws<InvalidOperationException>(() =>
            new PdfPageContentReader(PdfDocument.Open(bytes)));
        Assert.Equal(1, new PdfPageContentReader(
            PdfDocument.OpenWithCompatibilityRecovery(bytes)).PageCount);
    }

    [Fact]
    public void Open_ResolvesMultipleObjectsFromAnObjectStreamByXrefIndex()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(
            indirectStructuralValues: true,
            indirectFilterValues: true));

        Assert.Equal("hello", Text(Assert.IsType<PdfString>(document.Resolve(1))));
        var dictionary = Assert.IsType<PdfDictionary>(document.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(42, Assert.IsType<PdfInteger>(dictionary[Name("Answer")]).Value);
    }

    [Fact]
    public void Resolve_ReturnsNullForMissingFreeAndStaleGenerationReferences()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf());

        Assert.Same(PdfNull.Instance, document.Resolve(99));
        Assert.Same(PdfNull.Instance, document.Resolve(0));
        Assert.Same(PdfNull.Instance, document.Resolve(new PdfIndirectReference(1, 1)));
    }

    [Fact]
    public void Resolve_RejectsAnObjectStreamIndexThatNamesAnotherObject()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(firstCompressedIndex: 1));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(1));

        Assert.Contains("does not match its compressed cross-reference entry",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AllowsSupersededMembersInAnOlderObjectStream()
    {
        PdfDocument original = PdfDocument.Open(ObjectStreamPdf());
        PdfDocument incremented = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(1, new PdfDictionary([
                new(Name("Type"), Name("Catalog")),
                new(Name("Updated"), new PdfBoolean(true))
            ]))
            .Build());

        PdfDictionary retained = Assert.IsType<PdfDictionary>(incremented.Resolve(2));

        Assert.Equal(42, Assert.IsType<PdfInteger>(retained[Name("Answer")]).Value);
    }

    [Fact]
    public void Resolve_RejectsHistoricalMembershipFromAnOlderObjectStreamVersion()
    {
        const string catalog = "<< /Type /Catalog >>";
        const string second = "<< /Answer 42 >>";
        const string third = "(old)";
        string header = $"1 0 2 {catalog.Length} 3 {catalog.Length + second.Length} ";
        string body = catalog + second + third;
        PdfDocument original = PdfDocument.Open(ObjectStreamPdf(
            header: header, objectCount: 3, body: body, thirdCompressedIndex: 2));
        byte[] data = Encoding.ASCII.GetBytes(header + body);
        var replacementStream = new PdfStream(new PdfDictionary([
            new(Name("Type"), Name("ObjStm")),
            new(Name("N"), new PdfInteger(3)),
            new(Name("First"), new PdfInteger(header.Length)),
            new(Name("Length"), new PdfInteger(data.Length))
        ]), data);
        var update = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(3, new PdfInteger(99))
            .ReplaceObject(5, replacementStream);
        PdfDocument incremented = PdfDocument.Open(update.Build());

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            incremented.Resolve(1));

        Assert.Contains("does not match any compressed cross-reference entry",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsAnObjectStreamWithTrailingHeaderEntries()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(header: "1 0 2 7 3 9 "));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(1));

        Assert.Contains("more entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsUnregisteredObjectStreamHeaderEntries()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(
            header: "1 0 2 7 3 23 ", objectCount: 3,
            body: "(hello)<< /Answer 42 >>null"));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(1));

        Assert.Contains("header entry 2 for object 3 does not match",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsOversizedObjectStreamBeforeAllocatingHeaders()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(
            objectCount: PdfDocument.MaximumObjectsPerObjectStream + 1));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(1));

        Assert.Contains("cannot contain more than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsNonzeroObjectStreamGeneration()
    {
        PdfDocument document = PdfDocument.Open(ObjectStreamPdf(objectStreamGeneration: 1));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(1));

        Assert.Contains("Object stream 5 must have generation 0", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsCyclicIndirectStreamLengths()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int firstOffset = source.Length;
        source.Append("1 0 obj 2 0 R endobj\n");
        int secondOffset = source.Length;
        source.Append("2 0 obj 1 0 R endobj\n");
        int streamOffset = source.Length;
        source.Append("3 0 obj << /Length 1 0 R >> stream\nX\nendstream endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 4\n0000000000 65535 f\n");
        source.Append($"{firstOffset:0000000000} 00000 n\n");
        source.Append($"{secondOffset:0000000000} 00000 n\n");
        source.Append($"{streamOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 4 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString()));

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => document.Resolve(3));

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public void Resolve_BoundsIndirectStreamLengthChains(int referenceCount, bool accepted)
    {
        PdfDocument document = PdfDocument.Open(StreamLengthChainPdf(referenceCount));
        int streamNumber = referenceCount + 2;

        if (accepted)
        {
            var stream = Assert.IsType<PdfStream>(document.Resolve(streamNumber));
            Assert.Equal("X", Encoding.ASCII.GetString(stream.EncodedData.Span));
            return;
        }

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(
            () => document.Resolve(streamNumber));
        Assert.Contains("too deeply indirect", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void PageTree_EnforcesDeclaredNestingLimit(int nodeCount, bool accepted)
    {
        PdfDocument document = PdfDocument.Open(DeepPageTreePdf(nodeCount));

        if (accepted)
        {
            Assert.Equal(1, new PdfIncrementalPageEditor(document).PageCount);
            return;
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new PdfIncrementalPageEditor(document));
        Assert.Contains("nesting depth", error.Message, StringComparison.Ordinal);
    }

    private static byte[] StreamLengthChainPdf(int referenceCount)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int> { 0 };
        for (int objectNumber = 1; objectNumber <= referenceCount; objectNumber++)
        {
            offsets.Add(source.Length);
            source.Append($"{objectNumber} 0 obj {objectNumber + 1} 0 R endobj\n");
        }
        int lengthNumber = referenceCount + 1;
        offsets.Add(source.Length);
        source.Append($"{lengthNumber} 0 obj 1 endobj\n");
        int streamNumber = referenceCount + 2;
        offsets.Add(source.Length);
        source.Append($"{streamNumber} 0 obj << /Length 1 0 R >> stream\nX\nendstream endobj\n");
        int xrefOffset = source.Length;
        source.Append($"xref\n0 {streamNumber + 1}\n0000000000 65535 f\n");
        foreach (int offset in offsets.Skip(1))
            source.Append($"{offset:0000000000} 00000 n\n");
        source.Append($"trailer << /Size {streamNumber + 1} >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] DeepPageTreePdf(int nodeCount)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>
        {
            0,
            source.Length
        };
        source.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        for (int index = 0; index < nodeCount; index++)
        {
            int objectNumber = index + 2;
            offsets.Add(source.Length);
            if (index == nodeCount - 1)
            {
                source.Append($"{objectNumber} 0 obj << /Type /Page /Parent {objectNumber - 1} 0 R " +
                    "/MediaBox [0 0 10 10] >> endobj\n");
                continue;
            }
            string parent = index == 0 ? string.Empty : $" /Parent {objectNumber - 1} 0 R";
            source.Append($"{objectNumber} 0 obj << /Type /Pages /Kids [{objectNumber + 1} 0 R] " +
                $"/Count 1{parent} >> endobj\n");
        }
        int size = nodeCount + 2;
        int xrefOffset = source.Length;
        source.Append($"xref\n0 {size}\n0000000000 65535 f\n");
        foreach (int offset in offsets.Skip(1))
            source.Append($"{offset:0000000000} 00000 n\n");
        source.Append($"trailer << /Size {size} /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] ObjectStreamPdf(
        int firstCompressedIndex = 0,
        string header = "1 0 2 7 ",
        int objectCount = 2,
        string body = "(hello)<< /Answer 42 >>",
        bool indirectStructuralValues = false,
        bool indirectFilterValues = false,
        int objectStreamGeneration = 0,
        int? thirdCompressedIndex = null)
    {
        byte[] decodedObjectStreamData = [
            .. Encoding.ASCII.GetBytes(header), .. Encoding.ASCII.GetBytes(body)];
        byte[] objectStreamData = indirectFilterValues
            ? Encoding.ASCII.GetBytes(Convert.ToHexString(decodedObjectStreamData) + ">")
            : decodedObjectStreamData;
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n");
        int typeOffset = 0;
        int countOffset = 0;
        int firstOffset = 0;
        if (indirectStructuralValues)
        {
            typeOffset = checked((int)output.Position);
            WriteAscii(output, "3 0 obj /ObjStm endobj\n");
            countOffset = checked((int)output.Position);
            WriteAscii(output, $"4 0 obj {objectCount} endobj\n");
            firstOffset = checked((int)output.Position);
            WriteAscii(output, $"7 0 obj {header.Length} endobj\n");
        }
        int filterAliasOffset = 0;
        int filterOffset = 0;
        if (indirectFilterValues)
        {
            filterAliasOffset = checked((int)output.Position);
            WriteAscii(output, "8 0 obj 9 0 R endobj\n");
            filterOffset = checked((int)output.Position);
            WriteAscii(output, "9 0 obj /ASCIIHexDecode endobj\n");
        }
        int objectStreamOffset = checked((int)output.Position);
        string objectStreamType = indirectStructuralValues ? "3 0 R" : "/ObjStm";
        string objectStreamCount = indirectStructuralValues ? "4 0 R" : objectCount.ToString();
        string firstObject = indirectStructuralValues ? "7 0 R" : header.Length.ToString();
        string filter = indirectFilterValues ? " /Filter 8 0 R" : string.Empty;
        WriteAscii(
            output,
            $"5 {objectStreamGeneration} obj << /Type {objectStreamType} /N {objectStreamCount} /First {firstObject} /Length {objectStreamData.Length}{filter} >> stream\n");
        output.Write(objectStreamData);
        WriteAscii(output, "\nendstream endobj\n");

        int xrefOffset = checked((int)output.Position);
        var rowBytes = new List<byte>();
        rowBytes.AddRange(XrefRow(0, 0, 65_535));
        rowBytes.AddRange(XrefRow(2, 5, firstCompressedIndex));
        rowBytes.AddRange(XrefRow(2, 5, 1));
        rowBytes.AddRange(thirdCompressedIndex.HasValue
            ? XrefRow(2, 5, thirdCompressedIndex.Value)
            : indirectStructuralValues
                ? XrefRow(1, typeOffset, 0) : XrefRow(0, 0, 0));
        rowBytes.AddRange(indirectStructuralValues
            ? XrefRow(1, countOffset, 0) : XrefRow(0, 0, 0));
        rowBytes.AddRange(XrefRow(1, objectStreamOffset, objectStreamGeneration));
        rowBytes.AddRange(XrefRow(1, xrefOffset, 0));
        if (indirectStructuralValues)
            rowBytes.AddRange(XrefRow(1, firstOffset, 0));
        else if (indirectFilterValues)
            rowBytes.AddRange(XrefRow(0, 0, 0));
        if (indirectFilterValues)
        {
            rowBytes.AddRange(XrefRow(1, filterAliasOffset, 0));
            rowBytes.AddRange(XrefRow(1, filterOffset, 0));
        }
        byte[] rows = [.. rowBytes];
        int size = indirectFilterValues ? 10 : indirectStructuralValues ? 8 : 7;
        WriteAscii(output, $"6 0 obj << /Type /XRef /Size {size} /Root 1 0 R /W [1 4 2] /Length {rows.Length} >> stream\n");
        output.Write(rows);
        WriteAscii(output, $"\nendstream endobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] XrefRow(byte type, int field1, int field2) =>
    [
        type,
        (byte)(field1 >> 24),
        (byte)(field1 >> 16),
        (byte)(field1 >> 8),
        (byte)field1,
        (byte)(field2 >> 8),
        (byte)field2
    ];

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static string Text(PdfString value) => Encoding.ASCII.GetString(value.Bytes.Span);
}
