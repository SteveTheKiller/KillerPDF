using Docnet.Core.Models;

namespace KillerPDF.Services
{
    // ============================================================
    // Shared page-rasterization flags (#141).
    //
    // PDFium does not draw a file's own annotations - sticky notes, highlights,
    // stamps, ink made in another app - unless it is asked to. Docnet's
    // parameterless GetImage() passes flags 0, so KillerPDF rendered pages
    // WITHOUT them: they were in the file and simply never painted, which is
    // why Firefox and SumatraPDF showed markup that KillerPDF did not.
    //
    // This mattered beyond the screen. Flatten and image export rasterize the
    // page and build a NEW document from the result, so annotations the source
    // carried were silently dropped from the output, and printing omitted them.
    // Every path that turns a page into pixels therefore uses this flag.
    //
    // NOT used by the OCR paths: those rasterize to recognize the page's own
    // text, and a reviewer's sticky note is not page content.
    //
    // Note that KillerPDF's OWN annotations are burned into the page content
    // stream on save (PdfBurn), not added as annotation objects, so nothing is
    // ever drawn twice by enabling this.
    // ============================================================
    internal static class PdfRender
    {
        /// <summary>
        /// Render the page WITH the annotations the file carries.
        ///
        /// TEMPORARILY DISABLED - it crashes. Docnet couples this flag to form-fill rendering:
        /// it builds a FormWrapper (FPDFDOC_InitFormFillEnvironment) per GetImage call, calls
        /// FPDFFFLDraw, then destroys the environment in its finally - all while the PAGE is still
        /// open, since PageReader closes the page later in its own Dispose. Tearing a form-fill
        /// environment down out of order corrupts PDFium's state, and the damage lands on the next
        /// native call: an AccessViolationException at docReader.Dispose(), in the continuous
        /// render worker's finally block.
        ///
        /// The fix is to render annotations WITHOUT the form-fill path. FPDF_ANNOT on its own draws
        /// annotation appearance streams; FPDFFFLDraw is only needed to paint interactive widgets,
        /// which this app does not want anyway - it draws its own form overlays (Forms.cs). Docnet
        /// gives no way to separate the two, so the real fix is a render through our own PDFium
        /// interop, which already owns the single-lock discipline. Until then this is off, which
        /// leaves #141 unfixed rather than crashing.
        /// </summary>
        // (Docnet's RenderFlags has no None member - zero is the "no flags" value.)
        internal const RenderFlags WithAnnotations = (RenderFlags)0;
    }
}
