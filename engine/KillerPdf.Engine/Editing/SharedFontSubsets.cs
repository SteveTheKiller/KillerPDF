using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Editing;

/// <summary>
/// Groups text-only content so each group is authored as one document and its fonts are subset once. Authoring a
/// document per item embeds a copy of every font in each one. A reader checks each annotation appearance and each
/// editable page layer, shared fonts included, against fixed graph limits, so a group holds at most
/// <see cref="GlyphBudget"/> glyphs of any one font. Content with images, patterns or nested forms that are not
/// text-only is authored on its own, so one save never holds every image at once.
/// </summary>
internal static class SharedFontSubsets
{
    internal const int GlyphBudget = 256;

    internal static List<List<T>> Group<T>(IEnumerable<T> items,
        Func<T, Dictionary<TrueTypeFont, HashSet<ushort>>?> glyphs)
    {
        var groups = new List<List<T>>();
        var group = new List<T>();
        var used = new Dictionary<TrueTypeFont, HashSet<ushort>>();
        foreach (T item in items)
        {
            if (glyphs(item) is not { Count: > 0 } own)
            {
                // Groups keep the items' order, so an open group closes first.
                if (group.Count > 0)
                {
                    groups.Add(group);
                    group = [];
                    used.Clear();
                }
                groups.Add([item]);
                continue;
            }
            bool fits = own.All(font => (used.TryGetValue(font.Key, out HashSet<ushort>? set) ? set : [])
                .Union(font.Value).Count() <= GlyphBudget);
            if (!fits && group.Count > 0)
            {
                groups.Add(group);
                group = [];
                used.Clear();
            }
            group.Add(item);
            foreach (var font in own)
            {
                if (!used.TryGetValue(font.Key, out HashSet<ushort>? set)) used[font.Key] = set = [];
                set.UnionWith(font.Value);
            }
        }
        if (group.Count > 0) groups.Add(group);
        return groups;
    }

    /// <summary>The glyphs each embedded font draws, or null when the content carries an image or pattern.</summary>
    internal static Dictionary<TrueTypeFont, HashSet<ushort>>? TextGlyphs(PdfContentStreamBuilder content)
    {
        if (content.ImageResources.Count > 0 || content.AssetResources.Count > 0 || content.PatternResources.Count > 0)
            return null;
        var fonts = new Dictionary<TrueTypeFont, HashSet<ushort>>();
        Add(fonts, content.EmbeddedFontResources);
        return content.FormResources.Keys.All(form => Collect(form, fonts, 0)) ? fonts : null;
    }

    /// <summary>The glyphs each embedded font draws in a form, or null when it carries an image or pattern.</summary>
    internal static Dictionary<TrueTypeFont, HashSet<ushort>>? TextGlyphs(PdfFormXObject form)
    {
        var fonts = new Dictionary<TrueTypeFont, HashSet<ushort>>();
        return Collect(form, fonts, 0) ? fonts : null;
    }

    private static bool Collect(PdfFormXObject form, Dictionary<TrueTypeFont, HashSet<ushort>> fonts, int depth)
    {
        if (depth > 16 || form.Images.Count > 0 || form.Assets.Count > 0 || form.Patterns.Count > 0) return false;
        Add(fonts, form.EmbeddedFonts);
        return form.Forms.Keys.All(nested => Collect(nested, fonts, depth + 1));
    }

    private static void Add(Dictionary<TrueTypeFont, HashSet<ushort>> fonts, IEnumerable<EmbeddedFontUsage> usages)
    {
        foreach (EmbeddedFontUsage usage in usages)
        {
            if (!fonts.TryGetValue(usage.Font, out HashSet<ushort>? used)) fonts[usage.Font] = used = [];
            used.UnionWith(usage.Mappings.Values.Select(mapping => mapping.Glyph));
        }
    }
}
