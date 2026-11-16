using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #495: the Merge dialog takes files and folders, by button or by dropping them from File Explorer, and its order can
// be changed before merging. A folder adds the PDFs directly inside it in name order; anything that is not a PDF is
// left out, and a file already in the list is not added twice.
public sealed class MergeFileListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merge-list-" + Guid.NewGuid().ToString("N"));

    public MergeFileListTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        string path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void PdfFilesAreAddedInTheOrderGivenAndOthersAreLeftOut()
    {
        string b = Touch("b.pdf"), a = Touch("a.PDF"), text = Touch("notes.txt");
        var list = new MergeFileList();

        int added = list.Add([b, text, a]);

        Assert.Equal(2, added);
        Assert.Equal([b, a], list.Paths);
    }

    [Fact]
    public void AFolderAddsThePdfsDirectlyInsideItInNameOrder()
    {
        string two = Touch("box", "2 second.pdf"), one = Touch("box", "1 first.pdf");
        Touch("box", "inner", "deeper.pdf");
        Touch("box", "photo.jpg");
        var list = new MergeFileList();

        list.Add([Path.Combine(_root, "box")]);

        Assert.Equal([one, two], list.Paths);
    }

    [Fact]
    public void AFileAlreadyInTheListIsNotAddedTwice()
    {
        string a = Touch("a.pdf");
        var list = new MergeFileList();
        list.Add([a]);

        int added = list.Add([a.ToUpperInvariant(), a]);

        Assert.Equal(0, added);
        Assert.Single(list.Paths);
    }

    [Fact]
    public void FilesCanBeMovedAndRemoved()
    {
        string a = Touch("a.pdf"), b = Touch("b.pdf"), c = Touch("c.pdf");
        var list = new MergeFileList();
        list.Add([a, b, c]);

        list.Move(2, 0);
        Assert.Equal([c, a, b], list.Paths);
        list.RemoveAt(1);
        Assert.Equal([c, b], list.Paths);
        list.Move(0, 5);   // past the end lands last
        Assert.Equal([b, c], list.Paths);
    }

    [Fact]
    public void AMissingPathAddsNothing() =>
        Assert.Equal(0, new MergeFileList().Add([Path.Combine(_root, "gone.pdf"), Path.Combine(_root, "no folder")]));
}
