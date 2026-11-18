using System.Windows.Media;

namespace KillerPDF.Controls;

internal sealed record RedactionMark(int PageIndex, double Left, double Bottom, double Right, double Top,
    string? OverlayText = null, Color? Fill = null, long AnnotationFlags = 4)
{
    internal bool IsVisibleOnScreen => (AnnotationFlags & 34) == 0;
}

internal sealed class RedactionMarkSnapshot(IEnumerable<RedactionMark> marks, string? sourceFingerprint = null,
    IEnumerable<string>? foundText = null)
{
    // Records contain only immutable values; the collection itself must also be copied and sealed.
    internal IReadOnlyList<RedactionMark> Marks { get; } = Array.AsReadOnly(marks.ToArray());
    internal string? SourceFingerprint { get; } = sourceFingerprint;
    // The words searches marked on this document, so they stay with it and never reach another one.
    internal IReadOnlyList<string> FoundText { get; } = Array.AsReadOnly((foundText ?? []).ToArray());
}
