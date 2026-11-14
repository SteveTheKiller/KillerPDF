using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// 1.9.3 opens EPUB and DjVu books, but the Open dialog offered only "PDF files", so a book could not be picked there.
public sealed class OpenDialogFilterTests
{
    private static string Loc(string key) => key;

    [Fact]
    public void TheFirstChoiceOpensPdfsAndBooksTogether()
    {
        string[] parts = OpenDialogFilter.For(Loc).Split('|');
        Assert.Equal("Str_Filter_Openable", parts[0]);
        Assert.Equal(new[] { "*.pdf", "*.epub", "*.djvu", "*.djv" }, parts[1].Split(';'));
    }

    [Fact]
    public void EachFormatCanAlsoBeChosenOnItsOwn()
    {
        string[] parts = OpenDialogFilter.For(Loc).Split('|');
        Assert.Equal(8, parts.Length);
        Assert.Equal(("Str_Filter_Pdf", "*.pdf"), (parts[2], parts[3]));
        Assert.Equal(("Str_Filter_Epub", "*.epub"), (parts[4], parts[5]));
        Assert.Equal(("Str_Filter_Djvu", "*.djvu;*.djv"), (parts[6], parts[7]));
    }
}
