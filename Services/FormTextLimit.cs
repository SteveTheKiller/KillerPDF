using System.Linq;

namespace KillerPDF.Services
{
    /// <summary>
    /// The /MaxLen rule a text field is saved under. The engine counts Unicode characters (runes),
    /// not UTF-16 units, so an emoji is one character; the live editor applies the same count so a
    /// value it accepts is one Save and Print accept too (BF-U007).
    /// </summary>
    internal static class FormTextLimit
    {
        internal static int Count(string text) => text.EnumerateRunes().Count();

        /// <summary>
        /// True when <paramref name="next"/> may replace <paramref name="previous"/>. A value that
        /// was already too long when the file opened is never cut; it may only get shorter.
        /// </summary>
        internal static bool Accepts(string next, string previous, int maximum) =>
            maximum <= 0 || Count(next) <= maximum || Count(next) < Count(previous);

        internal static bool IsOver(string text, int maximum) => maximum > 0 && Count(text) > maximum;

        /// <summary>The smallest length limit of each field name, across every page. A value typed
        /// into one field of a repeated name is sent to them all, so the tightest limit decides.</summary>
        internal static IReadOnlyDictionary<string, int> SmallestByName(KillerPdf.Engine.Documents.PdfDocument document)
        {
            var reader = new KillerPdf.Engine.Documents.PdfFormWidgetReader(document);
            int pages = KillerPdf.Engine.Documents.PdfPageInformation.Read(document).Count;
            var smallest = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int page = 0; page < pages; page++)
                foreach (KillerPdf.Engine.Documents.PdfFormWidgetInfo widget in reader.Read(page))
                    if (widget.MaximumLength > 0 && (!smallest.TryGetValue(widget.FieldName, out int limit) || widget.MaximumLength < limit))
                        smallest[widget.FieldName] = widget.MaximumLength;
            return smallest;
        }
    }
}
