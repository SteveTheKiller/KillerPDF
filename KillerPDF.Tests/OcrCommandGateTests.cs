using KillerPDF.Features;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Each OCR command on the open document asks the gate for what it writes before it does anything
// else. Text written to a file is a text export, while the clipboard commands stay free.
public sealed class OcrCommandGateTests
{
    // R4-002: Extract All Text writes a .txt or .md file, so it answers as data, like Convert Scan to Text.
    [Fact]
    public void ExtractAllTextAsksForADataExportAndStopsWhenRefused()
    {
        var asked = new List<PdfOutput>();

        bool started = OcrCommandGate.MayStart(OcrCommand.ExtractAllText, output => { asked.Add(output); return false; });

        Assert.False(started);
        Assert.Equal([PdfOutput.Data], asked);
    }

    [Theory]
    [InlineData("SearchablePdf", "Printable")]
    [InlineData("ScanToText", "Data")]
    [InlineData("ExtractAllText", "Data")]
    public void AFileWritingCommandStartsOnceTheGateAllowsIt(string command, string expected)
    {
        var asked = new List<PdfOutput>();

        Assert.True(OcrCommandGate.MayStart(Enum.Parse<OcrCommand>(command), output => { asked.Add(output); return true; }));
        Assert.Equal([Enum.Parse<PdfOutput>(expected)], asked);
    }

    // The accepted line: OCR copy to the clipboard is not an output, so nothing is asked.
    [Theory]
    [InlineData("PageToClipboard")]
    [InlineData("RegionToClipboard")]
    public void ClipboardCommandsAskNothing(string command) =>
        Assert.True(OcrCommandGate.MayStart(Enum.Parse<OcrCommand>(command),
            _ => throw new InvalidOperationException("The clipboard was gated.")));
}
