using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// Real DjVu text layers (the archive.org books Pride and Prejudice and The Tale of Peter Rabbit) carry the structural
// separators of the DjVu text model as characters that no child zone covers: U+001F after a paragraph, U+001D after a
// region and one U+0000 after the whole page. Each belongs to the deepest zone whose range holds it, and the page's
// text is still written, with those characters placed inside that zone's rectangle.
public sealed class DjvuPdfSeparatorTextConversionTests
{
    // Page 2 of Pride and Prejudice has this shape (a full-page column holding VT, then a column, region, paragraph,
    // line and words holding the text, with US, GS, VT and NUL left over at the ends of the parents).
    // Offsets: 0 VT | 1-3 "ab " | 4-6 "cd " | 7 LF | 8 US | 9 GS | 10 VT | 11 NUL.
    [Fact]
    public void ConversionPlacesStructuralSeparatorsInTheZoneThatHoldsThemAndKeepsTheText()
    {
        const string text = "\v" + "ab cd \n" + "\u001f" + "\u001d" + "\v" + "\0";
        var native = DjvuDocument.Open(Page(text,
            new Node(1, 2, 4, 30, 10, 0, 12,
                new Node(2, 0, 0, 40, 20, 0, 1),
                new Node(2, 2, 4, 30, 10, 1, 10,
                    new Node(3, 2, 4, 30, 10, 1, 9,
                        new Node(4, 2, 4, 30, 10, 1, 8,
                            new Node(5, 2, 4, 30, 10, 1, 7,
                                new Node(6, 2, 4, 10, 10, 1, 3),
                                new Node(6, 20, 4, 10, 10, 4, 3))))))));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Empty(notices);
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(text, string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(["\v", "ab ", "cd \n", "\u001f", "\u001d\v", "\0"], letters.Select(letter => letter.Value));
        // Leaves keep their own rectangles (pixels times 72/144).
        AssertBox(letters[0], 0, 0, 20, 10);
        AssertBox(letters[1], 1, 2, 6, 7);
        AssertBox(letters[2], 10, 2, 15, 7);
        // Each separator follows the last child of the zone that holds it, which ends at x = 32 where the zone ends,
        // so the gap between the neighbouring children is empty and the character takes its proportional share of
        // that zone's width: US is the 8th of the paragraph's 8 characters (starting at x = 2 + 30 * 7 / 8),
        // GS the 9th of the region's 9 (2 + 30 * 8 / 9), NUL the 12th of the page's 12 (2 + 30 * 11 / 12).
        AssertBox(letters[3], (2 + 30 * 7 / 8d) / 2, 2, 16, 7);
        AssertBox(letters[4], (2 + 30 * 8 / 9d) / 2, 2, 16, 7);
        AssertBox(letters[5], (2 + 30 * 11 / 12d) / 2, 2, 16, 7);
        Assert.Equal(text, native.ReadText(0));
    }

    // A character between two children (a hyphen the OCR kept outside both words) sits between their rectangles.
    [Fact]
    public void ConversionPlacesACharacterBetweenTwoChildrenBetweenTheirRectangles()
    {
        var native = DjvuDocument.Open(Page("ab-cd",
            new Node(5, 2, 4, 30, 10, 0, 5,
                new Node(6, 2, 4, 10, 10, 0, 2),
                new Node(6, 20, 4, 10, 10, 3, 2))));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Empty(notices);
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(["ab", "-", "cd"], letters.Select(letter => letter.Value));
        AssertBox(letters[0], 1, 2, 6, 7);
        // Word one ends at x = 12 and word two starts at x = 20.
        AssertBox(letters[1], 6, 2, 10, 7);
        AssertBox(letters[2], 10, 2, 15, 7);
    }

    // A zone whose children carry no text has no neighbours: its characters share its rectangle in proportion.
    [Fact]
    public void ConversionSpreadsTheTextOfAZoneWithoutTextChildrenAcrossItsRectangle()
    {
        var native = DjvuDocument.Open(Page("xy",
            new Node(6, 2, 4, 10, 10, 0, 2, new Node(7, 2, 4, 5, 10, 0, 0))));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Empty(notices);
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("xy", letter.Value);
        AssertBox(letter, 1, 2, 6, 7);
    }

    // Text that no zone holds at all still cannot be placed, even beside zones that do hold text.
    [Fact]
    public void ConversionStillLeavesOutTextThatNoZoneHolds()
    {
        var native = DjvuDocument.Open(Page("ab-cd",
            new Node(5, 2, 4, 30, 10, 0, 2),
            new Node(5, 20, 4, 10, 10, 3, 2)));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("TextNotConverted", Assert.Single(notices).Code);
    }

    private static void AssertBox(PdfExtractedLetter letter, double left, double bottom, double right, double top)
    {
        Assert.Equal(left, letter.BoundingBox.Left, 6);
        Assert.Equal(bottom, letter.BoundingBox.Bottom, 6);
        Assert.Equal(right, letter.BoundingBox.Right, 6);
        Assert.Equal(top, letter.BoundingBox.Top, 6);
    }

    // A zone given in absolute pixels (left, bottom, width, height) and absolute text offsets (ASCII text, so bytes
    // are UTF16 units). Encoding turns it into the relative form the TXTa chunk stores: a first child is measured
    // from its parent, a later sibling from the previous sibling (page, paragraph and line siblings stack downward;
    // the others follow on the right).
    private sealed record Node(byte Type, int Left, int Bottom, int Width, int Height, int Start, int Length,
        params Node[] Children);

    private static IEnumerable<byte[]> Encode(Node node, Node? parent, Node? previous)
    {
        int left = node.Left, bottom = node.Bottom, start = node.Start;
        if (parent is not null && previous is null)
        {
            left = node.Left - parent.Left;
            bottom = parent.Bottom + parent.Height - node.Bottom - node.Height;
            start = node.Start - parent.Start;
        }
        else if (previous is not null)
        {
            start = node.Start - (previous.Start + previous.Length);
            if (node.Type is 1 or 4 or 5)
            {
                left = node.Left - previous.Left;
                bottom = previous.Bottom - node.Bottom - node.Height;
            }
            else
            {
                left = node.Left - (previous.Left + previous.Width);
                bottom = node.Bottom - previous.Bottom;
            }
        }
        yield return Zone(node.Type, left, bottom, node.Width, node.Height, start, node.Length, node.Children.Length);
        Node? before = null;
        foreach (var child in node.Children)
        {
            foreach (var record in Encode(child, node, before)) yield return record;
            before = child;
        }
    }

    // A blank 40 by 20 pixel page at 144 dpi (so 20 by 10 points): 20 all-white MMR rows (one V0 bit each) and the
    // end-of-facsimile-block code, then the text layer.
    private static byte[] Page(string text, params Node[] roots)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 40, 0, 20, 26, 0, 144, 0, 22, 1]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 40, 0, 20, 0xff, 0xff, 0xf0, 0x01, 0x00, 0x10]),
            .. Chunk("TXTa", [(byte)(utf8.Length >> 16), (byte)(utf8.Length >> 8), (byte)utf8.Length,
                .. utf8, 1, .. roots.SelectMany(root => Encode(root, null, null)).SelectMany(zone => zone)])])];
    }

    private static byte[] Zone(byte type, int left, int bottom, int width, int height, int start, int length, int children)
    {
        var result = new byte[17];
        result[0] = type;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + index * 2), checked((ushort)(values[index] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        result[14] = (byte)(children >> 16); result[15] = (byte)(children >> 8); result[16] = (byte)children;
        return result;
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
