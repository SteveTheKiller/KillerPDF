using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;

namespace KillerPDF.Services
{
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

        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocumentRaw(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);
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

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_SetRotation", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_SetRotationRaw(IntPtr page, int rotation);
        private static void FPDFPage_SetRotation(IntPtr page, int rotation)
        { lock (PdfiumLock) FPDFPage_SetRotationRaw(page, rotation); }

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GenerateContent", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_GenerateContentRaw(IntPtr page);
        private static bool FPDFPage_GenerateContent(IntPtr page)
        { lock (PdfiumLock) return FPDFPage_GenerateContentRaw(page); }

        // ---- Save ---------------------------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDF_SaveWithVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersionRaw(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);
        private static bool FPDF_SaveWithVersion(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion)
        { lock (PdfiumLock) return FPDF_SaveWithVersionRaw(document, ref fileWrite, flags, fileVersion); }

        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;          // must be 1
            public IntPtr WriteBlock;    // cdecl: int WriteBlock(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PdfWriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        private const uint FPDF_REMOVE_SECURITY = 3;

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

        // ---- The two direct-PDFium file operations -------------------------------------------

        /// <summary>
        /// Uses PDFium to save a copy of <paramref name="sourcePath"/> with all security/encryption
        /// removed. Returns true on success. Falls back gracefully if PDFium is unavailable.
        /// PDFium is already initialised by Docnet; no separate init call is needed.
        /// </summary>
        internal static bool TryPdfiumStripEncryption(string sourcePath, string destPath)
        {
            try
            {
                // Ensure PDFium is initialised - Docnet does this lazily on first use,
                // so force it now before we call PDFium P/Invoke directly.
                try { _ = DocLib.Instance; } catch { }

                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero) return false;
                try
                {
                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }
                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch { return false; }
        }

        /// <summary>
        /// Uses PDFium to load <paramref name="sourcePath"/>, zero-out all page /Rotate values,
        /// strip encryption, and save to <paramref name="destPath"/>. Returns true on success.
        /// Called from SaveTempAndReload's xref-error fallback - PDFium is guaranteed to be
        /// initialised by then because the page preview has already rendered via Docnet.
        /// </summary>
        internal static bool TryPdfiumSaveWithZeroRotations(string sourcePath, string destPath)
        {
            try
            {
                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FPDF_LoadDocument returned null for: {sourcePath}\n\n"); } catch { }
                    return false;
                }
                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = FPDF_LoadPage(doc, i);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            FPDFPage_SetRotation(page, 0);   // strip /Rotate so Docnet renders cleanly
                            FPDFPage_GenerateContent(page);
                        }
                        finally { FPDF_ClosePage(page); }
                    }

                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }

                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryPdfiumSaveWithZeroRotations failed\n" +
                        $"  source: {sourcePath}\n" +
                        $"  type:   {ex.GetType().FullName}\n" +
                        $"  msg:    {ex.Message}\n" +
                        $"  stack:  {ex.StackTrace}\n\n");
                }
                catch { /* log failure is non-fatal */ }
                return false;
            }
        }
    }
}
