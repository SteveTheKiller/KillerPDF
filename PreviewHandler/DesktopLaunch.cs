using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KillerPDF.PreviewHandler;

/// <summary>
/// Starts KillerPDF from the preview pane at the user's normal integrity level. The pane runs in
/// prevhost.exe at low integrity, the sandbox Windows gives previews, and anything it starts
/// directly inherits that sandbox, where the app could not save to Documents. Explorer's desktop
/// runs at the normal level, so the pane asks it to start the app, the same way a double-click
/// would. When Explorer cannot be reached the app starts directly, sandboxed, which still shows
/// the document.
/// </summary>
internal static class DesktopLaunch
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserId = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    /// <summary>%LOCALAPPDATA%Low, the one profile folder a low-integrity process can write.</summary>
    internal static string LowWritableFolder()
    {
        if (SHGetKnownFolderPath(LocalAppDataLow, 0, IntPtr.Zero, out IntPtr path) != 0)
            return Path.GetTempPath();
        try { return Marshal.PtrToStringUni(path) ?? Path.GetTempPath(); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    internal static void Start(string executable, string argument)
    {
        if (TryThroughExplorer(executable, argument)) return;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    private static bool TryThroughExplorer(string executable, string argument)
    {
        object? windows = null, desktop = null, browser = null, view = null, folderView = null, shell = null;
        try
        {
            Type? type = Type.GetTypeFromCLSID(ShellWindowsClass);
            if (type is null) return false;
            windows = Activator.CreateInstance(type);
            if (windows is not IShellWindows shellWindows) return false;
            object location = CsidlDesktop;
            object root = new();
            desktop = shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (desktop is not IServiceProvider provider) return false;
            Guid service = TopLevelBrowser, browserId = ShellBrowserId;
            if (provider.QueryService(ref service, ref browserId, out IntPtr browserPointer) != 0 || browserPointer == IntPtr.Zero)
                return false;
            browser = Marshal.GetObjectForIUnknown(browserPointer);
            Marshal.Release(browserPointer);
            if (browser is not IShellBrowser shellBrowser || shellBrowser.QueryActiveShellView(out view) != 0
                || view is not IShellView shellView)
                return false;
            Guid dispatch = DispatchId;
            if (shellView.GetItemObject(SvgioBackground, ref dispatch, out folderView) != 0 || folderView is null)
                return false;
            // IShellFolderViewDual.Application is Explorer's IShellDispatch2; its ShellExecute runs
            // inside Explorer.
            dynamic dual = folderView;
            shell = dual.Application;
            dynamic dispatcher = shell!;
            dispatcher.ShellExecute(executable, "\"" + argument + "\"",
                Path.GetDirectoryName(executable) ?? "", "open", 1);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (object? com in new[] { shell, folderView, view, browser, desktop, windows })
                if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object item, int window, int windowClass);
        int RegisterPending(int threadId, ref object location, ref object root, int windowClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object location, ref object root, int windowClass, out int window, int options);
        void OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object item);
        void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int InsertMenusSB(IntPtr shared, IntPtr widths);
        [PreserveSig] int SetMenuSB(IntPtr shared, IntPtr ole, IntPtr active);
        [PreserveSig] int RemoveMenusSB(IntPtr shared);
        [PreserveSig] int SetStatusTextSB(IntPtr text);
        [PreserveSig] int EnableModelessSB(int enable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
        [PreserveSig] int BrowseObject(IntPtr item, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr window);
        [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
        [PreserveSig] int OnViewWindowActive(IntPtr view);
        [PreserveSig] int SetToolbarItems(IntPtr buttons, uint count, uint flags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp(int enter);
        [PreserveSig] int TranslateAccelerator(IntPtr message);
        [PreserveSig] int EnableModeless(int enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr window);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(IntPtr settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr lParam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr item, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}
