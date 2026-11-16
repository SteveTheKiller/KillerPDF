using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>
/// Marks copied in KillerPDF, with the Windows clipboard's change count at the moment they were copied. They paste only
/// while that count is unchanged: anything copied afterward, in KillerPDF or another program, is what pastes, so one
/// copied picture no longer answers every paste until a restart (#497).
/// </summary>
internal sealed class MarkupClipboard
{
    private readonly List<PageAnnotation> _items = [];
    private uint _sequence;

    internal IReadOnlyList<PageAnnotation> Items => _items;

    internal void Set(IEnumerable<PageAnnotation> items, uint systemSequence)
    {
        _items.Clear();
        _items.AddRange(items);
        _sequence = systemSequence;
    }

    internal bool IsCurrent(uint systemSequence) => _items.Count > 0 && _sequence == systemSequence;

    /// <summary>The Windows clipboard's change count; it moves whenever any program puts something on the clipboard.</summary>
    internal static uint SystemSequence() => GetClipboardSequenceNumber();

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
