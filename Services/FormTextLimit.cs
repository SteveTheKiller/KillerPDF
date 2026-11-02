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

    /// <summary>
    /// Each open document's smallest limits, read once on a worker and held with that document, not in
    /// a viewer's one slot, so moving between tabs or panes never reads a document again and building a
    /// field never waits on a walk of every page. The entry goes with the document. A page edit opens a
    /// new working document, whose fields may differ, so it is read afresh.
    /// </summary>
    internal sealed class FormTextLimitCache
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Task<IReadOnlyDictionary<string, int>>> _byDocument = new();

        /// <summary>The read of a document's limits, started on a worker the first time it is asked for.</summary>
        internal Task<IReadOnlyDictionary<string, int>> Read(object document, Func<IReadOnlyDictionary<string, int>> read)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(read);
            return _byDocument.GetValue(document, _ => Task.Run(read));
        }

        /// <summary>The limits once they are read, or null while the read runs, when it failed or when it
        /// was never started. Until then a field holds to its own limit.</summary>
        internal IReadOnlyDictionary<string, int>? Known(object document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return _byDocument.TryGetValue(document, out Task<IReadOnlyDictionary<string, int>>? read)
                && read.IsCompletedSuccessfully ? read.Result : null;
        }
    }
}
