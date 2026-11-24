using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfIncrementalUpdateBuilderTests
{
    [Fact]
    public void Build_RejectsUndefinedCrossReferenceFormat()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfInteger(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => update.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = (PdfCrossReferenceFormat)int.MaxValue
            }));
    }

    [Fact]
    public void Build_DefaultsToTheCrossReferenceFormatTheSourceAlreadyUses()
    {
        // A classic table appended after a cross-reference stream is the shape Acrobat refused in a
        // KillerPDF OCR save; an update must continue the source's own format.
        byte[] streamBytes = PdfDocumentWriter.Write(PdfDocument.Open(new PdfDocumentBuilder().Build()),
            new PdfDocumentWriteOptions { CrossReferenceFormat = PdfCrossReferenceFormat.Stream });
        PdfDocument streamSource = PdfDocument.Open(streamBytes);
        Assert.True(streamSource.CrossReferences.Sections[0].IsStream);
        PdfDocument tableSource = PdfDocument.Open(new PdfDocumentBuilder().Build());
        Assert.False(tableSource.CrossReferences.Sections[0].IsStream);

        var streamUpdate = new PdfIncrementalUpdateBuilder(streamSource);
        streamUpdate.AddObject(new PdfInteger(1));
        var tableUpdate = new PdfIncrementalUpdateBuilder(tableSource);
        tableUpdate.AddObject(new PdfInteger(1));
        PdfDocument streamUpdated = PdfDocument.Open(streamUpdate.Build());
        PdfDocument tableUpdated = PdfDocument.Open(tableUpdate.Build());

        Assert.True(streamUpdated.CrossReferences.Sections[0].IsStream);
        Assert.False(tableUpdated.CrossReferences.Sections[0].IsStream);
    }

    [Fact]
    public void Build_PreservesSourceBytesAndAppendsResolvableRevision()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfDocument original = PdfDocument.Open(source);
        var rootReference = Assert.IsType<PdfIndirectReference>(original.Trailer[Name("Root")]);
        var root = Assert.IsType<PdfDictionary>(original.Resolve(rootReference));
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference customReference = update.AddObject(Latin1("incremental value"));
        var updatedRoot = new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("KillerTest"), customReference)));

        byte[] result = update.ReplaceObject(rootReference.ObjectNumber, updatedRoot).Build();
        PdfDocument reopened = PdfDocument.Open(result);
        var reopenedRoot = Assert.IsType<PdfDictionary>(reopened.Resolve(rootReference));
        var oldIds = Assert.IsType<PdfArray>(original.Trailer[Name("ID")]);
        var newIds = Assert.IsType<PdfArray>(reopened.Trailer[Name("ID")]);

        Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        Assert.Equal(2, reopened.CrossReferences.Sections.Count);
        Assert.Equal("incremental value", DecodeLatin1(
            Assert.IsType<PdfString>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(reopenedRoot[Name("KillerTest")])))));
        Assert.Equal(original.CrossReferences.StartXref.Offset,
            Assert.IsType<PdfInteger>(reopened.Trailer[Name("Prev")]).Value);
        Assert.Equal(
            Assert.IsType<PdfString>(oldIds[0]).Bytes.ToArray(),
            Assert.IsType<PdfString>(newIds[0]).Bytes.ToArray());
        Assert.NotEqual(
            Assert.IsType<PdfString>(oldIds[1]).Bytes.ToArray(),
            Assert.IsType<PdfString>(newIds[1]).Bytes.ToArray());
    }

    [Fact]
    public void ReservedObjects_CanReferToEachOther()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference first = update.ReserveObject();
        PdfIndirectReference second = update.ReserveObject();
        update.SetObject(first, new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Next"), second)]));
        update.SetObject(second, new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Prev"), first)]));

        PdfDocument reopened = PdfDocument.Open(update.Build());
        var firstValue = Assert.IsType<PdfDictionary>(reopened.Resolve(first));
        var secondValue = Assert.IsType<PdfDictionary>(reopened.Resolve(second));

        Assert.Equal(second.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(firstValue[Name("Next")]).ObjectNumber);
        Assert.Equal(first.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(secondValue[Name("Prev")]).ObjectNumber);
    }

    [Theory]
    [InlineData(PdfCrossReferenceFormat.Table)]
    [InlineData(PdfCrossReferenceFormat.Stream)]
    public void Build_PreservesApplicationTrailerEntriesAndDropsStaleChecksum(
        PdfCrossReferenceFormat format)
    {
        PdfDocument source = PdfDocument.Open(SourceWithTrailerState());
        var update = new PdfIncrementalUpdateBuilder(source);
        update.AddObject(new PdfInteger(1));

        PdfDocument reopened = PdfDocument.Open(update.Build(
            new PdfIncrementalUpdateWriteOptions { CrossReferenceFormat = format }));

        PdfDictionary state = Assert.IsType<PdfDictionary>(
            reopened.Trailer[Name("PrivateState")]);
        Assert.True(Assert.IsType<PdfBoolean>(state[Name("Enabled")]).Value);
        Assert.False(reopened.Trailer.ContainsKey(Name("DocChecksum")));
    }

    [Fact]
    public void Writers_PreserveApplicationTrailerStateInheritedFromOlderRevisions()
    {
        PdfDocument source = PdfDocument.Open(SourceWithInheritedTrailerState());
        Assert.False(source.Trailer.ContainsKey(Name("PrivateState")));
        Assert.True(source.CrossReferences.MergedTrailer.ContainsKey(Name("PrivateState")));

        PdfDocument incrementallyUpdated = PdfDocument.Open(
            new PdfIncrementalUpdateBuilder(source)
                .ReplaceObject(2, new PdfInteger(8))
                .Build());
        PdfDocument fullyRewritten = PdfDocument.Open(PdfDocumentWriter.Write(source));

        Assert.True(incrementallyUpdated.Trailer.ContainsKey(Name("PrivateState")));
        Assert.True(fullyRewritten.Trailer.ContainsKey(Name("PrivateState")));
        foreach (string structuralName in new[]
            { "Type", "W", "Index", "Length", "Filter" })
        {
            Assert.False(incrementallyUpdated.Trailer.ContainsKey(Name(structuralName)));
            Assert.False(fullyRewritten.Trailer.ContainsKey(Name(structuralName)));
        }

        PdfDocument staleInherited = PdfDocument.Open(
            SourceWithInheritedTrailerState("[2 1 R]"));
        InvalidOperationException incrementalError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalUpdateBuilder(staleInherited)
                .ReplaceObject(2, new PdfInteger(8))
                .Build());
        InvalidOperationException rewriteError = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.Write(staleInherited));
        Assert.Contains("Trailer /PrivateState value contains a stale indirect reference",
            incrementalError.Message, StringComparison.Ordinal);
        Assert.Contains("Trailer /PrivateState value contains a stale indirect reference",
            rewriteError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writers_UseNewestApplicationTrailerValueAcrossRevisions()
    {
        PdfDocument source = PdfDocument.Open(SourceWithInheritedTrailerState(
            "<< /Enabled false >>", "<< /Enabled true >>"));
        PdfDictionary mergedState = Assert.IsType<PdfDictionary>(
            source.CrossReferences.MergedTrailer[Name("PrivateState")]);
        Assert.True(Assert.IsType<PdfBoolean>(mergedState[Name("Enabled")]).Value);

        PdfDocument incrementallyUpdated = PdfDocument.Open(
            new PdfIncrementalUpdateBuilder(source)
                .ReplaceObject(2, new PdfInteger(8))
                .Build());
        PdfDocument fullyRewritten = PdfDocument.Open(PdfDocumentWriter.Write(source));

        Assert.True(Assert.IsType<PdfBoolean>(Assert.IsType<PdfDictionary>(
            incrementallyUpdated.Trailer[Name("PrivateState")])[Name("Enabled")]).Value);
        Assert.True(Assert.IsType<PdfBoolean>(Assert.IsType<PdfDictionary>(
            fullyRewritten.Trailer[Name("PrivateState")])[Name("Enabled")]).Value);
    }

    [Fact]
    public void Build_RejectsStaleApplicationTrailerReferences()
    {
        PdfDocument source = PdfDocument.Open(SourceWithTrailerState("[1 1 R]"));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.AddObject(new PdfInteger(1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("Trailer /PrivateState value contains a stale indirect reference",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsStaleCatalogRoot()
    {
        string sourceText = Encoding.ASCII.GetString(SourceWithTrailerState())
            .Replace("/Root 1 0 R", "/Root 1 1 R", StringComparison.Ordinal);
        var update = new PdfIncrementalUpdateBuilder(
            PdfDocument.Open(Encoding.ASCII.GetBytes(sourceText)));
        update.AddObject(new PdfInteger(1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("trailer /Root to reference a live catalog dictionary",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RejectsMalformedInheritedDocumentIdentifiersBeforeUpdate()
    {
        string sourceText = Encoding.ASCII.GetString(SourceWithTrailerState())
            .Replace("/DocChecksum", "/ID [<01>] /DocChecksum",
                StringComparison.Ordinal);
        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfDocument.Open(Encoding.ASCII.GetBytes(sourceText)));

        Assert.Contains("Trailer /ID must be an array of two strings",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ResolvesIndirectCatalogVersionNames()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(catalogReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference catalogType = update.AddObject(Name("Catalog"));
        PdfIndirectReference version = update.AddObject(Name("2.0"));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog
            .Where(entry => !entry.Key.Equals(Name("Type")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Type"), catalogType))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Version"), version))));

        PdfDocument reopened = PdfDocument.Open(update.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream
            }));

        Assert.True(reopened.CrossReferences.Sections[0].IsStream);
    }

    [Fact]
    public void Build_ValidatesCatalogVersionForClassicTables()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(catalogReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Version"), Name("Future")))));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("catalog /Version value is not a PDF version",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsInvalidReplacementDocumentInformationFields()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("Title"), new PdfInteger(17))
        ]));
        update.SetDocumentInformation(information);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("Trailer /Info /Title value is not a string",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PreservesMalformedInheritedDocumentInformationDuringUnrelatedUpdate()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int informationOffset = source.Length;
        source.Append("2 0 obj\n<< /Title 7 /ModDate (not-a-date) >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append($"{catalogOffset:0000000000} 00000 n\n");
        source.Append($"{informationOffset:0000000000} 00000 n\n");
        source.Append("trailer\n<< /Size 3 /Root 1 0 R /Info 2 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(
            Encoding.ASCII.GetBytes(source.ToString()));

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference marker = update.AddObject(new PdfInteger(1));
        PdfDocument reopened = PdfDocument.Open(update.Build());

        Assert.Equal(1, Assert.IsType<PdfInteger>(reopened.Resolve(marker)).Value);
        PdfIndirectReference informationReference = Assert.IsType<PdfIndirectReference>(
            reopened.Trailer[Name("Info")]);
        PdfDictionary information = Assert.IsType<PdfDictionary>(
            reopened.Resolve(informationReference));
        Assert.Equal(7, Assert.IsType<PdfInteger>(information[Name("Title")]).Value);
    }

    [Theory]
    [InlineData("D:20251301000000Z")]
    [InlineData("D:2026Z")]
    [InlineData("D:20260824120000+0700")]
    public void Build_RejectsInvalidReplacementDocumentInformationDates(string date)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("ModDate"), new PdfString(
                Encoding.ASCII.GetBytes(date), PdfStringForm.Literal))
        ]));
        update.SetDocumentInformation(information);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => update.Build());

        Assert.Contains("Trailer /Info /ModDate value is not a valid PDF date string",
            error.Message, StringComparison.Ordinal);
    }

    // #455: a PDF date is a text string, so a UTF-16BE /ModDate is as valid as an ASCII one.
    [Fact]
    public void Build_AcceptsReplacementDocumentInformationDatesWrittenAsUnicodeTextStrings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("ModDate"), new PdfString(
                [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("D:20260711135232Z")],
                PdfStringForm.Hexadecimal))
        ]));
        update.SetDocumentInformation(information);

        PdfDocument reopened = PdfDocument.Open(update.Build());

        Assert.NotNull(reopened.Trailer[Name("Info")]);
    }

    [Theory]
    [InlineData("D:2026")]
    [InlineData("D:202608")]
    [InlineData("D:20260824")]
    [InlineData("D:2026082412")]
    [InlineData("D:202608241234")]
    [InlineData("D:20240229123456")]
    [InlineData("D:20260824123456Z")]
    [InlineData("D:20260824123456-07'00'")]
    [InlineData("D:20260824123456+05'30")]
    [InlineData("D:20260824123456+05")]
    [InlineData("D:20260824123456+05'")]
    public void Build_AcceptsValidReplacementDocumentInformationDates(string date)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("ModDate"), new PdfString(
                Encoding.ASCII.GetBytes(date), PdfStringForm.Literal))
        ]));

        PdfDocument reopened = PdfDocument.Open(
            update.SetDocumentInformation(information).Build());

        Assert.IsType<PdfDictionary>(reopened.Resolve(information));
    }

    [Fact]
    public void Build_ResolvesIndirectReplacementDocumentInformationFields()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference title = update.AddObject(
            new PdfString("Indirect title"u8, PdfStringForm.Literal));
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("Title"), title)
        ]));

        PdfDocument reopened = PdfDocument.Open(
            update.SetDocumentInformation(information).Build());

        PdfDictionary rewrittenInformation = Assert.IsType<PdfDictionary>(
            reopened.Resolve(information));
        Assert.Equal("Indirect title", DecodeLatin1(Assert.IsType<PdfString>(
            reopened.Resolve(Assert.IsType<PdfIndirectReference>(
                rewrittenInformation[Name("Title")])))));
    }

    [Fact]
    public void Build_RejectsStaleCustomReplacementDocumentInformationGraphs()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference information = update.AddObject(new PdfDictionary([
            new(Name("Private"), new PdfArray([
                new PdfIndirectReference(999, 0)
            ]))
        ]));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            update.SetDocumentInformation(information).Build());

        Assert.Contains("Trailer /Info value contains a stale indirect reference",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingAnObject_PreservesItsCurrentGeneration()
    {
        byte[] source = SourceWithGenerationTwo();
        PdfDocument original = PdfDocument.Open(source);
        byte[] result = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(1, new PdfInteger(99))
            .Build();
        PdfDocument reopened = PdfDocument.Open(result);

        Assert.Equal(99, Assert.IsType<PdfInteger>(reopened.Resolve(new PdfIndirectReference(1, 2))).Value);
        Assert.IsType<PdfNull>(reopened.Resolve(new PdfIndirectReference(1, 0)));
    }

    [Fact]
    public void ReplacingAnObjectAgain_ComposesToTheLatestValue()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            original.Trailer[Name("Root")]);
        PdfDictionary root = Assert.IsType<PdfDictionary>(original.Resolve(rootReference));
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Stage"), new PdfInteger(1)))));
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Stage"), new PdfInteger(2)))));

        PdfDictionary reopened = Assert.IsType<PdfDictionary>(
            PdfDocument.Open(update.Build()).Resolve(rootReference));

        Assert.Equal(2, Assert.IsType<PdfInteger>(reopened[Name("Stage")]).Value);
    }

    [Theory]
    [InlineData(PdfCrossReferenceFormat.Table)]
    [InlineData(PdfCrossReferenceFormat.Stream)]
    public void FreeObject_EmitsLinkedFreeEntryWithAdvancedGeneration(
        PdfCrossReferenceFormat format)
    {
        PdfDocument original = PdfDocument.Open(SourceWithGenerationTwo());

        byte[] bytes = new PdfIncrementalUpdateBuilder(original)
            .FreeObject(1)
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = format,
                CompressCrossReferenceStream = format == PdfCrossReferenceFormat.Stream
            });
        PdfDocument reopened = PdfDocument.Open(bytes);
        PdfCrossReferenceSection newest = reopened.CrossReferences.Sections[0];

        Assert.Equal(PdfCrossReferenceEntryType.Free, newest[1].Type);
        Assert.Equal(3, newest[1].Field2);
        Assert.Equal(1, newest[0].Field1);
        Assert.IsType<PdfNull>(reopened.Resolve(new PdfIndirectReference(1, 2)));
    }

    [Fact]
    public void ReplaceAndFreeObject_ComposeToTheFinalAction()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[Name("Root")]);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.FreeObject(root.ObjectNumber);
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary([
            new(Name("Type"), Name("Catalog"))
        ]));

        PdfDocument reopened = PdfDocument.Open(update.Build());

        Assert.Equal(PdfCrossReferenceEntryType.InUse,
            reopened.CrossReferences[root.ObjectNumber].Type);
        Assert.IsType<PdfDictionary>(reopened.Resolve(root));
    }

    [Fact]
    public void FreeObject_CanSupersedeACompressedObject()
    {
        PdfDocument original = PdfDocument.Open(ObjectStreamPdf());

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original)
            .FreeObject(2)
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                CompressCrossReferenceStream = true
            }));

        Assert.Equal(PdfCrossReferenceEntryType.Free,
            reopened.CrossReferences[2].Type);
        Assert.Equal(1, reopened.CrossReferences[2].Field2);
        Assert.IsType<PdfNull>(reopened.Resolve(2));
    }

    [Fact]
    public void FreeObject_ProtectsPermanentTrailerRootsAndRemovesInheritedInfo()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[Name("Root")]);
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalUpdateBuilder(original).FreeObject(root.ObjectNumber).Build());

        var addInfo = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference info = addInfo.AddObject(new PdfDictionary([
            new(Name("Title"), Latin1("temporary"))
        ]));
        PdfDocument withInfo = PdfDocument.Open(addInfo.SetDocumentInformation(info).Build());
        PdfDocument withoutInfo = PdfDocument.Open(new PdfIncrementalUpdateBuilder(withInfo)
            .FreeObject(info.ObjectNumber).Build());

        Assert.False(withoutInfo.Trailer.ContainsKey(Name("Info")));
        Assert.Equal(PdfCrossReferenceEntryType.Free,
            withoutInfo.CrossReferences[info.ObjectNumber].Type);

        var aliasUpdate = new PdfIncrementalUpdateBuilder(withInfo);
        PdfIndirectReference infoAlias = aliasUpdate.AddObject(info);
        PdfIndirectReference infoSecondAlias = aliasUpdate.AddObject(infoAlias);
        PdfDocument aliasedInfo = PdfDocument.Open(aliasUpdate
            .SetDocumentInformation(infoSecondAlias).Build());
        PdfDocument removedAliasedInfo = PdfDocument.Open(
            new PdfIncrementalUpdateBuilder(aliasedInfo)
                .FreeObject(info.ObjectNumber).Build());
        Assert.False(removedAliasedInfo.Trailer.ContainsKey(Name("Info")));
        Assert.Equal(PdfCrossReferenceEntryType.Free,
            removedAliasedInfo.CrossReferences[info.ObjectNumber].Type);

        var restore = new PdfIncrementalUpdateBuilder(aliasedInfo);
        restore.FreeObject(info.ObjectNumber);
        restore.ReplaceObject(info.ObjectNumber, new PdfDictionary([
            new(Name("Title"), Latin1("restored"))
        ]));
        PdfDocument restored = PdfDocument.Open(restore.Build());
        PdfObject restoredValue = restored.Trailer[Name("Info")];
        while (restoredValue is PdfIndirectReference reference)
            restoredValue = restored.Resolve(reference);
        Assert.Equal("restored", Encoding.Latin1.GetString(
            Assert.IsType<PdfString>(Assert.IsType<PdfDictionary>(
                restoredValue)[Name("Title")]).Bytes.Span));

        var redirect = new PdfIncrementalUpdateBuilder(aliasedInfo);
        PdfIndirectReference replacementInfo = redirect.AddObject(new PdfDictionary([
            new(Name("Title"), Latin1("redirected"))
        ]));
        redirect.ReplaceObject(infoSecondAlias.ObjectNumber, replacementInfo);
        redirect.FreeObject(info.ObjectNumber);
        PdfDocument redirected = PdfDocument.Open(redirect.Build());
        PdfObject redirectedValue = redirected.Trailer[Name("Info")];
        while (redirectedValue is PdfIndirectReference reference)
            redirectedValue = redirected.Resolve(reference);
        Assert.Equal("redirected", Encoding.Latin1.GetString(
            Assert.IsType<PdfString>(Assert.IsType<PdfDictionary>(
                redirectedValue)[Name("Title")]).Bytes.Span));
    }

    [Fact]
    public void FreeObject_RejectsAStaleInheritedInformationRegistration()
    {
        PdfDocument source = PdfDocument.Open(SourceWithStaleInformationReference());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalUpdateBuilder(source)
                .FreeObject(2)
                .Build());

        Assert.Contains("trailer /Info to reference a live dictionary",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeObject_LinksNewEntriesToTheInheritedFreeChain()
    {
        PdfDocument original = PdfDocument.Open(SourceWithExistingFreeHead());

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original)
            .FreeObject(2)
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream
            }));
        PdfCrossReferenceSection newest = reopened.CrossReferences.Sections[0];

        Assert.Equal(2, newest[0].Field1);
        Assert.Equal(1, newest[2].Field1);
        Assert.Equal(PdfCrossReferenceEntryType.Free, reopened.CrossReferences[1].Type);
    }

    [Fact]
    public void DistinctFreeOnlyRevisions_ReceiveDistinctRevisionIdentifiers()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var seed = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference first = seed.AddObject(new PdfInteger(1));
        PdfIndirectReference second = seed.AddObject(new PdfInteger(2));
        PdfDocument seeded = PdfDocument.Open(seed.Build());

        PdfDocument freedFirst = PdfDocument.Open(
            new PdfIncrementalUpdateBuilder(seeded).FreeObject(first.ObjectNumber).Build());
        PdfDocument freedSecond = PdfDocument.Open(
            new PdfIncrementalUpdateBuilder(seeded).FreeObject(second.ObjectNumber).Build());
        PdfArray firstIds = Assert.IsType<PdfArray>(freedFirst.Trailer[Name("ID")]);
        PdfArray secondIds = Assert.IsType<PdfArray>(freedSecond.Trailer[Name("ID")]);

        Assert.NotEqual(
            Assert.IsType<PdfString>(firstIds[1]).Bytes.ToArray(),
            Assert.IsType<PdfString>(secondIds[1]).Bytes.ToArray());
    }

    [Fact]
    public void Build_IsDeterministicForTheSameSourceAndChanges()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        static byte[] Update(PdfDocument document) => new PdfIncrementalUpdateBuilder(document)
            .AddAndBuild(new PdfInteger(42));

        Assert.Equal(Update(original), Update(original));
    }

    [Fact]
    public void Build_CanReplaceAndRemoveDocumentInformation()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var firstUpdate = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference information = firstUpdate.AddObject(new PdfDictionary([
            new(Name("Title"), Latin1("Replacement title"))
        ]));
        PdfDocument replaced = PdfDocument.Open(
            firstUpdate.SetDocumentInformation(information).Build());
        PdfDictionary replacedInformation = Assert.IsType<PdfDictionary>(replaced.Resolve(
            Assert.IsType<PdfIndirectReference>(replaced.Trailer[Name("Info")])));

        var secondUpdate = new PdfIncrementalUpdateBuilder(replaced);
        secondUpdate.AddObject(new PdfInteger(1));
        PdfDocument removed = PdfDocument.Open(
            secondUpdate.SetDocumentInformation(null).Build());

        Assert.Equal("Replacement title", DecodeLatin1(
            Assert.IsType<PdfString>(replacedInformation[Name("Title")])));
        Assert.False(removed.Trailer.ContainsKey(Name("Info")));
    }

    [Fact]
    public void Build_RejectsEmptyOrIncompleteUpdates()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalUpdateBuilder(original).Build());
        var incomplete = new PdfIncrementalUpdateBuilder(original);
        incomplete.ReserveObject();
        Assert.Throws<InvalidOperationException>(() => incomplete.Build());
        Assert.Throws<ArgumentException>(() =>
            new PdfIncrementalUpdateBuilder(original).ReplaceObject(999, new PdfInteger(1)));
    }

    [Fact]
    public void Build_AppendsClassicRevisionToXrefStreamAndReplacesCompressedObject()
    {
        PdfDocument original = PdfDocument.Open(ObjectStreamPdf());
        // A classic revision after a stream is now only written when a caller asks for it explicitly.
        byte[] result = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(2, new PdfDictionary([
                new KeyValuePair<PdfName, PdfObject>(Name("Updated"), new PdfBoolean(true))]))
            .Build(new PdfIncrementalUpdateWriteOptions { CrossReferenceFormat = PdfCrossReferenceFormat.Table });
        PdfDocument reopened = PdfDocument.Open(result);
        var updated = Assert.IsType<PdfDictionary>(reopened.Resolve(2));

        Assert.True(Assert.IsType<PdfBoolean>(updated[Name("Updated")]).Value);
        Assert.Equal(2, reopened.CrossReferences.Sections.Count);
        Assert.False(reopened.Trailer.ContainsKey(Name("Type")));
        Assert.False(reopened.Trailer.ContainsKey(Name("W")));
        Assert.Equal(original.CrossReferences.StartXref.Offset,
            Assert.IsType<PdfInteger>(reopened.Trailer[Name("Prev")]).Value);
    }

    [Fact]
    public void Build_AppendsCompressedSparseCrossReferenceStream()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var rootReference = Assert.IsType<PdfIndirectReference>(original.Trailer[Name("Root")]);
        var root = Assert.IsType<PdfDictionary>(original.Resolve(rootReference));
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference added = update.AddObject(Latin1("stream revision"));
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("KillerTest"), added))));

        byte[] result = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            CompressCrossReferenceStream = true
        });
        PdfDocument reopened = PdfDocument.Open(result);
        PdfCrossReferenceSection newest = reopened.CrossReferences.Sections[0];
        PdfDictionary reopenedRoot = Assert.IsType<PdfDictionary>(reopened.Resolve(rootReference));

        Assert.True(newest.IsStream);
        Assert.Equal(original.CrossReferences.StartXref.Offset, newest.PreviousOffset);
        Assert.Equal("stream revision", DecodeLatin1(Assert.IsType<PdfString>(
            reopened.Resolve(Assert.IsType<PdfIndirectReference>(reopenedRoot[Name("KillerTest")])))));
        PdfArray index = Assert.IsType<PdfArray>(newest.Trailer[Name("Index")]);
        Assert.True(index.Count >= 4);
        Assert.Equal("/FlateDecode", Assert.IsType<PdfName>(newest.Trailer[Name("Filter")]).ToString());
    }

    [Fact]
    public void CrossReferenceStreamBuild_IsDeterministic()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        static byte[] Update(PdfDocument document)
        {
            var update = new PdfIncrementalUpdateBuilder(document);
            update.AddObject(new PdfInteger(42));
            return update.Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                CompressCrossReferenceStream = true
            });
        }

        Assert.Equal(Update(original), Update(original));
    }

    [Fact]
    public void CrossReferenceStreamCompression_RequiresStreamFormat()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(original);
        update.AddObject(new PdfInteger(1));

        Assert.Throws<InvalidOperationException>(() => update.Build(
            new PdfIncrementalUpdateWriteOptions { CompressCrossReferenceStream = true }));
    }

    [Fact]
    public void CrossReferenceStream_RequiresPdf15OrLater()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        source[5] = (byte)'1';
        source[7] = (byte)'4';
        PdfDocument original = PdfDocument.Open(source);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.AddObject(new PdfInteger(1));

        Assert.Throws<InvalidOperationException>(() => update.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream
            }));
    }

    [Fact]
    public void CrossReferenceStream_HonorsCatalogVersionOverride()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        source[5] = (byte)'1';
        source[7] = (byte)'4';
        PdfDocument original = PdfDocument.Open(source);
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            original.Trailer[Name("Root")]);
        PdfDictionary root = Assert.IsType<PdfDictionary>(original.Resolve(rootReference));
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Version"), Name("1.5")))));

        PdfDocument reopened = PdfDocument.Open(update.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream
            }));

        Assert.True(reopened.CrossReferences.Sections[0].IsStream);
    }

    [Fact]
    public void CrossReferenceStream_DoesNotUseReplacementBehindStaleRootGeneration()
    {
        var source = new StringBuilder("%PDF-1.4\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 2 /Root 1 1 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        PdfDocument original = PdfDocument.Open(
            Encoding.ASCII.GetBytes(source.ToString()));
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(1, new PdfDictionary([
            new(Name("Type"), Name("Catalog")),
            new(Name("Version"), Name("1.5"))
        ]));

        Assert.Throws<InvalidOperationException>(() => update.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream
            }));
    }

    [Fact]
    public void CrossReferenceStream_CanPackBoundedIncrementalObjectStreams()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference[] references = [.. Enumerable.Range(0, 205).Select(value => update.AddObject(new PdfInteger(value)))];

        byte[] bytes = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        });
        PdfDocument reopened = PdfDocument.Open(bytes);
        PdfCrossReferenceSection newest = reopened.CrossReferences.Sections[0];

        Assert.Equal(204, Assert.IsType<PdfInteger>(reopened.Resolve(references[^1])).Value);
        Assert.All(references, reference => Assert.Equal(
            PdfCrossReferenceEntryType.Compressed, newest[reference.ObjectNumber].Type));
        Assert.Equal(3, newest.Values.Count(entry =>
            entry.Type == PdfCrossReferenceEntryType.InUse
            && reopened.Resolve(entry.ObjectNumber) is PdfStream stream
            && stream.Dictionary.TryGetValue(Name("Type"), out PdfObject? type)
            && type is PdfName name && name.Equals(Name("ObjStm"))));
    }

    [Fact]
    public void ObjectStreamOptions_RequireCompatibleSettings()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var tableUpdate = new PdfIncrementalUpdateBuilder(original);
        tableUpdate.AddObject(new PdfInteger(1));
        Assert.Throws<InvalidOperationException>(() => tableUpdate.Build(
            new PdfIncrementalUpdateWriteOptions { UseObjectStreams = true }));

        var compressionUpdate = new PdfIncrementalUpdateBuilder(original);
        compressionUpdate.AddObject(new PdfInteger(1));
        Assert.Throws<InvalidOperationException>(() => compressionUpdate.Build(
            new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                CompressObjectStreams = true
            }));
    }

    [Fact]
    public void IncrementalObjectStream_CanSupersedeAnExistingGenerationZeroObjectDeterministically()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            original.Trailer[Name("Root")]);
        PdfDictionary root = Assert.IsType<PdfDictionary>(original.Resolve(rootReference));
        var replacement = new PdfDictionary(root.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Packed"), new PdfBoolean(true))));
        static PdfIncrementalUpdateWriteOptions Options() => new()
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        };
        byte[] Build() => new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(rootReference.ObjectNumber, replacement).Build(Options());

        byte[] first = Build();
        byte[] second = Build();
        PdfDocument reopened = PdfDocument.Open(first);
        PdfDictionary reopenedRoot = Assert.IsType<PdfDictionary>(reopened.Resolve(rootReference));

        Assert.Equal(first, second);
        Assert.Equal(PdfCrossReferenceEntryType.Compressed,
            reopened.CrossReferences[rootReference.ObjectNumber].Type);
        Assert.True(Assert.IsType<PdfBoolean>(reopenedRoot[Name("Packed")]).Value);
    }

    private static byte[] SourceWithGenerationTwo()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append("1 2 obj\n7\nendobj\n");
        int catalogOffset = source.Length;
        source.Append("2 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f \n");
        source.Append($"{objectOffset:0000000000} 00002 n \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 3 /Root 2 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] SourceWithStaleInformationReference()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int valueOffset = source.Length;
        source.Append("2 0 obj\n<< /Title (active object) >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append($"{valueOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 3 /Root 1 0 R /Info 2 1 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] SourceWithTrailerState(
        string privateState = "<< /Enabled true >>")
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 2 /Root 1 0 R " +
            $"/PrivateState {privateState} /DocChecksum <01020304> >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] SourceWithInheritedTrailerState(
        string privateState = "<< /Enabled true >>",
        string? latestPrivateState = null)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int catalogOffset = source.Length;
        source.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int firstXrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 2 /Root 1 0 R " +
            $"/PrivateState {privateState} /Type /XRef /W [1 2 1] " +
            "/Index [0 2] /Length 9 /Filter /FlateDecode >>\n");
        source.Append($"startxref\n{firstXrefOffset}\n%%EOF\n");
        int valueOffset = source.Length;
        source.Append("2 0 obj\n7\nendobj\n");
        int latestXrefOffset = source.Length;
        source.Append("xref\n2 1\n");
        source.Append($"{valueOffset:0000000000} 00000 n \n");
        source.Append($"trailer\n<< /Size 3 /Root 1 0 R /Prev {firstXrefOffset}");
        if (latestPrivateState is not null)
            source.Append($" /PrivateState {latestPrivateState}");
        source.Append(" >>\n");
        source.Append($"startxref\n{latestXrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] SourceWithExistingFreeHead()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int valueOffset = source.Length;
        source.Append("2 0 obj\n7\nendobj\n");
        int catalogOffset = source.Length;
        source.Append("3 0 obj\n<< /Type /Catalog >>\nendobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 4\n0000000001 65535 f \n");
        source.Append("0000000000 00001 f \n");
        source.Append($"{valueOffset:0000000000} 00000 n \n");
        source.Append($"{catalogOffset:0000000000} 00000 n \n");
        source.Append("trailer\n<< /Size 4 /Root 3 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] ObjectStreamPdf()
    {
        byte[] catalog = "<< /Type /Catalog >>"u8.ToArray();
        byte[] header = Encoding.ASCII.GetBytes($"1 0 2 {catalog.Length} ");
        byte[] body = [.. catalog, .. "<< /Answer 42 >>"u8.ToArray()];
        byte[] objectStreamData = [.. header, .. body];
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-2.0\n");
        int objectStreamOffset = checked((int)output.Position);
        WriteAscii(output,
            $"5 0 obj << /Type /ObjStm /N 2 /First {header.Length} /Length {objectStreamData.Length} >> stream\n");
        output.Write(objectStreamData);
        WriteAscii(output, "\nendstream endobj\n");
        int xrefOffset = checked((int)output.Position);
        byte[] rows =
        [
            .. XrefRow(0, 0, 65_535),
            .. XrefRow(2, 5, 0),
            .. XrefRow(2, 5, 1),
            .. XrefRow(0, 0, 0),
            .. XrefRow(0, 0, 0),
            .. XrefRow(1, objectStreamOffset, 0),
            .. XrefRow(1, xrefOffset, 0)
        ];
        WriteAscii(output,
            $"6 0 obj << /Type /XRef /Size 7 /Root 1 0 R /W [1 4 2] /Length {rows.Length} >> stream\n");
        output.Write(rows);
        WriteAscii(output, $"\nendstream endobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] XrefRow(byte type, int field1, int field2) =>
    [
        type,
        (byte)(field1 >> 24), (byte)(field1 >> 16), (byte)(field1 >> 8), (byte)field1,
        (byte)(field2 >> 8), (byte)field2
    ];

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));

    private static PdfString Latin1(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static string DecodeLatin1(PdfString value) => Encoding.Latin1.GetString(value.Bytes.Span);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

internal static class PdfIncrementalUpdateBuilderTestExtensions
{
    public static byte[] AddAndBuild(this PdfIncrementalUpdateBuilder builder, PdfObject value)
    {
        builder.AddObject(value);
        return builder.Build();
    }
}
