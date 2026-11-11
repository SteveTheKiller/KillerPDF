using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (APP-BF-008). The viewer is not compiled into this project, and moving a tab between panes needs two
// live viewers, so the behavior evidence is the written window check in the 1.9.3 notes (needs-vm).
public sealed class NativeNoticeDismissalPinTests
{
    // The dismissal used to live in a table owned by each viewer, so the viewer that received a moved tab never knew the
    // notice had been dismissed. One table for the process, keyed by the document, travels with the document.
    [Fact]
    public void ADismissedBookNoticeIsRememberedPerDocumentNotPerViewer()
    {
        string notice = Viewer("PdfViewer.NativeNotice.cs");

        Assert.Contains("private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> NativeNoticeDismissed = [];", notice);
        Assert.DoesNotContain("private readonly System.Runtime.CompilerServices.ConditionalWeakTable", notice);
        Assert.Equal(2, Count(notice, "NativeNoticeDismissed."));
        Assert.DoesNotContain("_nativeNoticeDismissed", notice);
    }
}
