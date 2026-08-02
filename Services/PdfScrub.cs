using PdfSharpCore.Pdf;

namespace KillerPDF.Services
{
    // ============================================================
    // Pre-save document scrubs - pure functions over a PdfDocument,
    // no window state. Split out of FileOperations.cs and Links.cs
    // (KillerUI refactor); shared by the GUI save paths, TempReload,
    // the CLI runner and the batch runner.
    // ============================================================
    internal static class PdfScrub
    {
        /// <summary>
        /// Dereferences a PdfItem if it is an indirect reference (PdfReference is internal to
        /// PdfSharpCore; we detect it by looking for a public "Value" property returning
        /// PdfObject). Null-tolerant: absent dictionary keys arrive here as null and mean
        /// "not there".
        /// </summary>
        internal static PdfItem? DerefItemStatic(PdfItem? item)
        {
            // Absent dictionary keys arrive here as null (Elements["/X"] on a fresh document is
            // null for /AcroForm, /Kids, ...). The scrubs' pattern matches treat null as "not
            // there", which is correct - dereferencing it here just tripped an NRE first.
            if (item is null) return null;
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        internal static double RectNum(PdfItem item) =>
            item is PdfReal r ? r.Value : item is PdfInteger n ? n.Value : 0;

        /// <summary>
        /// Returns the PDF object number of a PdfItem that is an indirect reference, or -1.
        /// Handles the internal PdfReference type via reflection, like DerefItemStatic above.
        /// </summary>
        internal static int GetObjectNumber(PdfItem? item)
        {
            if (item is null) return -1;
            var prop = item.GetType().GetProperty("ObjectNumber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(item) is int n2 ? n2 : -1;
        }

        // #103: PdfSharpCore's writer can emit the catalog's /Outlines reference without ever
        // writing the (empty, lazily created) outlines object itself - a dangling xref entry
        // that strict parsers, including PdfSharpCore on reopen, refuse. An outlines dictionary
        // with no /First contains no bookmarks, so dropping the entry is a semantic no-op that
        // keeps the file consistent. Real bookmark trees (/First present) are left untouched.
        // Called before every save of the working document.
        internal static void ScrubEmptyOutlines(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                var item = cat.Elements["/Outlines"];
                if (item == null) return;
                var resolved = DerefItemStatic(item);
                if (resolved is not PdfDictionary o || o.Elements["/First"] == null)
                    cat.Elements.Remove("/Outlines");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        // PdfSharpCore's PdfPage.MediaBox/CropBox property GETTERS have create-on-read semantics:
        // touching page.CropBox on a page that has none plants an empty /CropBox [0 0 0 0] into
        // the page dictionary (the same lazy-getter trap as the phantom /Outlines above). A
        // zero-size page box saves to disk and Adobe then rejects the page as "dimensions
        // out-of-range" even though the MediaBox is fine (Chrome falls back to the MediaBox,
        // which is why such files still open there). Dropping a degenerate CropBox is a semantic
        // no-op - the page falls back to its MediaBox - and it also HEALS files written by
        // affected versions (1.6.x up to 1.6.2) when they are re-saved. Real crops are untouched.
        // Called before every save of the working document.
        internal static void ScrubDegenerateCropBoxes(PdfDocument doc)
        {
            try
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var elements = doc.Pages[i].Elements;
                    var item = elements["/CropBox"];
                    if (item is null) continue;
                    var resolved = DerefItemStatic(item);

                    // The box can be a parsed PdfArray (loaded from disk) or a PdfRectangle
                    // (planted in memory by the lazy getter) - handle both, like ScaleRectValue.
                    double w = -1, h = -1;
                    if (resolved is PdfRectangle rect)
                    {
                        w = Math.Abs(rect.X2 - rect.X1);
                        h = Math.Abs(rect.Y2 - rect.Y1);
                    }
                    else if (resolved is PdfArray arr && arr.Elements.Count == 4 &&
                             arr.Elements[0] is PdfReal or PdfInteger && arr.Elements[1] is PdfReal or PdfInteger &&
                             arr.Elements[2] is PdfReal or PdfInteger && arr.Elements[3] is PdfReal or PdfInteger)
                    {
                        w = Math.Abs(RectNum(arr.Elements[2]) - RectNum(arr.Elements[0]));
                        h = Math.Abs(RectNum(arr.Elements[3]) - RectNum(arr.Elements[1]));
                    }

                    // Remove only when we could read the box AND it is degenerate; anything we
                    // cannot interpret is left alone rather than destroyed.
                    if (w >= 0 && (w < 1 || h < 1))
                        elements.Remove("/CropBox");
                }
            }
            catch { /* malformed page tree - leave the save as-is */ }
        }

        // A KillerPDF save fully REWRITES the file, which mathematically invalidates any existing
        // digital signature: its /ByteRange and digest describe the old bytes (ISO 19005-2, 6.4.3
        // requires the digest to cover the entire file). Carrying the dead signature forward
        // misleads viewers and fails PDF/A validation, so strip signature VALUES (/V) from
        // signature fields and the catalog's /Perms certification (DocMDP / usage rights) that
        // references them. The empty fields stay and can be re-signed via Sign Document.
        // Called before every save of the working document.
        internal static void ScrubDeadSignatures(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                cat.Elements.Remove("/Perms");
                if (DerefItemStatic(cat.Elements["/AcroForm"]) is not PdfDictionary acro) return;
                if (DerefItemStatic(acro.Elements["/Fields"]) is PdfArray fields)
                    ScrubSigFieldValues(fields, 0);
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        private static void ScrubSigFieldValues(PdfArray fields, int depth)
        {
            if (depth > 8) return;   // defensive: malformed circular /Kids
            foreach (var item in fields.Elements)
            {
                if (DerefItemStatic(item) is not PdfDictionary field) continue;
                if (field.Elements.GetName("/FT") == "/Sig" && field.Elements["/V"] != null)
                    field.Elements.Remove("/V");
                if (DerefItemStatic(field.Elements["/Kids"]) is PdfArray kids)
                    ScrubSigFieldValues(kids, depth + 1);
            }
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        internal static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItemStatic(elem) as PdfDictionary;
                    if (ann is null) continue;

                    // Dereference subtype in case it is an indirect name.
                    var subtypeItem = ann.Elements["/Subtype"];
                    var subtype = (subtypeItem as PdfDictionary ?? DerefItemStatic(subtypeItem) as PdfDictionary) is null
                        ? subtypeItem?.ToString() ?? ""
                        : "";
                    if (!subtype.Contains("Link")) continue;

                    // Remove appearance stream and color.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");

                    // /BS (border style dict) takes precedence over /Border in PDF spec;
                    // set W=0 explicitly.  Also set /Border [0 0 0] for older viewers.
                    var bs = new PdfDictionary();
                    bs.Elements["/W"] = new PdfInteger(0);
                    ann.Elements["/BS"] = bs;

                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }
    }
}
