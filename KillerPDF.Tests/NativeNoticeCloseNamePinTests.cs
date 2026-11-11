using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PIN (APP-BF-009). The notice is built inside the viewer, which this project does not compile.
public sealed class NativeNoticeCloseNamePinTests
{
    // A name read once at build time keeps the language the app had then. A resource reference follows a language switch.
    [Fact]
    public void TheNoticeCloseButtonNameFollowsTheLanguage()
    {
        string notice = Viewer("PdfViewer.NativeNotice.cs");

        Assert.Contains("close.SetResourceReference(AutomationProperties.NameProperty, \"Str_Restricted_Dismiss\");", notice);
        Assert.DoesNotContain("AutomationProperties.SetName(close,", notice);
    }
}
