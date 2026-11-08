using KillerPDF.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FileAssociationRegistryTests : IDisposable
{
    private const string TestParent = @"Software\KillerPDF-Tests";
    private readonly string _keyPath = TestParent + @"\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private const string Exe = @"C:\Program Files\KillerPDF\KillerPDF.exe";

    public FileAssociationRegistryTests() => _root = Registry.CurrentUser.CreateSubKey(_keyPath);

    [Theory]
    [InlineData(".pdf", "KillerPDF.pdf")]
    [InlineData(".epub", "KillerPDF.epub")]
    [InlineData(".djvu", "KillerPDF.djvu")]
    [InlineData(".djv", "KillerPDF.djvu")]
    public void RegisteredTypesOpenInKillerPdfFromOpenWithAndDefaultApps(string extension, string progId)
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        using (var openWith = _root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"))
            Assert.Contains(progId, openWith!.GetValueNames());
        using (var command = _root.OpenSubKey(@"Software\Classes\" + progId + @"\shell\open\command"))
            Assert.Equal("\"" + Exe + "\" \"%1\"", command!.GetValue(""));
        using (var supported = _root.OpenSubKey(@"Software\Classes\Applications\KillerPDF.exe\SupportedTypes"))
            Assert.Contains(extension, supported!.GetValueNames());
        using (var capabilities = _root.OpenSubKey(@"Software\KillerPDF\Capabilities\FileAssociations"))
            Assert.Equal(progId, capabilities!.GetValue(extension));
    }

    [Fact]
    public void UnregisterRemovesOnlyKillerPdfEntries()
    {
        using (var other = _root.CreateSubKey(@"Software\Classes\.epub\OpenWithProgids"))
            other.SetValue("OtherReader.epub", Array.Empty<byte>(), RegistryValueKind.None);
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        FileAssociationRegistry.Unregister(_root, "KillerPDF");

        using var openWith = _root.OpenSubKey(@"Software\Classes\.epub\OpenWithProgids");
        Assert.Equal(new[] { "OtherReader.epub" }, openWith!.GetValueNames());
        Assert.Null(_root.OpenSubKey(@"Software\Classes\KillerPDF.epub"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\KillerPDF.djvu"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\KillerPDF.pdf"));
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(TestParent, writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
    }
}
