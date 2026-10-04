using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KillerPDF
{
    /// <summary>
    /// Gives the standalone install and uninstall windows their own taskbar button, labeled
    /// "KillerPDF Installer" or "Uninstall KillerPDF" with the app icon, instead of grouping them
    /// under the exe name. The taskbar takes the label from the window's relaunch display name.
    /// </summary>
    internal static class TaskbarIdentity
    {
        private const string AppName = "KillerPDF";
        private const int WmDestroy = 0x0002;

        private static string? _label;
        private static string? _id;
        private static string _arguments = string.Empty;

        /// <summary>Labels the ownerless windows of this process as the uninstaller.</summary>
        internal static void UseUninstall() => Use("Uninstall " + AppName, "Uninstall", "/uninstall");

        /// <summary>Labels the ownerless windows of this process as the installer.</summary>
        internal static void UseInstaller() => Use(AppName + " Installer", "Installer", string.Empty);

        private static void Use(string label, string role, string arguments)
        {
            _label = label;
            _id = "SteveTheKiller." + AppName + "." + role;
            _arguments = arguments;
        }

        /// <summary>Applies the label when the window opens without an owner. Windows opened
        /// over the main window keep the app's own taskbar button.</summary>
        internal static void Track(Window window) => window.SourceInitialized += (_, _) => Apply(window);

        private static void Apply(Window window)
        {
            if (_label == null || window.Owner != null) return;
            window.ShowInTaskbar = true;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            string command = "\"" + exe + "\"" + (_arguments.Length > 0 ? " " + _arguments : string.Empty);
            if (!Write(handle, _id, command, exe + ",0", _label)) return;
            HwndSource.FromHwnd(handle)?.AddHook(ClearOnDestroy);
        }

        // The shell expects these properties to be removed before the window is destroyed.
        private static IntPtr ClearOnDestroy(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmDestroy) Write(hwnd, null, null, null, null);
            return IntPtr.Zero;
        }

        private static bool Write(IntPtr handle, string? id, string? command, string? icon, string? label)
        {
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(handle, ref iid, out IPropertyStore store) != 0) return false;
                try
                {
                    Set(store, RelaunchCommandKey, command);
                    Set(store, RelaunchIconKey, icon);
                    Set(store, RelaunchNameKey, label);
                    Set(store, AppIdKey, id);
                    store.Commit();
                    return true;
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch (Exception)
            {
                // A missing taskbar label must never stop setup.
                return false;
            }
        }

        private static readonly Guid AppModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly PropertyKey RelaunchCommandKey = new PropertyKey(AppModel, 2);
        private static readonly PropertyKey RelaunchIconKey = new PropertyKey(AppModel, 3);
        private static readonly PropertyKey RelaunchNameKey = new PropertyKey(AppModel, 4);
        private static readonly PropertyKey AppIdKey = new PropertyKey(AppModel, 5);

        private static void Set(IPropertyStore store, PropertyKey key, string? value)
        {
            var variant = new PropVariant();
            if (value != null)
            {
                variant.Type = 31;   // VT_LPWSTR
                variant.Pointer = Marshal.StringToCoTaskMemUni(value);
            }
            try { store.SetValue(ref key, ref variant); }
            finally { if (variant.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(variant.Pointer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
            public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, out IPropertyStore store);
    }
}
