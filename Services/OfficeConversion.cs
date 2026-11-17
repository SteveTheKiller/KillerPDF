using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>Raised when an older .doc or .xls file needs Word or Excel and it is not installed.</summary>
internal sealed class OfficeNotInstalledException(string application)
    : InvalidOperationException($"Microsoft {application} is not installed.")
{
    internal string Application { get; } = application;
}

/// <summary>
/// Document to PDF (#74). Word .docx and Excel .xlsx files are converted by KillerPDF itself.
/// The older binary .doc and .xls formats are exported through Word or Excel when installed,
/// in a hidden instance that is closed afterwards. The file's content decides the route, so a
/// .doc that is really a .docx package converts internally.
/// </summary>
internal static class OfficeConversion
{
    internal static readonly string[] Extensions = [".docx", ".doc", ".xlsx", ".xls"];

    internal static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Word or Excel can sit on a hidden prompt (a password, a repair offer) that no one will
    // ever answer in an automation instance; the export is abandoned after this long.
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    internal static byte[] ToPdf(string path) => ToPdf(path, CreateApplication);

    internal static byte[] ToPdf(string path, Func<bool, object> createApplication) =>
        ToPdf(path, createApplication, DefaultTimeout);

    internal static byte[] ToPdf(string path, Func<bool, object> createApplication, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        bool spreadsheet = Path.GetExtension(path).StartsWith(".xl", StringComparison.OrdinalIgnoreCase);
        if (bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var archive = OfficePackage.Open(bytes);
            string main = OfficePackage.MainPart(archive, spreadsheet ? "xl/workbook.xml" : "word/document.xml");
            if (main.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return XlsxConverter.Convert(bytes);
            if (main.StartsWith("word/", StringComparison.OrdinalIgnoreCase)) return DocxConverter.Convert(bytes);
            throw new InvalidDataException("The file is not a Word or Excel document.");
        }
        return InstalledOffice(path, word: !spreadsheet, createApplication, timeout);
    }

    private static byte[] InstalledOffice(string path, bool word, Func<bool, object> createApplication, TimeSpan timeout)
    {
        string output = Path.Combine(Path.GetTempPath(), $"KillerPDF-convert-{Guid.NewGuid():N}.pdf");
        Exception? failure = null;
        var session = new ExportSession();
        var thread = new Thread(() =>
        {
            try { Export(Path.GetFullPath(path), output, word, createApplication, session); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Name = "KillerPDF Office export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout))
        {
            // The STA thread is stuck inside Office. An instance this export created is ended by
            // process, since no call into it will return; a user's own instance is left alone.
            session.EndOwnedProcess();
            throw new TimeoutException(word
                ? "Word did not finish converting the document."
                : "Excel did not finish converting the workbook.");
        }
        try
        {
            if (failure is TargetInvocationException { InnerException: Exception inner }) failure = inner;
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (!File.Exists(output)) throw new IOException("The export did not produce a PDF.");
            return File.ReadAllBytes(output);
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static object CreateApplication(bool word)
    {
        string application = word ? "Word" : "Excel";
        Type type = Type.GetTypeFromProgID(word ? "Word.Application" : "Excel.Application")
            ?? throw new OfficeNotInstalledException(application);
        try { return Activator.CreateInstance(type) ?? throw new OfficeNotInstalledException(application); }
        catch (COMException) { throw new OfficeNotInstalledException(application); }
    }

    // What the export thread knows about the instance it is driving, readable from the caller's
    // thread when the export does not come back.
    private sealed class ExportSession
    {
        private int _ownedProcessId;

        internal void NoteOwnedApplication(object app)
        {
            try
            {
                if (Get(app, "Hwnd") is { } handle && Convert.ToInt64(handle) is long hwnd && hwnd != 0)
                {
                    GetWindowThreadProcessId((IntPtr)hwnd, out uint processId);
                    Volatile.Write(ref _ownedProcessId, (int)processId);
                }
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException
                or InvalidCastException or FormatException) { }
        }

        internal void EndOwnedProcess()
        {
            int processId = Volatile.Read(ref _ownedProcessId);
            if (processId == 0) return;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }

    // Late-bound so KillerPDF needs no Office interop assemblies. An instance that was already
    // visible or has existing documents is left running with its documents.
    private static void Export(string path, string output, bool word, Func<bool, object> createApplication,
        ExportSession session)
    {
        object? app = null, documents = null, document = null;
        bool ours = false;
        try
        {
            app = createApplication(word);
            bool hidden = Get(app, "Visible") is false;
            documents = Get(app, word ? "Documents" : "Workbooks");
            // Establish ownership before registering a PID for timeout cleanup.
            ours = hidden && Get(documents!, "Count") is 0;
            if (ours)
            {
                Set(app, "Visible", false);
                Set(app, "DisplayAlerts", word ? (object)0 : false);
                session.NoteOwnedApplication(app);
            }
            if (word)
            {
                // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument.
                var security = Get(app, "AutomationSecurity")!;
                // Linked files and fields would otherwise be refreshed from wherever the
                // document points, which can be a remote share that collects the user's hash.
                object? options = TryGet(app, "Options");
                object? updateLinks = options is null ? null : TryGet(options, "UpdateLinksAtOpen");
                try
                {
                    Set(app, "AutomationSecurity", 3);
                    if (options is not null) TrySet(options, "UpdateLinksAtOpen", false);
                    document = Call(documents!, "Open", path, false, true, false, "");
                }
                finally
                {
                    Set(app, "AutomationSecurity", security);
                    if (options is not null && updateLinks is not null) TrySet(options, "UpdateLinksAtOpen", updateLinks);
                    Release(options);
                }
                try { Call(document!, "ExportAsFixedFormat", output, 17); }
                finally { TryClose(document!, 0); }
            }
            else
            {
                // Filename, UpdateLinks, ReadOnly, Format, Password.
                var security = Get(app, "AutomationSecurity")!;
                // Auto_Open and other Excel 4.0 macros run from workbook events, which
                // AutomationSecurity does not cover, so events stay off while the workbook is open.
                object? events = TryGet(app, "EnableEvents");
                try
                {
                    // msoAutomationSecurityForceDisable blocks VBA, but not Excel 4.0 macros.
                    Set(app, "AutomationSecurity", 3);
                    TrySet(app, "EnableEvents", false);
                    document = Call(documents!, "Open", path, 0, true, Type.Missing, "");
                }
                finally
                {
                    Set(app, "AutomationSecurity", security);
                    if (events is not null) TrySet(app, "EnableEvents", events);
                }
                try { Call(document!, "ExportAsFixedFormat", 0, output); }
                finally { TryClose(document!, false); }
            }
        }
        finally
        {
            Release(document);
            Release(documents);
            if (app is not null)
            {
                if (ours)
                {
                    try { Call(app, "Quit"); }
                    catch (Exception ex) when (ex is COMException or TargetInvocationException) { }
                }
                Release(app);
            }
        }
    }

    // The document is closed whether or not the export succeeded, so a failed export never
    // leaves it open in the user's Word or Excel or holds the file.
    private static void TryClose(object document, object saveChanges)
    {
        try { Call(document, "Close", saveChanges); }
        catch (Exception ex) when (ex is COMException or TargetInvocationException) { }
    }

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    // For settings an older Office may lack: a missing one is skipped, not a failed conversion.
    private static object? TryGet(object target, string name)
    {
        try { return Get(target, name); }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException) { return null; }
    }

    private static void TrySet(object target, string name, object value)
    {
        try { Set(target, name, value); }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException) { }
    }

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
