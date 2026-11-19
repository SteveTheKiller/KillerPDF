using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OfficeAutomationSecurityTests
{
    [Theory]
    [InlineData(".doc", false, 1, false)]
    [InlineData(".xls", false, 1, false)]
    [InlineData(".doc", true, 1, false)]
    [InlineData(".xls", true, 1, false)]
    [InlineData(".doc", false, 2, true)]
    [InlineData(".xls", false, 2, true)]
    [InlineData(".doc", true, 2, true)]
    [InlineData(".xls", true, 2, true)]
    public void BinaryConversionDisablesAutomationMacrosDuringOpenAndRestoresSecurity(
        string extension, bool failOpen, int initialSecurity, bool visible)
    {
        string input = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(input, [0xD0, 0xCF, 0x11, 0xE0]);
        var app = new OfficeApplication(failOpen)
        {
            AutomationSecurity = initialSecurity,
            Visible = visible,
        };
        try
        {
            if (failOpen)
                Assert.Throws<InvalidOperationException>(() => OfficeConversion.ToPdf(input, _ => app));
            else
                Assert.Equal(new byte[] { 37, 80, 68, 70 }, OfficeConversion.ToPdf(input, _ => app));
            Assert.Equal(3, app.Documents.SecurityAtOpen);
            Assert.Equal(initialSecurity, app.AutomationSecurity);
            Assert.Equal(!visible, app.QuitCalled);
        }
        finally { File.Delete(input); }
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".xls")]
    public void AFailedExportStillClosesTheDocumentAndQuitsOurInstance(string extension)
    {
        string input = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(input, [0xD0, 0xCF, 0x11, 0xE0]);
        var app = new OfficeApplication(failOpen: false) { FailExport = true };
        try
        {
            Assert.Throws<InvalidOperationException>(() => OfficeConversion.ToPdf(input, _ => app));
            Assert.True(app.Documents.Opened!.CloseCalled);
            Assert.True(app.QuitCalled);
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public void AnOpenThatNeverReturnsTimesOutInsteadOfHangingTheCaller()
    {
        string input = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".doc");
        File.WriteAllBytes(input, [0xD0, 0xCF, 0x11, 0xE0]);
        var app = new OfficeApplication(failOpen: false) { HangOpen = true };
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            // Typed so Convert shows a localized timeout reason (#506); it is still a TimeoutException.
            Assert.IsAssignableFrom<TimeoutException>(Assert.Throws<OfficeConversionTimeoutException>(() => OfficeConversion.ToPdf(input, _ => app, TimeSpan.FromMilliseconds(300))));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            app.Release.Set();
            File.Delete(input);
        }
    }

    public sealed class OfficeApplication
    {
        public OfficeApplication(bool failOpen) => Documents = new OfficeDocuments(this, failOpen);
        public bool FailExport { get; init; }
        public bool HangOpen { get; init; }
        public System.Threading.ManualResetEventSlim Release { get; } = new(false);
        public bool Visible { get; set; }
        public object DisplayAlerts { get; set; } = false;
        public int AutomationSecurity { get; set; } = 1;
        public OfficeDocuments Documents { get; }
        public OfficeDocuments Workbooks => Documents;
        public bool QuitCalled { get; private set; }
        public void Quit() => QuitCalled = true;
    }

    public sealed class OfficeDocuments(OfficeApplication app, bool failOpen)
    {
        public int Count => 0;
        public int SecurityAtOpen { get; private set; }
        public OfficeDocument Open(string path, object links, bool readOnly,
            [System.Runtime.InteropServices.Optional] object format,
            [System.Runtime.InteropServices.Optional] object password)
        {
            SecurityAtOpen = app.AutomationSecurity;
            if (failOpen) throw new InvalidOperationException("Synthetic Office open failure.");
            if (app.HangOpen) app.Release.Wait();
            return Opened = new OfficeDocument(app.FailExport);
        }
        public OfficeDocument? Opened { get; private set; }
    }

    public sealed class OfficeDocument(bool failExport)
    {
        public bool CloseCalled { get; private set; }
        public void ExportAsFixedFormat(object first, object second)
        {
            if (failExport) throw new InvalidOperationException("Synthetic Office export failure.");
            string output = first is string path ? path : (string)second;
            File.WriteAllBytes(output, [37, 80, 68, 70]);
        }
        public void Close(object saveChanges) => CloseCalled = true;
    }
}
