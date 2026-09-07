using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class PdfFontResourceReaderTests
{
    private static readonly PdfDocument Document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

    [Fact]
    public void CompositeSubstitutionDoesNotTreatAnUnmappedGlyphIdAsUnicode()
    {
        PdfStream unicode = Stream("1 begincodespacerange <0000> <ffff> endcodespacerange "
            + "1 beginbfchar <0041> <dbffdfff> endbfchar");
        var font = Read(Type0(D(), N("Identity-H"), unicode));
        Assert.Equal(char.ConvertFromUtf32(0x10ffff), Assert.Single(font.Decode(new byte[] { 0, 65 })).Text);
        Assert.Null(font.GetGlyphOutline(65));
    }

    [Theory]
    [InlineData(0, "Identity-H")]
    [InlineData(1, "Identity-H")]
    [InlineData(2, "Identity-H")]
    [InlineData(3, "Identity-V")]
    public void RecoveryUsesUnicodeAndFallbackOutlinesWhenDescendantIsMissing(int shape, string encoding)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Subtype", N("Type0")), ("BaseFont", N("TestCID")), ("Encoding", N(encoding)),
            ("ToUnicode", Stream("1 begincodespacerange <0000> <ffff> endcodespacerange "
                + "1 beginbfchar <0041> <0041> endbfchar"))
        };
        if (shape > 0) entries.Add(("DescendantFonts", shape switch
        {
            1 => PdfNull.Instance,
            2 => new PdfArray([]),
            _ => new PdfArray([PdfNull.Instance])
        }));
        var resource = D(entries.ToArray());
        Assert.Throws<FormatException>(() => Read(resource));
        var recovery = PdfDocument.OpenWithCompatibilityRecovery(new PdfDocumentBuilder().AddBlankPage().Build());
        var resolver = new TestFontResolver(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        PdfExtractionFont font = PdfFontResourceReader.Read(recovery, resource, resolver);
        Assert.Equal("A", Assert.Single(font.Decode(new byte[] { 0, 65 })).Text);
        Assert.Equal(1000, font.GetWidth(65));
        Assert.Equal(encoding == "Identity-V", font.IsVertical);
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
        Assert.Single(resolver.Requests);
    }

    [Theory]
    [InlineData(false, "Identity-H")]
    [InlineData(true, "UnknownEncoding")]
    public void RecoveryRequiresUnicodeAndIdentityEncodingForMissingDescendant(bool unicode, string encoding)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Subtype", N("Type0")), ("Encoding", N(encoding))
        };
        if (unicode) entries.Add(("ToUnicode", Stream("1 begincodespacerange <0000> <ffff> endcodespacerange")));
        var recovery = PdfDocument.OpenWithCompatibilityRecovery(new PdfDocumentBuilder().AddBlankPage().Build());
        Assert.Throws<FormatException>(() => PdfFontResourceReader.Read(recovery, D(entries.ToArray())));
    }

    [Fact]
    public void StandardHelveticaUsesStandardEncodingAndExactWidths()
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica"))));
        Assert.Equal("Helvetica", font.FontName);
        Assert.Equal("AW i", string.Concat(font.Decode("AW i"u8.ToArray()).Select(c => c.Text)));
        Assert.Equal(667, font.GetWidth(65));
        Assert.Equal(944, font.GetWidth(87));
        Assert.Equal(222, font.GetWidth(105));
        Assert.Equal(new PdfGlyphBounds(14, 0, 654, 718), font.GetGlyphBounds(65));
    }

    [Theory]
    [InlineData("ArialMT", 667)]
    [InlineData("ABCDEF+Arial-BoldMT", 722)]
    [InlineData("TimesNewRomanPSMT", 722)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", 667)]
    [InlineData("CourierNewPSMT", 600)]
    [InlineData("CourierNewPS-BoldItalicMT", 600)]
    public void CoreFontAliasesUseCanonicalMetrics(string fontName, double expectedWidth)
    {
        PdfExtractionFont font = Read(D(
            ("Subtype", N("Type1")), ("BaseFont", N(fontName))));

        Assert.Equal(expectedWidth, font.GetWidth(65));
        Assert.NotNull(font.GetGlyphBounds(65));
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Theory]
    [InlineData("MyriadPro-Regular")]
    [InlineData("MyriadPro-Semibold")]
    [InlineData("MyriadPro-It")]
    [InlineData("MinionPro-BoldIt")]
    [InlineData("TimesNewRomanPSMT")]
    [InlineData("CustomMono-Bold")]
    public void MissingSimpleFontsReceiveBundledStyleCompatibleOutlines(string fontName)
    {
        PdfExtractionFont font = Read(D(
            ("Subtype", N("Type1")), ("BaseFont", N(fontName))));

        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Fact]
    public void EmbeddedWindowsSymbolFontUsesPrivateUseCharacterMapping()
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true, symbolCmap: true);
        int cmap = FindTable(bytes, "cmap");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(cmap + 14), 38);
        PdfExtractionFont font = Read(D(("Subtype", N("TrueType")),
            ("BaseFont", N("SubsetSymbol")),
            ("FontDescriptor", D(("FontFile2", new PdfStream(D(), bytes))))));

        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(31)).Contours);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(0, 0)]
    public void SymbolicFontDistinguishesEncodedAndUnicodeCharacterMaps(int platform, int expectedLeft)
    {
        var cmap = new byte[274];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), (ushort)platform);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14), 262);
        cmap[18 + 33] = 1;
        cmap[18 + 75] = 2;
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true,
            includeCompound: true, cmap: cmap);
        PdfExtractionFont font = Read(D(("Subtype", N("TrueType")),
            ("BaseFont", N("SubsetSymbol")),
            ("FontDescriptor", D(("Flags", new PdfInteger(4)),
                ("FontFile2", new PdfStream(D(), bytes)))),
            ("ToUnicode", Stream("1 begincodespacerange <00> <FF> endcodespacerange "
                + "1 beginbfchar <21> <004B> endbfchar"))));

        Assert.Equal("K", Assert.Single(font.Decode(new byte[] { 33 })).Text);
        Assert.Equal(expectedLeft, font.GetGlyphBounds(33)!.Value.Left);
    }

    [Fact]
    public void MissingCompositeFontUsesItsUnicodeMapWithBundledOutlines()
    {
        PdfStream unicode = Stream(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 beginbfchar <0041> <0041> endbfchar");
        PdfExtractionFont font = Read(Type0(D(), N("Identity-H"), unicode));

        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Fact]
    public void MissingIdentityFontRecoversInvalidUnicodeMappingsWithBundledOutlines()
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfStream unicode = Stream(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 beginbfchar <0041> <D800> endbfchar");

        PdfExtractionFont font = PdfFontResourceReader.Read(document,
            Type0(D(), N("Identity-H"), unicode));

        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Fact]
    public void MissingCompositeFontUsesInjectedPlatformNeutralFontBytes()
    {
        var resolver = new TestFontResolver(
            TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var descendant = D(("Subtype", N("CIDFontType2")),
            ("CIDSystemInfo", D(("Registry", Text("Adobe")),
                ("Ordering", Text("Identity")))));
        PdfStream unicode = Stream(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange "
            + "1 beginbfchar <0041> <0041> endbfchar");

        PdfExtractionFont font = PdfFontResourceReader.Read(Document,
            Type0(descendant, N("Identity-V"), unicode), resolver);

        Assert.Equal(new PdfFontRequest("TestCID", "Adobe", "Identity", true),
            Assert.Single(resolver.Requests));
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Fact]
    public void MissingSimpleFontUsesInjectedPlatformNeutralFontBytes()
    {
        var resolver = new TestFontResolver(
            TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));

        PdfExtractionFont font = PdfFontResourceReader.Read(Document,
            D(("Subtype", N("TrueType")), ("BaseFont", N("KillerSimple")),
                ("Encoding", N("WinAnsiEncoding"))), resolver);

        Assert.Equal(new PdfFontRequest("KillerSimple", "Adobe", "Standard", false),
            Assert.Single(resolver.Requests));
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Theory]
    [InlineData("WinAnsiEncoding", 128, "\u20AC")]
    [InlineData("MacRomanEncoding", 128, "\u00C4")]
    [InlineData("StandardEncoding", 174, "\uFB01")]
    public void DecodesSimpleEncodings(string encoding, int code, string expected)
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", N(encoding))));
        Assert.Equal(expected, Assert.Single(font.Decode(new byte[] { (byte)code })).Text);
    }

    [Fact]
    public void CompatibilityRecoveryAcceptsMisspelledWinAnsiEncoding()
    {
        PdfDictionary dictionary = D(("Subtype", N("Type1")),
            ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEcoding")));
        Assert.Throws<NotSupportedException>(() => Read(dictionary));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());

        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);

        Assert.Equal("\u20AC", Assert.Single(font.Decode(new byte[] { 128 })).Text);
    }

    [Theory]
    [InlineData("Symbol", 65, "\u0391")]
    [InlineData("ZapfDingbats", 33, "\u2701")]
    public void HonorsBuiltInSymbolAndDingbatEncoding(string name, int code, string expected)
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N(name))));
        Assert.Equal(expected, Assert.Single(font.Decode(new byte[] { (byte)code })).Text);
        Assert.True(font.GetWidth((uint)code) > 0);
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline((uint)code)).Contours);
    }

    [Fact]
    public void DifferencesResolveGlyphNamesLigaturesSuffixesAndUnicodeNames()
    {
        var encoding = D(("BaseEncoding", N("WinAnsiEncoding")), ("Differences", new PdfArray([
            new PdfInteger(65), N("Aacute"), N("f_f_i"), N("uni03A9"), N("u1F600"), N("A.alt")])));
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("Encoding", encoding)));
        Assert.Equal(["\u00C1", "ffi", "\u03A9", "\U0001F600", "A"], font.Decode("ABCDE"u8.ToArray()).Select(c => c.Text));
    }

    [Fact]
    public void ToUnicodeOverridesEncodingWhileMissingMappingsUseEncoding()
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <03A9> endbfchar"))));
        Assert.Equal(["\u03A9", "B"], font.Decode("AB"u8.ToArray()).Select(c => c.Text));
    }

    [Fact]
    public void ToUnicodeStreamInheritsAndOverridesBaseMappings()
    {
        PdfStream inherited = Stream(
            "1 begincodespacerange <00> <FF> endcodespacerange "
            + "2 beginbfchar <41> <0041> <42> <0042> endbfchar");
        var dictionary = D(("UseCMap", inherited));
        var derived = new PdfStream(dictionary, Encoding.ASCII.GetBytes(
            "/Base usecmap 2 beginbfchar <42> <03A9> <43> <0043> endbfchar"));

        PdfExtractionFont font = Read(D(("Subtype", N("Type1")),
            ("BaseFont", N("Helvetica")), ("ToUnicode", derived)));

        Assert.Equal(["A", "\u03A9", "C"],
            font.Decode("ABC"u8.ToArray()).Select(character => character.Text));
    }

    [Fact]
    public void ToUnicodeStreamInheritsAdobeCollectionUnicodeMap()
    {
        var derived = new PdfStream(D(("UseCMap", N("Adobe-Japan1-UCS2"))),
            Encoding.ASCII.GetBytes(
                "/Adobe-Japan1-UCS2 usecmap 1 beginbfchar <0001> <03A9> endbfchar"));
        PdfExtractionFont font = Read(Type0(D(), N("Identity-H"), derived));

        Assert.Equal(["\u03A9", "!"], font.Decode(new byte[] { 0, 1, 0, 2 })
            .Select(character => character.Text));
    }

    [Fact]
    public void ToUnicodeStreamRejectsUnknownNamedDictionaryInheritance()
    {
        var derived = new PdfStream(D(("UseCMap", N("Unknown-UCS2"))),
            Encoding.ASCII.GetBytes("/Unknown-UCS2 usecmap"));

        Assert.Throws<NotSupportedException>(() => Read(Type0(D(), N("Identity-H"), derived)));
    }

    [Fact]
    public void ExplicitSpaceGlyphRemainsBlankWhenToUnicodeMapsAControlCharacter()
    {
        var encoding = D(("BaseEncoding", N("WinAnsiEncoding")),
            ("Differences", new PdfArray([new PdfInteger(2), N("space")])));
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Times-Roman")),
            ("Encoding", encoding), ("FirstChar", new PdfInteger(2)),
            ("Widths", new PdfArray([new PdfInteger(250)])),
            ("ToUnicode", Stream("1 begincodespacerange <00> <FF> endcodespacerange "
                + "1 beginbfchar <02> <0002> endbfchar"))));

        Assert.Equal("\u0002", Assert.Single(font.Decode(new byte[] { 2 })).Text);
        Assert.Empty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(2)).Contours);
    }

    [Fact]
    public void SimpleFontUsesOneByteCodesDespiteIncorrectDeclaredToUnicodeCodeSpace()
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <41> <03A9> <42> <0042> endbfchar"))));
        Assert.Equal(["\u03A9", "B"], font.Decode("AB"u8.ToArray()).Select(c => c.Text));
    }

    [Fact]
    public void ToUnicodeMetadataAcceptsPostScriptDefinitionsWithoutChangingMappings()
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("/CIDSystemInfo << /Registry (Example >>) def /Ordering (UCS) def /Supplement 0 def >> def " +
                "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <03A9> endbfchar"))));
        Assert.Equal("\u03A9", Assert.Single(font.Decode("A"u8.ToArray())).Text);
    }

    [Theory]
    [InlineData("/CIDSystemInfo 21 0 R def ", true)]
    [InlineData("/CIDSystemInfo 21 % reference\n 0 R def ", true)]
    [InlineData("/OtherInfo 21 0 R def ", false)]
    [InlineData("/CIDSystemInfo 21 0 R pop ", false)]
    [InlineData("1 begincidchar <0041> 5 endcidchar /CIDSystemInfo 21 0 R def ", false)]
    [InlineData("/CIDSystemInfo 21 65536 R def ", false)]
    public void RecoverySkipsOnlyPreMappingCidSystemInfoReferences(string metadata,
        bool recoverable)
    {
        var descendant = D(("W", new PdfArray([
            new PdfInteger(5), new PdfInteger(5), new PdfInteger(321)])));
        PdfDictionary dictionary = Type0(descendant,
            Stream(metadata + "1 begincodespacerange <0000> <FFFF> endcodespacerange "
                + "1 begincidchar <0041> 5 endcidchar"),
            Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange "
                + "1 beginbfchar <0041> <03A9> endbfchar"));
        Assert.ThrowsAny<FormatException>(() => Read(dictionary));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        if (!recoverable)
        {
            Assert.ThrowsAny<FormatException>(() => PdfFontResourceReader.Read(document, dictionary));
            return;
        }
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);
        Assert.Equal("\u03A9", Assert.Single(font.Decode(new byte[] { 0, 65 })).Text);
        Assert.Equal(321, font.GetWidth(65));
    }

    [Fact]
    public void RecoveryIgnoresStrayDelimiterInCMapNameButPreservesMappings()
    {
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("/CMapName /Adobe-Identity-UC> def "
                + "1 begincodespacerange <00> <FF> endcodespacerange "
                + "1 beginbfchar <41> <03A9> endbfchar")));
        Assert.Throws<KillerPdf.Engine.Syntax.PdfSyntaxException>(() => Read(dictionary));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);
        Assert.Equal("\u03A9", Assert.Single(font.Decode("A"u8.ToArray())).Text);
    }

    [Theory]
    [InlineData("/OtherName /Broken> def")]
    [InlineData("/OtherName /Broken Family - Panton.otf,000-UTF16 def")]
    [InlineData("/CMapName /Broken Family - Panton.otf,000-UTF16")]
    [InlineData("/CMapName /Broken Family -\nPanton.otf,000-UTF16 def")]
    [InlineData("/CMapName /Broken 1 beginbfchar - def")]
    [InlineData("1 begincodespacerange <00> <FF> endcodespacerange /CMapName /Broken Family - def")]
    [InlineData("/CMapName /Broken> beginbfchar")]
    [InlineData("1 begincodespacerange <00> <FF> endcodespacerange /CMapName /Broken> def")]
    public void RecoveryDoesNotDiscardMalformedMappingOrUnrelatedNames(string source)
    {
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream(source)));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        Assert.ThrowsAny<FormatException>(() => PdfFontResourceReader.Read(document, dictionary));
    }

    [Theory]
    [InlineData("Family-Fontfabric - Panton.otf,000-UTF16")]
    [InlineData("Family .5bad")]
    public void RecoveryDiscardsOnlySingleLineCMapNameFragments(string fragments)
    {
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("/CMapName /Broken " + fragments + " def\n"
                + "1 begincodespacerange <00> <FF> endcodespacerange "
                + "1 beginbfchar <41> <03A9> endbfchar")));
        Assert.ThrowsAny<FormatException>(() => Read(dictionary));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);
        Assert.Equal("\u03A9", Assert.Single(font.Decode("A"u8.ToArray())).Text);
    }

    [Fact]
    public void RecoveryKeepsTheCMapNameFragmentScanBounded()
    {
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", Stream("/CMapName /Broken " + new string('A', 4096) + " - def")));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        Assert.ThrowsAny<FormatException>(() => PdfFontResourceReader.Read(document, dictionary));
    }

    [Fact]
    public void RecoveryDecodesUnfilteredZlibUnicodeMapWithoutChangingStrictRead()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "1 begincodespacerange <00> <FF> endcodespacerange "
            + "1 beginbfchar <41> <03A9> endbfchar");
        using var output = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(output,
            System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(source);
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", new PdfStream(D(), output.ToArray())));
        Assert.Equal("A", Assert.Single(Read(dictionary).Decode("A"u8.ToArray())).Text);

        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);
        Assert.Equal("\u03A9", Assert.Single(font.Decode("A"u8.ToArray())).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryRejectsCorruptOrOversizedUnfilteredZlibUnicodeMap(bool oversized)
    {
        using var output = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(output,
            System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(new byte[oversized ? 32 * 1024 * 1024 + 1 : 64]);
        byte[] encoded = output.ToArray();
        if (!oversized) encoded[^1] ^= 0xFF;
        var dictionary = D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("ToUnicode", new PdfStream(D(), encoded)));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(
            () => PdfFontResourceReader.Read(document, dictionary));
    }

    [Fact]
    public void MissingUnicodeCmapStillAllowsEmbeddedMetricExtraction()
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        int cmap = FindTable(bytes, "cmap");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(cmap + 4), 2);
        Assert.Throws<NotSupportedException>(() => TrueTypeFont.Load(bytes));
        var font = Read(D(("Subtype", N("TrueType")), ("BaseFont", N("KillerTest")),
            ("FontDescriptor", D(("FontFile2", new PdfStream(D(), bytes)))),
            ("ToUnicode", Stream("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <0041> endbfchar"))));
        Assert.Equal("A", Assert.Single(font.Decode("A"u8.ToArray())).Text);
        Assert.Equal(800, font.Ascent);
        Assert.Null(font.GetGlyphBounds(65));
    }

    [Fact]
    public void ExplicitWidthsAndDescriptorMetricsOverrideStandardMetrics()
    {
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("Helvetica")), ("FirstChar", new PdfInteger(65)),
            ("Widths", new PdfArray([new PdfInteger(900)])),
            ("FontDescriptor", D(("Ascent", new PdfInteger(850)), ("Descent", new PdfInteger(-150)), ("MissingWidth", new PdfInteger(250))))));
        Assert.Equal(900, font.GetWidth(65));
        Assert.Equal(250, font.GetWidth(0));
        Assert.Equal(850, font.Ascent);
        Assert.Equal(-150, font.Descent);
    }

    [Fact]
    public void IdentityCidWidthsSupportArrayRangeAndDefault()
    {
        var descendant = D(("Subtype", N("CIDFontType2")), ("DW", new PdfInteger(777)),
            ("W", new PdfArray([new PdfInteger(1), new PdfArray([new PdfInteger(600), new PdfInteger(610)]),
                new PdfInteger(10), new PdfInteger(12), new PdfInteger(900)])));
        var font = Read(Type0(descendant, N("Identity-H"),
            Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar <0001> <0041> endbfchar")));
        Assert.Equal("A", Assert.Single(font.Decode(new byte[] { 0, 1 })).Text);
        Assert.Equal(600, font.GetWidth(1));
        Assert.Equal(610, font.GetWidth(2));
        Assert.Equal(900, font.GetWidth(11));
        Assert.Equal(777, font.GetWidth(20));
        Assert.Equal("\uFFFD", Assert.Single(font.Decode(new byte[] { 0, 20 })).Text);
    }

    [Fact]
    public void CustomCidMappingUsesSourceForUnicodeAndCidForWidth()
    {
        var descendant = D(("Subtype", N("CIDFontType2")), ("W", new PdfArray([
            new PdfInteger(5), new PdfInteger(6), new PdfInteger(600)])));
        var font = Read(Type0(descendant,
            Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 begincidrange <0041> <0042> 5 endcidrange"),
            Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0041> <0042> <0041> endbfrange")));
        Assert.Equal(["A", "B"], font.Decode(new byte[] { 0, 65, 0, 66 }).Select(c => c.Text));
        Assert.Equal(600, font.GetWidth(65));
        Assert.Equal(600, font.GetWidth(66));
    }

    [Fact]
    public void EmbeddedFontSuppliesMissingSimpleWidthsAndCidUnicodeFallback()
    {
        var descriptor = D(("FontFile2", new PdfStream(D(), TrueTypeFontTests.BuildTestFont(false))));
        var simple = Read(D(("Subtype", N("TrueType")), ("BaseFont", N("KillerTest")), ("FontDescriptor", descriptor)));
        Assert.Equal(600, simple.GetWidth(65));
        var cid = Read(Type0(D(("Subtype", N("CIDFontType2")), ("FontDescriptor", descriptor)), N("Identity-H")));
        Assert.Equal("A", Assert.Single(cid.Decode(new byte[] { 0, 1 })).Text);
    }

    [Fact]
    public void EmbeddedTrueTypeGlyphBoundsUseLocaAndGlyf()
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        int glyphOffset = FindTable(bytes, "glyf");
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(glyphOffset + 2), 25);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(glyphOffset + 4), -50);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(glyphOffset + 6), 575);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(glyphOffset + 8), 700);
        var font = Read(D(("Subtype", N("TrueType")), ("BaseFont", N("KillerTest")),
            ("FontDescriptor", D(("FontFile2", new PdfStream(D(), bytes))))));
        Assert.Equal(new PdfGlyphBounds(25, -50, 575, 700), font.GetGlyphBounds(65));
    }

    [Theory]
    [InlineData(")", true, true)]
    [InlineData("\u0080)", true, true)]
    [InlineData(")", false, false)]
    [InlineData("1 beginbfchar <0001> ) endbfchar", true, false)]
    [InlineData("1 begincodespacerange <0000> ) endcodespacerange", true, false)]
    public void UnreadableUnicodeMapRecoveryRequiresEmbeddedIdentityFont(
        string map, bool embedded, bool recoverable)
    {
        var descriptor = embedded
            ? D(("FontFile2", new PdfStream(D(), TrueTypeFontTests.BuildTestFont(false, includeOutlines: true))))
            : D();
        var baseFont = Type0(D(("Subtype", N("CIDFontType2")), ("FontDescriptor", descriptor)), N("Identity-H"));
        var dictionary = new PdfDictionary(baseFont.Append(new KeyValuePair<PdfName, PdfObject>(N("ToUnicode"), Stream(map))));
        Assert.ThrowsAny<FormatException>(() => Read(dictionary));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            new PdfDocumentBuilder().AddBlankPage().Build());
        if (!recoverable)
        {
            Assert.ThrowsAny<FormatException>(() => PdfFontResourceReader.Read(document, dictionary));
            return;
        }
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);
        Assert.Equal("A", Assert.Single(font.Decode(new byte[] { 0, 1 })).Text);
        Assert.NotNull(font.GetGlyphOutline(1));
        Assert.Equal("\uFFFD", Assert.Single(font.Decode(new byte[] { 0, 0 })).Text);
        Assert.Equal("An unreadable ToUnicode map was ignored; embedded font mappings were used.",
            Assert.Single(font.Diagnostics));
    }

    [Fact]
    public void EmbeddedCffGlyphOutlineUsesSimpleFontEncoding()
    {
        byte[] program = [.. PdfCffGlyphReaderTests.Numbers(10, 20), 21,
            .. PdfCffGlyphReaderTests.Numbers(100, 0, 0, 200, -100, 0), 5, 14];
        byte[] bytes = PdfCffGlyphReaderTests.Build(program);
        var fontFile = new PdfStream(D(("Subtype", N("Type1C"))), bytes);
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("KillerTest")),
            ("FontDescriptor", D(("FontFile3", fontFile)))));

        PdfGlyphContour contour = Assert.Single(Assert.IsType<PdfGlyphOutline>(
            font.GetGlyphOutline(65)).Contours);
        Assert.Equal(new PdfGlyphPoint(10, 20, true), contour.Points[0]);
        Assert.Equal(new PdfGlyphPoint(10, 220, true), contour.Points[^1]);
    }

    [Fact]
    public void EmbeddedCffComposesOmittedCompatibilityLigatures()
    {
        byte[] letterF = [.. PdfCffGlyphReaderTests.Numbers(0, 0), 21,
            .. PdfCffGlyphReaderTests.Numbers(200, 0, 0, 500), 5, 14];
        byte[] letterI = [.. PdfCffGlyphReaderTests.Numbers(0, 0), 21,
            .. PdfCffGlyphReaderTests.Numbers(100, 0, 0, 500), 5, 14];
        var fontFile = new PdfStream(D(("Subtype", N("Type1C"))),
            PdfCffGlyphReaderTests.BuildNamed(("f", letterF), ("i", letterI)));
        var encoding = D(("Differences", new PdfArray([new PdfInteger(11), N("ff")])));
        var descriptor = D(("FontFile3", fontFile), ("MissingWidth", new PdfInteger(300)));
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("KillerTest")),
            ("FirstChar", new PdfInteger(11)), ("LastChar", new PdfInteger(11)),
            ("Widths", new PdfArray([new PdfInteger(570)])), ("Encoding", encoding),
            ("FontDescriptor", descriptor)));

        PdfGlyphOutline outline = Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(11));
        Assert.Equal(2, outline.Contours.Count);
        Assert.Equal(new PdfGlyphPoint(0, 0, true), outline.Contours[0].Points[0]);
        Assert.Equal(new PdfGlyphPoint(285, 0, true), outline.Contours[1].Points[0]);
    }

    [Fact]
    public void EmbeddedCffComposesLigaturesWhenSubsetOmitsComponentOutlines()
    {
        byte[] unrelatedGlyph = [.. PdfCffGlyphReaderTests.Numbers(0, 0), 21,
            .. PdfCffGlyphReaderTests.Numbers(100, 0, 0, 500), 5, 14];
        var fontFile = new PdfStream(D(("Subtype", N("Type1C"))),
            PdfCffGlyphReaderTests.BuildNamed(("A", unrelatedGlyph)));
        var encoding = D(("Differences", new PdfArray([new PdfInteger(11), N("ff")])),
            ("BaseEncoding", N("StandardEncoding")));
        var descriptor = D(("FontFile3", fontFile), ("MissingWidth", new PdfInteger(0)));
        var font = Read(D(("Subtype", N("Type1")), ("BaseFont", N("KillerTest")),
            ("FirstChar", new PdfInteger(11)), ("LastChar", new PdfInteger(11)),
            ("Widths", new PdfArray([new PdfInteger(570)])), ("Encoding", encoding),
            ("FontDescriptor", descriptor)));

        PdfGlyphOutline component = Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(102));
        PdfGlyphOutline ligature = Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(11));
        Assert.Equal(component.Contours.Count * 2, ligature.Contours.Count);
    }

    [Fact]
    public void EmbeddedNameKeyedCffCompositeUsesItsUnicodeMap()
    {
        byte[] program = [.. PdfCffGlyphReaderTests.Numbers(10, 20), 21,
            .. PdfCffGlyphReaderTests.Numbers(100, 0, 0, 200, -100, 0), 5, 14];
        var fontFile = new PdfStream(D(("Subtype", N("Type1C"))),
            PdfCffGlyphReaderTests.Build(program));
        var descendant = D(("Subtype", N("CIDFontType0")),
            ("FontDescriptor", D(("FontFile3", fontFile))));
        PdfStream unicode = Stream(
            "1 begincodespacerange <00> <FF> endcodespacerange "
            + "1 beginbfchar <41> <0041> endbfchar");
        PdfExtractionFont font = Read(Type0(descendant, N("Identity-H"), unicode));

        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(font.GetGlyphOutline(65)).Contours);
    }

    [Fact]
    public void VerticalCidMetricsUseW2AndDw2()
    {
        var descendant = D(("DW", new PdfInteger(600)), ("DW2", new PdfArray([new PdfInteger(900), new PdfInteger(-1100)])),
            ("W2", new PdfArray([new PdfInteger(1), new PdfArray([new PdfInteger(-800), new PdfInteger(200), new PdfInteger(750)]),
                new PdfInteger(5), new PdfInteger(6), new PdfInteger(-950), new PdfInteger(300), new PdfInteger(850)])));
        var font = Read(Type0(descendant, N("Identity-V")));
        Assert.True(font.IsVertical);
        Assert.Equal(new PdfVerticalGlyphMetrics(-800, 200, 750), font.GetVerticalMetrics(1));
        Assert.Equal(new PdfVerticalGlyphMetrics(-950, 300, 850), font.GetVerticalMetrics(6));
        Assert.Equal(new PdfVerticalGlyphMetrics(-1100, 300, 900), font.GetVerticalMetrics(20));
    }

    [Fact]
    public void EncodingCmapPreservesMixedLengthCodeBoundariesWithoutToUnicode()
    {
        var descriptor = D(("FontFile2", new PdfStream(D(), TrueTypeFontTests.BuildTestFont(false))));
        var font = Read(Type0(D(("FontDescriptor", descriptor)),
            Stream("2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange 2 begincidchar <41> 1 <8001> 1 endcidchar")));
        Assert.Equal([1, 2], font.Decode(new byte[] { 65, 128, 1 }).Select(c => c.ByteLength));
        Assert.Equal(["A", "A"], font.Decode(new byte[] { 65, 128, 1 }).Select(c => c.Text));
    }

    [Fact]
    public void EncodingCmapStreamInheritsAndOverridesBaseMappings()
    {
        PdfStream inherited = Stream(
            "1 begincodespacerange <00> <FF> endcodespacerange "
            + "2 begincidchar <41> 1 <42> 2 endcidchar");
        var encoding = new PdfStream(D(("UseCMap", inherited)), Encoding.ASCII.GetBytes(
            "/Base usecmap 2 begincidchar <42> 3 <43> 4 endcidchar"));
        var descendant = D(("DW", new PdfInteger(500)),
            ("W", new PdfArray([new PdfInteger(1), new PdfArray([
                new PdfInteger(510), new PdfInteger(520), new PdfInteger(530),
                new PdfInteger(540)])])));

        PdfExtractionFont font = Read(Type0(descendant, encoding));

        Assert.Equal(510, font.GetWidth(0x41));
        Assert.Equal(530, font.GetWidth(0x42));
        Assert.Equal(540, font.GetWidth(0x43));
    }

    [Fact]
    public void EncodingCmapStreamInheritsAndOverridesAdobeMappings()
    {
        var encoding = new PdfStream(D(("UseCMap", N("90ms-RKSJ-H"))), Encoding.ASCII.GetBytes(
            "/90ms-RKSJ-H usecmap 1 begincidchar <41> 2 endcidchar"));
        var descendant = D(("CIDSystemInfo", D(("Registry", Text("Adobe")), ("Ordering", Text("Japan1")))),
            ("DW", new PdfInteger(500)), ("W", new PdfArray([new PdfInteger(1), new PdfArray([
                new PdfInteger(510), new PdfInteger(520)])])));

        PdfExtractionFont font = Read(Type0(descendant, encoding));

        Assert.Equal("!\u65E5", string.Concat(font.Decode(Convert.FromHexString("4193FA"))
            .Select(character => character.Text)));
        Assert.Equal([1, 2], font.Decode(Convert.FromHexString("4193FA"))
            .Select(character => character.ByteLength));
        Assert.Equal(520, font.GetWidth(0x41));
    }

    [Fact]
    public void EncodingCmapStreamInheritsAdobeMapFromContent()
    {
        var descendant = D(("CIDSystemInfo", D(("Registry", Text("Adobe")), ("Ordering", Text("Japan1")))));
        PdfExtractionFont font = Read(Type0(descendant,
            Stream("/90ms-RKSJ-H usecmap")));

        Assert.Equal("A\u65E5", string.Concat(font.Decode(Convert.FromHexString("4193FA"))
            .Select(character => character.Text)));
    }

    [Fact]
    public void EncodingCmapStreamRejectsUnknownNamedContentInheritance()
    {
        Assert.Throws<NotSupportedException>(() => Read(Type0(D(),
            Stream("/Unknown-H usecmap"))));
    }

    [Fact]
    public void PredefinedJapaneseCmapDecodesMixedSingleAndDoubleByteText()
    {
        var descendant = D(("CIDSystemInfo", D(("Registry", Text("Adobe")), ("Ordering", Text("Japan1")))));
        var font = Read(Type0(descendant, N("90ms-RKSJ-H")));
        Assert.Equal("A\u65E5\u672C", string.Concat(font.Decode(Convert.FromHexString("4193FA967B")).Select(c => c.Text)));
        Assert.Equal([1, 2, 2], font.Decode(Convert.FromHexString("4193FA967B")).Select(c => c.ByteLength));
    }

    [Theory]
    [InlineData("GBK-EUC-H", "GB1", "D6D0B9FA", "\u4E2D\u56FD")]
    [InlineData("B5pc-H", "CNS1", "A4A4A4E5", "\u4E2D\u6587")]
    [InlineData("KSCms-UHC-H", "Korea1", "C7D1B1B9", "\uD55C\uAD6D")]
    [InlineData("UniJIS-UTF16-H", "Japan1", "65E5672C", "\u65E5\u672C")]
    public void PredefinedCmapsUseCollectionUnicodeWithoutToUnicode(string encoding, string ordering, string hex, string expected)
    {
        var descendant = D(("CIDSystemInfo", D(("Registry", Text("Adobe")), ("Ordering", Text(ordering)))));
        var font = Read(Type0(descendant, N(encoding)));
        Assert.Equal(expected, string.Concat(font.Decode(Convert.FromHexString(hex)).Select(c => c.Text)));
    }

    [Fact]
    public void InvalidWidthsAreRejectedBeforeRangeExpansion()
    {
        var descendant = D(("W", new PdfArray([new PdfInteger(0), new PdfInteger(1000000000), new PdfInteger(600)])));
        Assert.Throws<FormatException>(() => Read(Type0(descendant, N("Identity-H"))));
    }

    [Fact]
    public void CustomEncodingCanUseAnExplicitUnicodeMap()
    {
        var font = Read(D(("Subtype", N("TrueType")), ("Encoding", N("Custom")),
            ("ToUnicode", Stream("1 beginbfchar <01> <0041> endbfchar"))));
        Assert.Equal("A", Assert.Single(font.Decode(new byte[] { 1 })).Text);
    }

    [Fact]
    public void Type3UnknownGlyphNamesPreserveTheirCharacterCodes()
    {
        var font = Read(D(("Subtype", N("Type3")), ("Encoding", D(("Differences",
            new PdfArray([new PdfInteger(98), N("Bullet"), N("Circle")]))))));
        Assert.Equal("bc", string.Concat(font.Decode("bc"u8.ToArray()).Select(c => c.Text)));
    }

    [Fact]
    public void EmptyDescriptorMetricsUseFontBoundingBox()
    {
        var font = Read(D(("Subtype", N("Type1")), ("FontDescriptor", D(
            ("Ascent", new PdfInteger(0)), ("Descent", new PdfInteger(0)),
            ("FontBBox", new PdfArray([new PdfInteger(-100), new PdfInteger(-257),
                new PdfInteger(1000), new PdfInteger(1125)]))))));
        Assert.Equal(1125, font.Ascent);
        Assert.Equal(-257, font.Descent);
    }

    private static int FindTable(byte[] bytes, string name)
    {
        for (int i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)); i++)
        {
            int offset = 12 + 16 * i;
            if (Encoding.ASCII.GetString(bytes, offset, 4) == name)
                return checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8)));
        }
        throw new InvalidOperationException("Missing test font table.");
    }
    private static PdfExtractionFont Read(PdfDictionary font) => PdfFontResourceReader.Read(Document, font);
    private static PdfDictionary Type0(PdfDictionary descendant, PdfObject encoding, PdfStream? unicode = null) => unicode is null
        ? D(("Subtype", N("Type0")), ("BaseFont", N("TestCID")), ("Encoding", encoding), ("DescendantFonts", new PdfArray([descendant])))
        : D(("Subtype", N("Type0")), ("BaseFont", N("TestCID")), ("Encoding", encoding), ("DescendantFonts", new PdfArray([descendant])), ("ToUnicode", unicode));
    private static PdfStream Stream(string value) => new(D(), Encoding.ASCII.GetBytes(value));
    private static PdfString Text(string value) => new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);
    private static PdfName N(string name) => new(Encoding.ASCII.GetBytes(name));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(v => new KeyValuePair<PdfName, PdfObject>(N(v.Name), v.Value)));

    private sealed class TestFontResolver(byte[] bytes) : IPdfFontResolver
    {
        internal List<PdfFontRequest> Requests { get; } = [];

        public byte[] Resolve(PdfFontRequest request)
        {
            Requests.Add(request);
            return bytes;
        }
    }
}
