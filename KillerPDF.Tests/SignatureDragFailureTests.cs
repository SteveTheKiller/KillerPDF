using System;
using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    // #515: a FileNotFoundException with an empty Message escaped DoDragDrop in signature placement and
    // crashed the window on every drag. Until the missing file is known, the drag failure is logged and
    // dropped, and the log and the watched-folder errors name the file instead of saying nothing.
    public class SignatureDragFailureTests
    {
        [Fact]
        public void CrashLogNamesTheMissingFile()
        {
            var sb = new StringBuilder();
            CrashExceptionText.Append(sb, new FileNotFoundException(null, @"C:\Sigs\gone.png"));
            Assert.Contains(@"FileName: C:\Sigs\gone.png", sb.ToString());
        }

        [Fact]
        public void WatchedFolderOpenErrorsCarryThePath()
        {
            var missing = Assert.IsType<FileNotFoundException>(WatchedFolderService.OpenFailure(2, @"C:\In\a.pdf"));
            Assert.Equal(@"C:\In\a.pdf", missing.FileName);
            Assert.Contains(@"C:\In\a.pdf", WatchedFolderService.OpenFailure(3, @"C:\In\a.pdf").Message);
            Assert.Contains(@"C:\In\a.pdf", WatchedFolderService.OpenFailure(5, @"C:\In\a.pdf").Message);
            Assert.Contains(@"C:\In\a.pdf", WatchedFolderService.OpenFailure(32, @"C:\In\a.pdf").Message);
        }

        // The drag keeps running inside PreviewMouseMove with its origin set, because the drop handler
        // refuses a drop once _signatureDragOrigin is gone. Only the failure is caught.
        [Fact]
        public void TheDragFailureIsLoggedWithoutDroppingTheDragOrigin()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "SignaturePlacement.cs"));
            int drag = source.IndexOf("DragDrop.DoDragDrop(", StringComparison.Ordinal);
            Assert.True(drag > 0);
            Assert.DoesNotContain("BeginInvoke", source);
            string after = source[drag..];
            Assert.Contains("catch (IOException ex)", after);
            Assert.Contains("CrashReporter.Capture(ex, \"Signature drag\")", after);
        }
    }
}
