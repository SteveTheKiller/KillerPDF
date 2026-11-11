using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// SOURCE SCAN: the window does not compile in this project, so the shell is read as text. A saved copy written to a
// temporary file burns the pictures on the page, and one it cannot read is left out. The count has to reach the
// status of whatever output the copy feeds. Window check on SN02: 1. Place a picture copied from a PDF that cannot be
// read. 2. Run Extract Pages, Sign, Export as a document, Export as images, a macro, Data Merge, a production layout
// and a guided task. 3. Each result message ends with the skipped-picture note.
public sealed class SavedCopyConsumerTests
{
    private static string ShellFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Shell");
    }

    private static IEnumerable<string> ShellFiles() => Directory.EnumerateFiles(ShellFolder(), "*.cs");

    // A call of the copy writer or of the snapshot helper that wraps it, not its declaration.
    private static readonly Regex CallSite = new(
        @"(?<!SavedCopy )(?<!private )\b(WriteSavedCopyToTemp|CreateOutputSnapshot)\(", RegexOptions.Compiled);

    [Fact]
    public void TheCopyWriterReturnsTheCountWithThePath()
    {
        string reduce = File.ReadAllText(Path.Combine(ShellFolder(), "ReduceSize.cs"));
        Assert.Contains("private SavedCopy WriteSavedCopyToTemp(", reduce);
        Assert.DoesNotContain("_lastCopySkippedPictures", reduce);
        Assert.DoesNotContain("_lastCopySkippedPictures", File.ReadAllText(Path.Combine(ShellFolder(), "FileOperations.cs")));
        Assert.Contains("private SavedCopy CreateOutputSnapshot()", File.ReadAllText(Path.Combine(ShellFolder(), "DataExchange.cs")));
    }

    // Every call site takes the result as a SavedCopy and then reads its count (.Note or .SkippedPictures). The two
    // exceptions are named: CreateOutputSnapshot passes the result on to its own caller, and the OCR host reads the
    // copy only as recognition input.
    [Fact]
    public void EveryCallerOfTheCopyWriterConsumesTheSkippedCount()
    {
        var problems = new List<string>();
        foreach (string file in ShellFiles())
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            int sites = CallSite.Matches(text).Count;
            if (sites == 0) continue;

            var consumers = Regex.Matches(text, @"SavedCopy\s+(\w+)\s*=\s*(?:await\s+BusyCue\.ThenRun\(|WriteSavedCopyToTemp\(|CreateOutputSnapshot\(|window\.WriteSavedCopyToTemp\()")
                .Select(match => match.Groups[1].Value).ToList();
            int passedOn = name == "DataExchange.cs" ? Regex.Matches(text, @"return WriteSavedCopyToTemp\(").Count : 0;
            int recognitionInput = name == "Ocr.cs" ? Regex.Matches(text, @"WriteSavedCopyToTemp\([^)]*\)\.Path").Count : 0;
            // A ThenRun call hands the writer over as a lambda, so its own call site is counted once more.
            int lambdas = Regex.Matches(text, @"\(\)\s*=>\s*WriteSavedCopyToTemp\(").Count;
            int handled = consumers.Count + passedOn + recognitionInput;
            // Declarations with a lambda writer count once as a consumer (the ThenRun result), once as a call site.
            if (sites != handled)
                problems.Add($"{name}: {sites} call sites, {handled} consumed ({consumers.Count} SavedCopy results, {passedOn} passed on, {recognitionInput} recognition input, {lambdas} lambdas)");
            foreach (string variable in consumers)
                if (!Regex.IsMatch(text, $@"\b{variable}\.(Note\(|SkippedPictures)"))
                    problems.Add($"{name}: {variable} is never read for its skipped-picture count");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void ASavedCopyCarriesItsCountToTheStatus()
    {
        var left = new KillerPDF.Services.SavedCopy(@"C:\temp\copy.pdf", 2);
        Assert.Equal("Saved. Left out: 2.", left.Note("Saved.", "Left out: {0}."));
        Assert.Equal("Saved.", new KillerPDF.Services.SavedCopy(@"C:\temp\copy.pdf", 0).Note("Saved.", "Left out: {0}."));
    }

    // The note text comes from the one localized string, appended through the shared helper.
    [Theory]
    [InlineData("PageOperations.cs", "extractCopy.Note(")]
    [InlineData("FileOperations.cs", "exportCopy.Note(")]
    [InlineData("FileOperations.cs", "imagesCopy.Note(")]
    [InlineData("Macros.cs", "macroCopy.Note(")]
    [InlineData("ProductionTools.cs", "layoutCopy.Note(")]
    [InlineData("DocumentTools.cs", "PicturesSkippedNote.Append(string.Format(Loc(\"Str_DataMerge_Complete\")")]
    [InlineData("MacroDataMerge.cs", "PicturesSkippedNote.Append(report.ToString()")]
    [InlineData("GuidedTasks.cs", "guidedCopy.SkippedPictures")]
    [InlineData("DirtyTracking.cs", "signCopy.SkippedPictures")]
    public void EachOutputAppendsTheNoteToItsOwnSuccessStatus(string file, string expected) =>
        Assert.Contains(expected, File.ReadAllText(Path.Combine(ShellFolder(), file)));
}
