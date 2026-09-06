using System.Runtime.InteropServices;
using Docnet.Core;

namespace KillerPDF.Services
{
    // PDFium uses cdecl callbacks, mutable byte buffers, and native structs across one audited
    // runtime-marshalling boundary. LibraryImport does not cover every signature in this bridge,
    // so keep one consistent DllImport surface rather than mixing incompatible stub strategies.
#pragma warning disable SYSLIB1054
    // ============================================================
    // Direct PDFium P/Invoke - the ONE home for every direct
    // pdfium.dll call in the app (KillerUI refactor; formerly split
    // across FileOperations.cs and Links.cs).
    //
    // THREADING: PDFium is single-threaded. Docnet serializes every
    // native call it makes on an internal static lock
    // (Docnet.Core.DocLib.Lock). Every DIRECT pdfium.dll call in
    // this app must hold that SAME lock, or a background Docnet
    // render and a direct call (link extraction, encryption strip)
    // can be inside PDFium at the same time - native heap
    // corruption, exit code 0xc0000374. Confirmed from a 1.6.3
    // crash dump (2026-07-17): two threads with concurrent PDFium
    // frames. The raw externs are suffixed Raw; only the
    // lock-holding wrappers may be called. Keeping every extern in
    // this one class is what keeps the lock discipline auditable.
    // ============================================================
    internal static class PdfiumInterop
    {
        internal static readonly object PdfiumLock =
            typeof(DocLib).GetField("Lock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) ?? new object();

        // ---- Document / page lifecycle -------------------------------------------------------

#pragma warning disable CA2101 // PDFium requires UTF-8 narrow strings, explicitly declared below.
        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocumentRaw(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
#pragma warning restore CA2101
        internal static IntPtr FPDF_LoadDocument(string filePath, string? password)
        { lock (PdfiumLock) return FPDF_LoadDocumentRaw(filePath, password); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_CloseDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocumentRaw(IntPtr document);
        internal static void FPDF_CloseDocument(IntPtr document)
        { lock (PdfiumLock) FPDF_CloseDocumentRaw(document); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCountRaw(IntPtr document);
        private static int FPDF_GetPageCount(IntPtr document)
        { lock (PdfiumLock) return FPDF_GetPageCountRaw(document); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadPage", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPageRaw(IntPtr document, int page_index);
        internal static IntPtr FPDF_LoadPage(IntPtr document, int page_index)
        { lock (PdfiumLock) return FPDF_LoadPageRaw(document, page_index); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_ClosePage", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePageRaw(IntPtr page);
        internal static void FPDF_ClosePage(IntPtr page)
        { lock (PdfiumLock) FPDF_ClosePageRaw(page); }

        // ---- Page rendering ----------------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_CreateEx", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_CreateExRaw(
            int width, int height, int format, IntPtr firstScan, int stride);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_Destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_DestroyRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_FillRect", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_FillRectRaw(
            IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_RenderPageBitmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmapRaw(
            IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
            int rotate, int flags);

        [DllImport("pdfium.dll", EntryPoint = "FPDFDOC_InitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFDOC_InitFormFillEnvironmentRaw(
            IntPtr document, IntPtr formInfo);

        [DllImport("pdfium.dll", EntryPoint = "FPDFDOC_ExitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFDOC_ExitFormFillEnvironmentRaw(IntPtr formHandle);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_FFLDraw", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_FFLDrawRaw(
            IntPtr formHandle, IntPtr bitmap, IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int flags);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GetAnnotCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_GetAnnotCountRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GetAnnot", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPage_GetAnnotRaw(IntPtr page, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_CloseAnnot", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_CloseAnnotRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_GetSubtype", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_GetSubtypeRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_GetFlags", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_GetFlagsRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_SetFlags", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_SetFlagsRaw(IntPtr annot, int flags);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_GetFormFieldFlags", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_GetFormFieldFlagsRaw(IntPtr formHandle, IntPtr annot);

        private const int FPDFBitmapBgra = 4;
        private const int FpdfAnnot = 0x01;
        private const int FpdfLcdText = 0x02;
        private const int FpdfAnnotSubtypeWidget = 20;   // fpdf_annot.h FPDF_ANNOT_WIDGET
        private const int FpdfAnnotFlagHidden = 1 << 1;  // fpdf_annot.h FPDF_ANNOT_FLAG_HIDDEN
        private const int FpdfFormFlagPushButton = 1 << 16;  // fpdf_annot.h FPDF_FORMFLAG_BUTTON_PUSHBUTTON

        // Marks every WIDGET annotation on the loaded page hidden so the FPDF_ANNOT render pass
        // does not paint form-field appearances, and returns each widget's original flags by
        // annotation index so RestoreWidgetAnnotationFlags can put them back before FFLDraw.
        // In-memory only: this renderer's document is a one-shot load that is closed right after,
        // never saved. EntryPointNotFound (an older bundled PDFium without the annot API) returns
        // null and degrades to leaving the fields baked in.
        // sawPushButton reports whether this page carried one. Push buttons are the only
        // widgets left without a live overlay, so they are the only reason the viewer needs
        // FFLDraw at all, and the caller uses this to skip that call on every page that has
        // none. False whenever the annot API was unavailable, which is the safe direction.
        private static System.Collections.Generic.Dictionary<int, int>? HideWidgetAnnotations(
            IntPtr page, IntPtr formHandle, out bool sawPushButton)
        {
            sawPushButton = false;
            try
            {
                var saved = new System.Collections.Generic.Dictionary<int, int>();
                int count = FPDFPage_GetAnnotCountRaw(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = FPDFPage_GetAnnotRaw(page, i);
                    if (annot == IntPtr.Zero) continue;
                    try
                    {
                        if (FPDFAnnot_GetSubtypeRaw(annot) == FpdfAnnotSubtypeWidget)
                        {
                            // Push buttons have no live WPF editor overlay. Leave their authored
                            // appearance in the page bitmap so interactive viewing cannot erase them.
                            int formFlags = FormFieldFlags(formHandle, annot);
                            if ((formFlags & FpdfFormFlagPushButton) != 0)
                            {
                                sawPushButton = true;
                                continue;
                            }
                            int flags = FPDFAnnot_GetFlagsRaw(annot);
                            saved[i] = flags;
                            _ = FPDFAnnot_SetFlagsRaw(annot, flags | FpdfAnnotFlagHidden);
                        }
                    }
                    finally { FPDFPage_CloseAnnotRaw(annot); }
                }
                return saved;
            }
            catch { return null; /* annot API unavailable: fields stay baked, no crash */ }
        }

        private static int FormFieldFlags(IntPtr formHandle, IntPtr annotation)
        {
            if (formHandle == IntPtr.Zero) return 0;
            try { return FPDFAnnot_GetFormFieldFlagsRaw(formHandle, annotation); }
            catch (EntryPointNotFoundException) { return 0; }
        }

        // Puts back the widget flags HideWidgetAnnotations saved, so FFLDraw sees the original
        // visibility: a genuinely hidden field stays hidden, everything else draws once there.
        private static void RestoreWidgetAnnotationFlags(
            IntPtr page, System.Collections.Generic.Dictionary<int, int>? saved)
        {
            if (saved is null) return;
            try
            {
                foreach (var kv in saved)
                {
                    IntPtr annot = FPDFPage_GetAnnotRaw(page, kv.Key);
                    if (annot == IntPtr.Zero) continue;
                    try { _ = FPDFAnnot_SetFlagsRaw(annot, kv.Value); }
                    finally { FPDFPage_CloseAnnotRaw(annot); }
                }
            }
            catch { }
        }

        /// <summary>
        /// Renders one page through PDFium with annotation appearance streams enabled, but without
        /// Docnet's form-fill environment. This avoids the native teardown crash caused by Docnet's
        /// annotation flag while still painting text notes, highlights, stamps, ink, and widget
        /// appearances into viewer, print, flatten, and image-export pixels.
        /// </summary>
        /// <param name="includeFormFields">False for the on-screen viewer, whose live form
        /// overlays already show the field values - baking them into the page bitmap as well
        /// painted the same text twice, slightly offset (the "drop shadow" ghost, thanks Thomas).
        /// True everywhere the pixels ARE the output: print, flatten, export, thumbnails.</param>
        internal static byte[]? RenderPageWithAnnotations(
            string sourcePath, int pageIndex, int width, int height,
            bool transparentBackground = false, bool includeFormFields = true)
        {
            if (width <= 0 || height <= 0) return null;
            try
            {
                try { _ = DocLib.Instance; } catch { }
                int stride = checked(width * 4);
                var bytes = new byte[checked(stride * height)];
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    lock (PdfiumLock)
                    {
                        IntPtr doc = FPDF_LoadDocumentRaw(sourcePath, null);
                        if (doc == IntPtr.Zero) return null;
                        // PDFium retains this pointer until ExitFormFillEnvironment, so it must be
                        // stable unmanaged memory, not a temporary buffer produced by the P/Invoke
                        // marshaller. The bundled ABI is one 32-bit version plus 31 pointer slots.
                        int formInfoSize = IntPtr.Size == 8 ? 256 : 128;
                        IntPtr formInfo = Marshal.AllocHGlobal(formInfoSize);
                        for (int offset = 0; offset < formInfoSize; offset += 4)
                            Marshal.WriteInt32(formInfo, offset, 0);
                        IntPtr form = IntPtr.Zero;
                        for (int version = 1; version <= 2 && form == IntPtr.Zero; version++)
                        {
                            Marshal.WriteInt32(formInfo, 0, version);
                            form = FPDFDOC_InitFormFillEnvironmentRaw(doc, formInfo);
                        }
                        try
                        {
                            IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                            if (page == IntPtr.Zero) return null;
                            try
                            {
                                // Widgets are hidden for the static FPDF_ANNOT pass in BOTH modes:
                                // the on-screen viewer replaces them with live overlays, and the
                                // output path paints them once via FFLDraw below - letting the
                                // static pass draw the /AP as well painted every field twice
                                // whenever the /AP layout and FFLDraw's (NeedAppearances) layout
                                // disagreed. If the output path has no form environment to draw
                                // with, the widgets stay visible so the static pass still shows them.
                                bool sawPushButton = false;
                                var savedWidgetFlags = includeFormFields && form == IntPtr.Zero
                                    ? null
                                    : HideWidgetAnnotations(page, form, out sawPushButton);
                                IntPtr bitmap = FPDFBitmap_CreateExRaw(
                                    width, height, FPDFBitmapBgra, pinned.AddrOfPinnedObject(), stride);
                                if (bitmap == IntPtr.Zero) return null;
                                try
                                {
                                    FPDFBitmap_FillRectRaw(bitmap, 0, 0, width, height,
                                        transparentBackground ? 0x00000000 : 0xFFFFFFFF);
                                    FPDF_RenderPageBitmapRaw(bitmap, page, 0, 0, width, height, 0,
                                        FpdfAnnot | FpdfLcdText);
                                    // Push buttons have no live overlay, and the static pass above
                                    // does not paint them: it is byte-identical in both modes, yet
                                    // they only ever appear on the output path, so FFLDraw is what
                                    // draws them. The viewer therefore needs FFLDraw too.
                                    //
                                    // Output mode restores every widget first so FFLDraw paints them
                                    // all, exactly as before. Viewer mode leaves the hidden flags on,
                                    // so FFLDraw paints only what HideWidgetAnnotations deliberately
                                    // skipped - the push buttons. Text, choice and check fields stay
                                    // hidden, so the ghosted-value regression from 1.7.2 cannot return.
                                    //
                                    // The viewer takes that second pdfium call only on a page that
                                    // actually holds a push button, so a document without any renders
                                    // through the same single pass it always did.
                                    //
                                    // If hiding was unavailable (an older bundled PDFium returns null
                                    // from HideWidgetAnnotations), the viewer skips FFLDraw rather
                                    // than painting every field over its own overlay.
                                    if (form != IntPtr.Zero
                                        && (includeFormFields
                                            || (savedWidgetFlags is not null && sawPushButton)))
                                    {
                                        if (includeFormFields)
                                            RestoreWidgetAnnotationFlags(page, savedWidgetFlags);
                                        FPDF_FFLDrawRaw(form, bitmap, page, 0, 0, width, height, 0,
                                            FpdfAnnot | FpdfLcdText);
                                    }
                                }
                                finally { FPDFBitmap_DestroyRaw(bitmap); }
                            }
                            finally
                            {
                                // This PDFium build expects the form environment to be released while
                                // its page is still alive. The one-shot renderer closes the page and
                                // document immediately afterwards, so no damaged native state is reused.
                                if (form != IntPtr.Zero)
                                {
                                    FPDFDOC_ExitFormFillEnvironmentRaw(form);
                                    form = IntPtr.Zero;
                                }
                                FPDF_ClosePageRaw(page);
                            }
                        }
                        finally
                        {
                            if (form != IntPtr.Zero) FPDFDOC_ExitFormFillEnvironmentRaw(form);
                            Marshal.FreeHGlobal(formInfo);
                            FPDF_CloseDocumentRaw(doc);
                        }
                    }
                    return bytes;
                }
                finally { pinned.Free(); }
            }
            catch { return null; }
        }

        // ---- Save ---------------------------------------------------------------------------

        // ---- Link extraction entry points (fallback for object-stream PDFs) ------------------
        // PdfSharpCore silently drops link annotations stored in object streams (linearized /
        // PDF 1.5+); PDFium resolves them natively. Consumed by Links.cs' cached-handle pass.

        [StructLayout(LayoutKind.Sequential)]
        internal struct FS_RECTF { public float left, top, right, bottom; }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageWidth", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageWidthRaw(IntPtr page);
        internal static double FPDF_GetPageWidth(IntPtr page)
        { lock (PdfiumLock) return FPDF_GetPageWidthRaw(page); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_GetPageHeight", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageHeightRaw(IntPtr page);
        internal static double FPDF_GetPageHeight(IntPtr page)
        { lock (PdfiumLock) return FPDF_GetPageHeightRaw(page); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFLink_Enumerate", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFLink_EnumerateRaw(IntPtr page, ref int startPos, out IntPtr linkAnnot);
        internal static bool FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot)
        { lock (PdfiumLock) return FPDFLink_EnumerateRaw(page, ref startPos, out linkAnnot); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFLink_GetAnnotRect", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFLink_GetAnnotRectRaw(IntPtr linkAnnot, out FS_RECTF rect);
        internal static bool FPDFLink_GetAnnotRect(IntPtr linkAnnot, out FS_RECTF rect)
        { lock (PdfiumLock) return FPDFLink_GetAnnotRectRaw(linkAnnot, out rect); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFLink_GetDest", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFLink_GetDestRaw(IntPtr document, IntPtr link);
        internal static IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link)
        { lock (PdfiumLock) return FPDFLink_GetDestRaw(document, link); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFLink_GetAction", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFLink_GetActionRaw(IntPtr link);
        internal static IntPtr FPDFLink_GetAction(IntPtr link)
        { lock (PdfiumLock) return FPDFLink_GetActionRaw(link); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFAction_GetType", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFAction_GetTypeRaw(IntPtr action);
        internal static uint FPDFAction_GetType(IntPtr action)
        { lock (PdfiumLock) return FPDFAction_GetTypeRaw(action); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFAction_GetDest", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFAction_GetDestRaw(IntPtr document, IntPtr action);
        internal static IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action)
        { lock (PdfiumLock) return FPDFAction_GetDestRaw(document, action); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFAction_GetURIPath", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFAction_GetURIPathRaw(IntPtr document, IntPtr action, byte[]? buffer, uint buflen);
        internal static uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, byte[]? buffer, uint buflen)
        { lock (PdfiumLock) return FPDFAction_GetURIPathRaw(document, action, buffer, buflen); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFDest_GetDestPageIndex", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFDest_GetDestPageIndexRaw(IntPtr document, IntPtr dest);
        internal static int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest)
        { lock (PdfiumLock) return FPDFDest_GetDestPageIndexRaw(document, dest); }

        /// <summary>Removes owner-only encryption through the engine.</summary>
        internal static bool TryRemoveEncryptionWithoutPassword(
            string sourcePath, string destinationPath)
        {
            try
            {
                PdfEngineIntegration.RemoveEncryption(
                    sourcePath, destinationPath, string.Empty);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }
        }

        /// <summary>Creates a zero-rotation copy through the engine.</summary>
        internal static bool TryCreateZeroRotationCopy(
            string sourcePath, string destinationPath)
        {
            try
            {
                PdfEngineIntegration.CreateZeroRotationCopy(sourcePath, destinationPath);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }
        }
    }
#pragma warning restore SYSLIB1054
}
