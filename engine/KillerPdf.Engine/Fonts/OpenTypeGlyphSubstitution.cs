using System.Buffers.Binary;

namespace KillerPdf.Engine.Fonts;

// Selected Latin default-language substitutions retain their font lookup order. A lookup this reader cannot apply is
// skipped, so its text is shown without that substitution instead of the chapter being refused.
internal sealed class OpenTypeGlyphSubstitution
{
    internal sealed record SubstitutionRule(ushort[] Outputs, ushort[] Components);
    internal IReadOnlyList<IReadOnlyList<SubstitutionRule>> Lookups { get; }
    internal bool HasSmallCaps { get; private set; }
    private ReadOnlyMemory<byte> _data;
    private int _work;
    private readonly CancellationToken _cancellation;

    internal OpenTypeGlyphSubstitution(TrueTypeFont font, CancellationToken cancellation, bool smallCaps = false,
        bool ligatures = true)
    {
        _cancellation = cancellation;
        var selected = new List<IReadOnlyList<SubstitutionRule>>();
        Lookups = selected.AsReadOnly();
        // A table too large to read within the work limit keeps the lookups read before the limit.
        try { Select(font, selected, smallCaps, ligatures); }
        catch (WorkLimitExceededException) { }
    }

    private void Select(TrueTypeFont font, List<IReadOnlyList<SubstitutionRule>> selected, bool smallCaps, bool ligatures)
    {
        if (!font.TryGetTableRange("GSUB", out int offset, out int length)) return;
        _data = font.FontData.Slice(offset, length);
        if (U16(0) != 1 || U16(2) != 0)
            throw new NotSupportedException("The EPUB GSUB version is not implemented.");
        int scripts = Relative(0, 4), features = Relative(0, 6), lookups = Relative(0, 8);
        int script = 0;
        for (int i = 0, count = U16(scripts); i < count; i++)
        {
            int record = scripts + 2 + 6 * i;
            if (Tag(record) == 0x6C61746E) script = Relative(scripts, record + 4);
        }
        if (script == 0 || U16(script) == 0) return;
        int language = Relative(script, script);
        if (U16(language) != 0)
            throw new NotSupportedException("The EPUB GSUB language lookup order is not implemented.");
        int featureCount = U16(features), lookupCount = U16(lookups);
        var indices = new List<int>();
        int required = U16(language + 2);
        if (required != 0xFFFF) indices.Add(required);
        for (int i = 0, count = U16(language + 4); i < count; i++) indices.Add(U16(language + 6 + i * 2));
        var selectedIndices = new SortedSet<int>();
        foreach (int index in indices)
        {
            if (index >= featureCount) throw Invalid();
            int record = features + 2 + index * 6;
            uint featureTag = Tag(record);
            if (!(ligatures && featureTag == 0x6C696761) && !(smallCaps && featureTag == 0x736D6370)) continue;
            if (featureTag == 0x736D6370) HasSmallCaps = true;
            int feature = Relative(features, record + 4);
            for (int i = 0, count = U16(feature + 2); i < count; i++)
            {
                int lookup = U16(feature + 4 + 2 * i);
                if (lookup >= lookupCount) throw Invalid();
                selectedIndices.Add(lookup);
            }
        }
        foreach (int index in selectedIndices)
        {
            int lookup = Relative(lookups, lookups + 2 + 2 * index);
            int lookupType = U16(lookup);
            if (lookupType is not (1 or 2 or 4 or 7) || U16(lookup + 2) != 0) continue;
            try { selected.Add(ReadLookup(font, lookup, lookupType)); }
            catch (UnsupportedLookupException) { }
        }
    }

