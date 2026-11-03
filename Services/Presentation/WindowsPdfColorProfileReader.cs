using KillerPdf.Engine.ColorManagement;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KillerPDF.Presentation;

/// <summary>Reads an explicitly named Windows profile into the portable color engine.</summary>
public static class WindowsPdfColorProfileReader
{
    /// <summary>Loads an existing ICC profile without changing its display association.</summary>
    public static PdfColorProfile Load(string profileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (profileName.Length >= 32768 || profileName.Contains('\0'))
            throw new ArgumentException("The profile name exceeds its limit or contains a null character.", nameof(profileName));
        cancellationToken.ThrowIfCancellationRequested();
        nint name = Marshal.StringToHGlobalUni(profileName);
        nint handle;
        try
        {
            var profile = new Profile { Type = 1, Data = name, Bytes = checked((uint)((profileName.Length + 1) * 2)) };
            handle = OpenColorProfileW(ref profile, 1, 1, 3);
            if (handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenColorProfileW failed.");
        }
        finally { Marshal.FreeHGlobal(name); }
        try
        {
            uint size = 0;
            // A null-buffer query normally returns false while supplying the required size.
            GetColorProfileFromHandle(handle, null, ref size);
            if (size == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetColorProfileFromHandle returned no profile bytes.");
            if (size > 32 * 1024 * 1024)
                throw new System.IO.InvalidDataException("The Windows profile exceeds the 32 MiB input limit.");
            cancellationToken.ThrowIfCancellationRequested();
            var data = new byte[checked((int)size)];
            if (!GetColorProfileFromHandle(handle, data, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetColorProfileFromHandle failed.");
            if (size == 0 || size > data.Length)
                throw new System.IO.InvalidDataException("The Windows profile byte count is invalid.");
            cancellationToken.ThrowIfCancellationRequested();
            return PdfColorProfile.Load(data.AsMemory(0, checked((int)size)));
        }
        finally
        {
            if (!CloseColorProfile(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CloseColorProfile failed.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Profile { public uint Type; public nint Data; public uint Bytes; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    static extern nint OpenColorProfileW(ref Profile profile, uint access, uint share, uint creation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetColorProfileFromHandle(nint profile, [Out] byte[]? data, ref uint bytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseColorProfile(nint profile);
}
