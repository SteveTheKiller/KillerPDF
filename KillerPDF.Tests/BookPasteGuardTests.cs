using Xunit;

namespace KillerPDF.Tests;

// A book is read-only: it has no page layer to hold annotations and no save path for them. Pasting
// copied annotations into one marked the tab changed with nothing to save, so closing it with Yes
// to the save prompt failed and kept the tab open.
public sealed class BookPasteGuardTests
{
    [Fact]
    public void PasteDoesNothingInABookAndTheMenuDoesNotOfferIt()
    {
        string keys = ShellSource.Read("Shell", "KeyboardShortcuts.cs");
        string paste = ShellSource.Between(keys, "private void PasteInView(int pageIndex)", "// Whether Paste has anything");
        string offer = ShellSource.Between(keys, "private bool CanPasteInView()", "// Alt+Shift+G");

        // The guard comes before any annotation or clipboard paste.
        Assert.True(paste.IndexOf("NativeDocumentRef is not null", StringComparison.Ordinal) is var guard and >= 0
            && guard < paste.IndexOf("PasteAnnotations(", StringComparison.Ordinal), paste);
        Assert.True(offer.IndexOf("NativeDocumentRef is not null", StringComparison.Ordinal) is var shown and >= 0
            && shown < offer.IndexOf("MarkupToPaste()", StringComparison.Ordinal), offer);
    }
}