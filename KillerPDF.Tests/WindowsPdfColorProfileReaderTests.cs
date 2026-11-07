using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KillerPDF.Presentation;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WindowsPdfColorProfileReaderTests
{
    [Fact]
    public void OrdinaryIccProfileKeepsItsSourceIdentity()
    {
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        byte[] before = File.ReadAllBytes(source);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(before)),
            WindowsPdfColorProfileReader.Load(source).ContentSha256);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void CancellationIsObservedBeforeOpeningTheProfile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            WindowsPdfColorProfileReader.Load("missing.icc", cancellation.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\0b")]
    public void InvalidProfileNamesAreRejected(string name)
        => Assert.Throws<ArgumentException>(() => WindowsPdfColorProfileReader.Load(name));

    [Fact]
    public void HybridWindowsProfileLoadsTheOriginalIccBytes()
    {
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "wsRGB.cdmp");
        nint name = Marshal.StringToHGlobalUni(source);
        nint wcs;
        try
        {
            var request = new NativeProfile { Type = 1, Data = name, Bytes = checked((uint)((source.Length + 1) * 2)) };
            wcs = WcsOpenColorProfileW(ref request, 0, 0, 1, 1, 3, 0);
            if (wcs == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(name); }
        byte[] bytes;
        try
        {
            nint icc = WcsCreateIccProfile(wcs, 0);
            if (icc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint size = 0;
                GetColorProfileFromHandle(icc, null, ref size);
                Assert.InRange(size, 132u, 32u * 1024 * 1024);
                bytes = new byte[size];
                Assert.True(GetColorProfileFromHandle(icc, bytes, ref size));
                Assert.Equal(bytes.Length, checked((int)size));
            }
            finally { Assert.True(CloseColorProfile(icc)); }
        }
        finally { Assert.True(CloseColorProfile(wcs)); }

        string expected = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(expected, PdfColorProfile.Load(bytes).ContentSha256);
        string path = Path.Combine(Path.GetTempPath(), "kp-hybrid-" + Guid.NewGuid().ToString("N") + ".icc");
        File.WriteAllBytes(path, bytes);
        try
        {
            var profile = WindowsPdfColorProfileReader.Load(path);
            Assert.Equal(expected, profile.ContentSha256);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeProfile { public uint Type; public nint Data; public uint Bytes; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    static extern nint WcsOpenColorProfileW(ref NativeProfile profile, nint camp, nint gmmp,
        uint access, uint share, uint creation, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    static extern nint WcsCreateIccProfile(nint profile, uint options);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetColorProfileFromHandle(nint profile, [Out] byte[]? data, ref uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("mscms.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseColorProfile(nint profile);
}
