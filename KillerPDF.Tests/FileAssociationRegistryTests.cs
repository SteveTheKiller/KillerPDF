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

    // Windows asks the file type's shell verbs how to print: right-click Print, Start-Process -Verb Print and mail
    // clients printing an attachment use "print", and a print to a named printer uses "printto" with the printer as %2.
    // Both run KillerPDF's own headless print, which reads PDFs only.
    [Fact]
    public void PdfDocumentsPrintThroughKillerPdfFromTheShell()
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        using (var print = _root.OpenSubKey(@"Software\Classes\KillerPDF.pdf\shell\print\command"))
            Assert.Equal("\"" + Exe + "\" --print \"%1\" --shell", print?.GetValue(""));
        using (var printTo = _root.OpenSubKey(@"Software\Classes\KillerPDF.pdf\shell\printto\command"))
            Assert.Equal("\"" + Exe + "\" --print \"%1\" --printer \"%2\" --shell", printTo?.GetValue(""));
    }

    // A default chosen through Open with (or kept from an older version) points at the application key instead of the
    // ProgID, so the verbs go there too. That key also opens books, which the headless print cannot read, so its print
    // verbs apply to PDF files only.
    [Theory]
    [InlineData("KillerPDF.exe")]
    [InlineData("KillerPDF.App.exe")]
    public void ApplicationPrintVerbsApplyToPdfFilesOnly(string application)
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        string shell = @"Software\Classes\Applications\" + application + @"\shell\";
        foreach (string verb in new[] { "print", "printto" })
        {
            using var key = _root.OpenSubKey(shell + verb);
            Assert.Equal("System.FileExtension:=.pdf", key?.GetValue("AppliesTo"));
        }
        using (var print = _root.OpenSubKey(shell + @"print\command"))
            Assert.Equal("\"" + Exe + "\" --print \"%1\" --shell", print?.GetValue(""));
        using (var printTo = _root.OpenSubKey(shell + @"printto\command"))
            Assert.Equal("\"" + Exe + "\" --print \"%1\" --printer \"%2\" --shell", printTo?.GetValue(""));
    }

    [Theory]
    [InlineData("KillerPDF.epub")]
    [InlineData("KillerPDF.djvu")]
    public void BooksHaveNoPrintVerb(string progId)
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        Assert.Null(_root.OpenSubKey(@"Software\Classes\" + progId + @"\shell\print"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\" + progId + @"\shell\printto"));
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

    public static TheoryData<string> ImageExtensions => [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    // #480: images list KillerPDF under Open with, which makes a new PDF from them. The user's own image
    // viewer stays the default: the extension's default ProgID is untouched and Default apps is not offered it.
    [Theory]
    [MemberData(nameof(ImageExtensions))]
    public void ImagesOfferKillerPdfInOpenWithWithoutBecomingTheDefault(string extension)
    {
        using (var type = _root.CreateSubKey(@"Software\Classes\" + extension))
            type.SetValue("", "OtherViewer.image");
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        using (var type = _root.OpenSubKey(@"Software\Classes\" + extension))
            Assert.Equal("OtherViewer.image", type!.GetValue(""));
        using (var openWith = _root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"))
            Assert.Contains("KillerPDF.image", openWith!.GetValueNames());
        using (var open = _root.OpenSubKey(@"Software\Classes\KillerPDF.image\shell\open"))
            Assert.Equal("Player", open!.GetValue("MultiSelectModel"));
        using (var command = _root.OpenSubKey(@"Software\Classes\KillerPDF.image\shell\open\command"))
            Assert.Equal("\"" + Exe + "\" \"%1\"", command!.GetValue(""));
        foreach (string application in new[] { "KillerPDF.exe", "KillerPDF.App.exe" })
            using (var supported = _root.OpenSubKey(@"Software\Classes\Applications\" + application + @"\SupportedTypes"))
                Assert.Contains(extension, supported!.GetValueNames());
        using (var capabilities = _root.OpenSubKey(@"Software\KillerPDF\Capabilities\FileAssociations"))
            Assert.DoesNotContain(extension, capabilities!.GetValueNames());
    }

    // #479: right-click an image, Save as KillerPDF, and the PDF is written beside it without a window.
    [Theory]
    [MemberData(nameof(ImageExtensions))]
    public void ImagesHaveASaveAsKillerPdfVerb(string extension)
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf", "Als KillerPDF speichern");

        using var verb = _root.OpenSubKey(@"Software\Classes\SystemFileAssociations\" + extension + @"\shell\KillerPDF.ImagesToPdf");
        Assert.Equal("Als KillerPDF speichern", verb!.GetValue("MUIVerb"));
        Assert.Equal(Exe + ",0", verb.GetValue("Icon"));
        Assert.Equal("Player", verb.GetValue("MultiSelectModel"));
        using var command = verb.OpenSubKey("command");
        Assert.Equal("\"" + Exe + "\" --images-to-pdf \"%1\"", command!.GetValue(""));
    }

    [Fact]
    public void SaveAsKillerPdfHasAnEnglishLabelByDefault()
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        using var verb = _root.OpenSubKey(@"Software\Classes\SystemFileAssociations\.png\shell\KillerPDF.ImagesToPdf");
        Assert.Equal("Save as KillerPDF", verb!.GetValue("MUIVerb"));
    }

    [Fact]
    public void UnregisterRemovesEveryImageEntryAndKeepsOtherApplications()
    {
        using (var other = _root.CreateSubKey(@"Software\Classes\.jpg\OpenWithProgids"))
            other.SetValue("OtherViewer.jpg", Array.Empty<byte>(), RegistryValueKind.None);
        using (var other = _root.CreateSubKey(@"Software\Classes\SystemFileAssociations\.jpg\shell\OtherEdit\command"))
            other.SetValue("", "other.exe \"%1\"");
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        FileAssociationRegistry.Unregister(_root, "KillerPDF");

        using (var openWith = _root.OpenSubKey(@"Software\Classes\.jpg\OpenWithProgids"))
            Assert.Equal(new[] { "OtherViewer.jpg" }, openWith!.GetValueNames());
        using (var shell = _root.OpenSubKey(@"Software\Classes\SystemFileAssociations\.jpg\shell"))
            Assert.Equal(new[] { "OtherEdit" }, shell!.GetSubKeyNames());
        Assert.Null(_root.OpenSubKey(@"Software\Classes\KillerPDF.image"));
        foreach (string extension in new[] { ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" })
        {
            Assert.Null(_root.OpenSubKey(@"Software\Classes\" + extension));
            Assert.Null(_root.OpenSubKey(@"Software\Classes\SystemFileAssociations\" + extension));
        }
    }

    // A per-user install often starts without any of these keys, so none of them may outlive Unregister.
    [Fact]
    public void UnregisterLeavesNoImageKeyOnAFreshRoot()
    {
        FileAssociationRegistry.Register(_root, Exe, "\"pdf.ico\",0", "KillerPDF", "killerpdf");

        FileAssociationRegistry.Unregister(_root, "KillerPDF");

        Assert.Null(_root.OpenSubKey(@"Software\Classes\SystemFileAssociations"));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\KillerPDF.image"));
        foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" })
            Assert.Null(_root.OpenSubKey(@"Software\Classes\" + extension));
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
