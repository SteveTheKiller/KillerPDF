using System.IO;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfMacroStoreTests
{
    [Fact]
    public void SaveLoadAndRemove_RoundTripsOnlyValidatedMacros()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-macros-{Guid.NewGuid():N}");
        try
        {
            var store = new PdfMacroStore(root);
            store.Save(new PdfMacro("Sharing copy", [
                new PdfMacroStep(PdfMacroOperation.Optimize),
                new PdfMacroStep(PdfMacroOperation.Save)]));

            PdfMacro loaded = Assert.Single(store.LoadAll());
            Assert.Equal("Sharing copy", loaded.Name);
            Assert.Equal([PdfMacroOperation.Optimize, PdfMacroOperation.Save],
                loaded.Steps.Select(step => step.Operation));
            store.Remove(loaded.Name);
            Assert.Empty(store.LoadAll());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