    private IReadOnlyList<SubstitutionRule> ReadLookup(TrueTypeFont font, int lookup, int lookupType)
    {
        var rules = new List<SubstitutionRule>();
        int? extensionType = null;
        for (int sub = 0, count = U16(lookup + 4); sub < count; sub++)
        {
            int table = Relative(lookup, lookup + 6 + 2 * sub);
            int subtableType = lookupType;
            if (lookupType == 7)
            {
                subtableType = U16(table + 2);
                if (U16(table) != 1 || subtableType is not (1 or 2 or 4))
                    throw new UnsupportedLookupException();
                if (extensionType is int previous && previous != subtableType) throw Invalid();
                extensionType = subtableType;
                uint extension = Tag(table + 4);
                if (extension < 8 || extension > (uint)(_data.Length - table)) throw Invalid();
                table += (int)extension;
            }
            int format = U16(table);
            if (subtableType == 1 && format == 1)
            {
                int singleCoverage = Relative(table, table + 2);
                if (U16(singleCoverage) != 1)
                    throw new UnsupportedLookupException();
                int delta = U16(table + 4);
                for (int c = 0, glyphCount = U16(singleCoverage + 2); c < glyphCount; c++)
                {
                    ushort input = U16(singleCoverage + 4 + 2 * c);
                    ushort output = unchecked((ushort)(input + delta));
                    ValidateGlyph(input);
                    ValidateGlyph(output);
                    rules.Add(new([output], [input]));
                }
                continue;
            }
            if (subtableType == 1 ? format != 2 : format != 1)
                throw new UnsupportedLookupException();
            int coverage = Relative(table, table + 2);
            int sets = U16(table + 4);
            int coverageFormat = U16(coverage);
            var covered = new ushort[sets];
            if (coverageFormat == 1)
            {
                if (U16(coverage + 2) != sets) throw Invalid();
                for (int c = 0; c < sets; c++) covered[c] = U16(coverage + 4 + 2 * c);
            }
            else if (coverageFormat == 2)
            {
                int coveredCount = 0, previousEnd = -1;
                for (int r = 0, ranges = U16(coverage + 2); r < ranges; r++)
                {
                    int record = coverage + 4 + 6 * r;
                    int first = U16(record), last = U16(record + 2), startIndex = U16(record + 4);
                    if (first > last || first <= previousEnd || startIndex != coveredCount
                        || last - first + 1 > sets - coveredCount) throw Invalid();
                    for (int glyph = first; glyph <= last; glyph++)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        covered[coveredCount++] = (ushort)glyph;
                    }
                    previousEnd = last;
                }
                if (coveredCount != sets) throw Invalid();
            }
            else throw new UnsupportedLookupException();
            for (int setIndex = 0; setIndex < sets; setIndex++)
            {
                ushort first = covered[setIndex];
                ValidateGlyph(first);
                if (subtableType == 1)
                {
                    ushort output = U16(table + 6 + 2 * setIndex);
                    ValidateGlyph(output);
                    rules.Add(new([output], [first]));
                    continue;
                }
                if (subtableType == 2)
                {
                    int sequence = Relative(table, table + 6 + 2 * setIndex);
                    int glyphCount = U16(sequence);
                    if (glyphCount is < 1 or > 256) throw Invalid();
                    var outputs = new ushort[glyphCount];
                    for (int c = 0; c < glyphCount; c++)
                    {
                        outputs[c] = U16(sequence + 2 + 2 * c);
                        ValidateGlyph(outputs[c]);
                    }
                    rules.Add(new(outputs, [first]));
                    continue;
                }
                int set = Relative(table, table + 6 + 2 * setIndex);
                for (int n = 0, ligatureCount = U16(set); n < ligatureCount; n++)
                {
                    int at = Relative(set, set + 2 + 2 * n);
                    ushort output = U16(at); ValidateGlyph(output);
                    int components = U16(at + 2);
                    if (components is < 1 or > 256) throw Invalid();
                    var input = new ushort[components]; input[0] = first;
                    for (int c = 1; c < components; c++) { input[c] = U16(at + 2 + 2 * c); ValidateGlyph(input[c]); }
                    rules.Add(new([output], input));
                }
            }
        }
        return rules.AsReadOnly();

        void ValidateGlyph(ushort glyph)
        {
            if (glyph == 0 || glyph >= font.GlyphCount) throw Invalid();
        }
    }

    private ushort U16(int at)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (++_work > 100_000) throw new WorkLimitExceededException();
        if (at < 0 || at > _data.Length - 2) throw Invalid();
        return BinaryPrimitives.ReadUInt16BigEndian(_data.Span.Slice(at, 2));
    }
    private uint Tag(int at) => ((uint)U16(at) << 16) | U16(at + 2);
    private int Relative(int origin, int at)
    {
        int relative = U16(at);
        if (relative == 0 || origin > _data.Length - relative) throw Invalid();
        return origin + relative;
    }
    private sealed class UnsupportedLookupException : Exception;
    private sealed class WorkLimitExceededException : Exception;
    private static InvalidDataException Invalid() => new("The EPUB GSUB table is truncated or invalid.");
}
