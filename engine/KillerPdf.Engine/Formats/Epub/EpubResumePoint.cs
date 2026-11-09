using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>
/// Where a page's layout can resume without laying out the chapter from its start: the last block that began before the
/// page did, and the exact layout state when it began. Laying out from that block reproduces the page; only the page the
/// block began on is laid out on the way, and it is dropped because the content before the block is missing from it.
/// </summary>
/// <param name="Path">Child positions from the chapter's root block down to the block (empty for the root itself).</param>
/// <param name="Page">The chapter page the block began on.</param>
/// <param name="Top">The flow position when the block began.</param>
/// <param name="PendingGap">The collapsed margin waiting above the block.</param>
/// <param name="PageStart">The text offset where that page began.</param>
/// <param name="Flags">Bit 0: a page break after the previous block is pending; bit 1: the page already has content.</param>
/// <param name="Started">For each ancestor by depth, whether it has laid out a line of its own.</param>
/// <param name="Marked">For each ancestor list item by depth, whether its number has been drawn.</param>
/// <param name="Padding">Ancestors whose top padding or border is still waiting to move with their first line, by depth.</param>
internal sealed record EpubResumePoint(int[] Path, int Page, double Top, double PendingGap, int PageStart, byte Flags,
    ulong Started, ulong Marked, EpubResumePadding[]? Padding)
{
    internal bool PendingBreakAfter => (Flags & 1) != 0;
    internal bool PageHasContent => (Flags & 2) != 0;
}

/// <summary>An ancestor's top padding and border as the layout tracked them when the resumed block began.</summary>
internal readonly record struct EpubResumePadding(int Depth, double Height, int PaintStart, double BorderHeight, PdfRgbColor? BorderColor);
