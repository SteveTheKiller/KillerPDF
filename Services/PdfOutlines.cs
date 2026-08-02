using PdfSharpCore.Pdf;

namespace KillerPDF.Services
{
    // ============================================================
    // Outline/bookmark document helpers - pure functions over the
    // PdfSharpCore outline tree, no window state. Split out of
    // SidebarOutline.cs (KillerUI refactor); the TreeView panel and
    // bookmark editing UI stay in the shell.
    // ============================================================
    internal static class PdfOutlines
    {
        /// <summary>
        /// #133: PdfSharpCore's lexer decodes UTF-16 bookmark titles by their BOM, but strings it
        /// decrypts AFTER parsing (owner-password protected files) never get that BOM re-check, so
        /// the title arrives as raw bytes widened to chars: a U+00FE U+00FF prefix (the BOM bytes)
        /// followed by one char per byte (mojibake).
        /// Detect the widened BOM, re-pack the chars into bytes, and decode as UTF-16. Titles that
        /// parsed correctly don't start with those two chars and pass through untouched.
        /// </summary>
        internal static string FixRawUnicodeTitle(string s)
        {
            if (s.Length < 2) return s;
            bool be = s[0] == '\u00FE' && s[1] == '\u00FF';   // UTF-16BE BOM as raw chars
            bool le = s[0] == '\u00FF' && s[1] == '\u00FE';   // UTF-16LE (Adobe tolerance)
            if (!be && !le) return s;
            foreach (char c in s)
                if (c > '\u00FF') return s;   // not byte-widened data - a real (odd) title, leave it
            var sb = new System.Text.StringBuilder((s.Length - 2) / 2);
            for (int i = 2; i + 1 < s.Length; i += 2)   // a trailing odd byte is dropped rather than corrupting the pairs
                sb.Append(be ? (char)((s[i] << 8) | s[i + 1])
                             : (char)((s[i + 1] << 8) | s[i]));
            return sb.ToString();
        }

        internal static int CountOutlines(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            int n = 0;
            foreach (PdfSharpCore.Pdf.PdfOutline o in col) n += 1 + CountOutlines(o.Outlines);
            return n;
        }

        // Bottom-up: Collection.Remove() drops the removed object from the document's reference
        // table, so deleting the whole branch leaf-first leaves no orphaned outline objects (with
        // dangling /Parent refs) behind in the saved file.
        internal static void RemoveOutlineRecursive(PdfSharpCore.Pdf.PdfOutlineCollection parent,
                                                    PdfSharpCore.Pdf.PdfOutline outline)
        {
            while (outline.Outlines.Count > 0)
                RemoveOutlineRecursive(outline.Outlines, outline.Outlines[outline.Outlines.Count - 1]);
            parent.Remove(outline);
        }

        // PdfSharpCore's PrepareForSave rewrites outline linkage keys (/First /Last /Next /Prev
        // /Parent /Count) from the in-memory collections but never REMOVES entries that no longer
        // apply: an item that became last keeps its old /Next, a parent whose children were all
        // deleted keeps /First /Last, and an emptied root would dangle (ScrubEmptyOutlines only
        // drops the catalog entry when /First is gone). After any bookmark edit, strip the linkage
        // keys everywhere - the writer rebuilds all of them from the collections on save.
        internal static void ScrubStaleOutlineLinkKeys(PdfDocument? doc)
        {
            if (doc is null) return;
            try
            {
                var item = doc.Internals.Catalog.Elements["/Outlines"];
                if (item is null) return;
                if (PdfScrub.DerefItemStatic(item) is PdfDictionary root)
                {
                    root.Elements.Remove("/First");
                    root.Elements.Remove("/Last");
                    root.Elements.Remove("/Count");
                }
                ScrubOutlineLinkKeys(doc.Outlines);
            }
            catch { /* malformed outline tree - the save-time scrubs are the backstop */ }
        }

        private static void ScrubOutlineLinkKeys(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline o in col)
            {
                o.Elements.Remove("/First");
                o.Elements.Remove("/Last");
                o.Elements.Remove("/Next");
                o.Elements.Remove("/Prev");
                o.Elements.Remove("/Parent");
                o.Elements.Remove("/Count");
                ScrubOutlineLinkKeys(o.Outlines);
            }
        }
    }
}
