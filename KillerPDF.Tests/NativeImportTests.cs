using System.Runtime.InteropServices;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeImportTests
{
    [Theory]
    [InlineData("user32.dll", "GetMonitorInfoW")]
    [InlineData("user32.dll", "SendMessageW")]
    [InlineData("user32.dll", "PostMessageW")]
    [InlineData("user32.dll", "LoadImageW")]
    [InlineData("kernel32.dll", "GetModuleHandleW")]
    public void ConvertedWindowsImports_NameRealExports(string libraryName, string entryPoint)
    {
        Assert.True(NativeLibrary.TryLoad(libraryName, out IntPtr library));
        try
        {
            Assert.True(NativeLibrary.TryGetExport(library, entryPoint, out _),
                $"{libraryName} does not export {entryPoint}.");
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
