using System.IO;
using System.IO.Compression;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DropImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kp-drop-" + Guid.NewGuid().ToString("N"));
    public DropImportTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private string Archive(params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open());
            writer.Write(entry.Text);
        }
        return path;
    }

    [Fact]
    public void ArchiveStagesOnlySupportedFilesInStableOrderAndDisposesUnadoptedBytes()
    {
        string archive = Archive(("z.pdf", "PDF-Z"), ("nested/a.png", "PNG-A"), ("irrelevant.txt", "do not extract"));
        byte[] original = File.ReadAllBytes(archive);
        string owned = Path.Combine(root, "owned");
        using (var batch = DropImport.Prepare([archive], owned))
        {
            Assert.True(batch.Expanded);
            Assert.Equal(new[] { "a.png", "z.pdf" }, batch.Files.Select(file => file.SuggestedName));
            Assert.All(batch.Files, file => Assert.True(file.Temporary));
            Assert.Equal(new[] { "PNG-A", "PDF-Z" }, batch.Files.Select(file => File.ReadAllText(file.Path)));
            Assert.Equal(2, Directory.GetFiles(owned).Length);
        }
        Assert.False(Directory.Exists(owned));
        Assert.Equal(original, File.ReadAllBytes(archive));
    }

    [Theory]
    [InlineData("pages.pdf")]
    [InlineData("images.png")]
    public void SupportedExtensionOnDirectoryNeverCreatesAnEmptyDocument(string directory)
    {
        string archive = Archive((directory + "/", ""), (directory + "/a.pdf", "real PDF bytes"));
        using var batch = DropImport.Prepare([archive], Path.Combine(root, "owned"));
        var file = Assert.Single(batch.Files);
        Assert.Equal("a.pdf", file.SuggestedName);
        Assert.Equal("real PDF bytes", File.ReadAllText(file.Path));
    }

    [Theory]
    [InlineData(DropImportFailure.EntryLimit)]
    [InlineData(DropImportFailure.DepthLimit)]
    [InlineData(DropImportFailure.FileLimit)]
    [InlineData(DropImportFailure.TotalLimit)]
    internal void ExceedingAnyArchiveBudgetRefusesTheWholeBatchAndRemovesStagedBytes(DropImportFailure failure)
    {
        string archive = Archive(("a.pdf", "12"), ("nested/b.pdf", "345"));
        var limits = new DropImportLimits(Entries: 2, Depth: 1, FileBytes: 3, TotalBytes: 5);
        limits = failure switch
        {
            DropImportFailure.EntryLimit => limits with { Entries = 1 },
            DropImportFailure.DepthLimit => limits with { Depth = 0 },
            DropImportFailure.FileLimit => limits with { FileBytes = 1 },
            _ => limits with { TotalBytes = 3 }
        };
        string owned = Path.Combine(root, "owned");
        Assert.Equal(failure, Assert.Throws<DropImportException>(() => DropImport.Prepare([archive], owned, limits)).Failure);
        Assert.False(Directory.Exists(owned));
    }

    [Theory]
    [InlineData("../escape.pdf")]
    [InlineData("/root.pdf")]
    [InlineData("C:/escape.pdf")]
    [InlineData("dir\\..\\escape.pdf")]
    [InlineData("safe.pdf:stream")]
    [InlineData("../ignored.txt")]
    public void UnsafeArchivePathsAreRefusedEvenForUnsupportedEntries(string name)
    {
        string archive = Archive(("a.pdf", "staged"), (name, "unsafe"));
        string owned = Path.Combine(root, "owned");
        Assert.Equal(DropImportFailure.UnsafePath,
            Assert.Throws<DropImportException>(() => DropImport.Prepare([archive], owned)).Failure);
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void ArchiveLinksAreRefusedWithoutCreatingTheirTargets()
    {
        string archive = Archive(("shortcut.pdf", "outside.pdf"));
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            zip.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        string owned = Path.Combine(root, "owned");
        Assert.Equal(DropImportFailure.Link,
            Assert.Throws<DropImportException>(() => DropImport.Prepare([archive], owned)).Failure);
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void FolderDiscoveryIsBoundedBeforeSortingAndKeepsNormalFileIdentity()
    {
        string folder = Path.Combine(root, "source");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "z.pdf"), "Z");
        File.WriteAllText(Path.Combine(folder, "a.pdf"), "A");
        File.WriteAllText(Path.Combine(folder, "ignored.txt"), "count me too");
        using (var batch = DropImport.Prepare([folder], Path.Combine(root, "ok"), new DropImportLimits(Entries: 3)))
        {
            Assert.True(batch.Expanded);
            Assert.Equal(new[] { "a.pdf", "z.pdf" }, batch.Files.Select(file => file.SuggestedName));
            Assert.All(batch.Files, file => Assert.False(file.Temporary));
        }
        string owned = Path.Combine(root, "over");
        Assert.Equal(DropImportFailure.EntryLimit,
            Assert.Throws<DropImportException>(() => DropImport.Prepare([folder], owned, new DropImportLimits(Entries: 2))).Failure);
        Assert.False(Directory.Exists(owned));
        Assert.Equal("A", File.ReadAllText(Path.Combine(folder, "a.pdf")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationRefusesTheBatchAndCleansAnyEarlierExtraction(bool duringBatch)
    {
        using var cancellation = new CancellationTokenSource();
        string first = Archive(("a.pdf", "first bytes"));
        string second = Archive(("b.pdf", "second bytes"));
        if (!duringBatch) cancellation.Cancel();
        IReadOnlyList<string> paths = duringBatch ? new CancelingInputs(first, second, cancellation) : [first];
        string owned = Path.Combine(root, "owned");
        Assert.ThrowsAny<OperationCanceledException>(() => DropImport.Prepare(paths, owned, cancellation: cancellation.Token));
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void ExactBudgetsAndAdoptionKeepBytesAvailableUntilTheOwnerCleansThem()
    {
        string archive = Archive(("a.pdf", "12"), ("nested/b.pdf", "345"));
        string owned = Path.Combine(root, "owned");
        var batch = DropImport.Prepare([archive], owned, new DropImportLimits(Entries: 2, Depth: 1, FileBytes: 3, TotalBytes: 5));
        string[] extracted = batch.Files.Select(file => file.Path).ToArray();
        batch.KeepFiles();
        batch.Dispose();
        Assert.Equal(new[] { "12", "345" }, extracted.Select(File.ReadAllText));
    }

    [Theory]
    [InlineData("entry-count")]
    [InlineData("duplicate")]
    [InlineData("depth")]
    public void IrrelevantAndDuplicateArchiveEntriesCannotBypassValidation(string kind)
    {
        string archive = kind switch
        {
            "duplicate" => Archive(("a.pdf", "first"), ("A.PDF", "second")),
            "depth" => Archive(("deep/ignored.txt", "ignored")),
            _ => Archive(("one.txt", "ignored"), ("two.txt", "ignored"))
        };
        var limits = new DropImportLimits(Entries: 1, Depth: 0);
        if (kind == "duplicate") limits = limits with { Entries = 2 };
        string owned = Path.Combine(root, "owned");
        Assert.Throws<DropImportException>(() => DropImport.Prepare([archive], owned, limits));
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void ActualDecodedBytesAreBoundedWhenArchiveMetadataUnderstatesTheLength()
    {
        string archive = Archive(("large.pdf", "0123456789"));
        byte[] bytes = File.ReadAllBytes(archive);
        int header = -1;
        for (int index = 0; index < bytes.Length - 28; index++)
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4b && bytes[index + 2] == 1 && bytes[index + 3] == 2)
            { header = index; break; }
        Assert.True(header >= 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 24, 4), 1);
        File.WriteAllBytes(archive, bytes);
        string owned = Path.Combine(root, "owned");
        Assert.Throws<InvalidDataException>(() => DropImport.Prepare([archive], owned, new DropImportLimits(FileBytes: 4)));
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void FailedArchiveAndExistingStorageNeverDeleteUnrelatedBytes()
    {
        string archive = Path.Combine(root, "broken.zip");
        File.WriteAllText(archive, "not a ZIP");
        string owned = Path.Combine(root, "owned");
        Assert.Throws<InvalidDataException>(() => DropImport.Prepare([archive], owned));
        Assert.False(Directory.Exists(owned));
        Directory.CreateDirectory(owned);
        string sentinel = Path.Combine(owned, "keep.txt");
        File.WriteAllText(sentinel, "unrelated");
        Assert.ThrowsAny<IOException>(() => DropImport.Prepare([archive], owned));
        Assert.Equal("unrelated", File.ReadAllText(sentinel));
    }

    [Fact]
    public void FolderDepthIsCheckedBeforeDescendingIntoTheNextDirectory()
    {
        string folder = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(folder, "child"));
        File.WriteAllText(Path.Combine(folder, "child", "a.pdf"), "A");
        string owned = Path.Combine(root, "owned");
        Assert.Equal(DropImportFailure.DepthLimit,
            Assert.Throws<DropImportException>(() => DropImport.Prepare([folder], owned, new DropImportLimits(Depth: 0))).Failure);
        Assert.False(Directory.Exists(owned));
    }

    [Fact]
    public void BusyTemporaryFileDefersCleanupWithoutCrashingOrLosingCancellation()
    {
        string archive = Archive(("a.pdf", "private import bytes"));
        string owned = Path.Combine(root, "owned");
        var batch = DropImport.Prepare([archive], owned);
        string staged = Assert.Single(batch.Files).Path;
        using (var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(Record.Exception(batch.Dispose));
        Assert.True(File.Exists(staged));
        batch.Dispose();
        Assert.False(Directory.Exists(owned));

        using var cancellation = new CancellationTokenSource();
        FileStream? locked = null;
        var paths = new CancelingInputs(archive, archive, cancellation, () =>
            locked = new FileStream(Directory.GetFiles(owned).Single(), FileMode.Open, FileAccess.Read, FileShare.None));
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => DropImport.Prepare(paths, owned, cancellation: cancellation.Token));
        }
        finally { locked?.Dispose(); }
        Assert.True(Directory.Exists(owned));
    }

    [Fact]
    public void AdoptingOneFileRemovesUnselectedAndFailedImportBytes()
    {
        string archive = Archive(("a.pdf", "keep"), ("b.pdf", "discard"), ("c.png", "converted image"));
        string owned = Path.Combine(root, "owned");
        var batch = DropImport.Prepare([archive], owned);
        string adopted = batch.Files[0].Path;
        batch.KeepFiles([adopted]);
        batch.Dispose();
        Assert.Equal("keep", File.ReadAllText(adopted));
        Assert.Equal(new[] { adopted }, Directory.GetFiles(owned));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedDirectoryIsRefusedBeforeReadingItsWholeMetadata(bool underreported)
    {
        string path = Path.Combine(root, "many.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (int index = 0; index < 5000; index++) zip.CreateEntry(index + ".txt");
        if (underreported)
        {
            byte[] bytes = File.ReadAllBytes(path);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 14), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), 1);
            File.WriteAllBytes(path, bytes);
        }
        var input = new ReadProbe(File.OpenRead(path));
        Assert.Equal(DropImportFailure.EntryLimit, Assert.Throws<DropImportException>(() =>
            DropImport.Prepare([path], Path.Combine(root, "owned"), new DropImportLimits(Entries: 1),
                openArchive: _ => input)).Failure);
        Assert.InRange(input.BytesRead, 0, 70_000);
        Assert.False(Directory.Exists(Path.Combine(root, "owned")));
    }

    [Fact]
    public void SmallZip64ArchiveKeepsSupportedContentAndHonorsItsRealCount()
    {
        string path = Archive(("a.pdf", "ZIP64 PDF bytes"));
        byte[] bytes = File.ReadAllBytes(path);
        byte[] footer = bytes[^22..];
        uint directorySize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
        uint directoryOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
        using (var output = new MemoryStream())
        {
            output.Write(bytes, 0, bytes.Length - 22);
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            writer.Write(0x06064b50u); writer.Write(44ul); writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0u); writer.Write(0u); writer.Write(1ul); writer.Write(1ul);
            writer.Write((ulong)directorySize); writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)(bytes.Length - 22)); writer.Write(1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(footer.AsSpan(8), ushort.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(footer.AsSpan(10), ushort.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12), uint.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), uint.MaxValue);
            output.Write(footer);
            File.WriteAllBytes(path, output.ToArray());
        }
        using (var known = ZipFile.OpenRead(path)) Assert.Single(known.Entries);
        using var batch = DropImport.Prepare([path], Path.Combine(root, "owned"), new DropImportLimits(Entries: 1));
        Assert.Equal("ZIP64 PDF bytes", File.ReadAllText(Assert.Single(batch.Files).Path));
    }

    [Fact]
    public void CentralDirectoryDigitalSignatureDoesNotBecomeAFileOrRejectItsPdf()
    {
        string archive = Archive(("a.pdf", "signed directory PDF"));
        byte[] bytes = File.ReadAllBytes(archive);
        byte[] footer = bytes[^22..];
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
        using (var output = new MemoryStream())
        {
            output.Write(bytes, 0, bytes.Length - 22);
            output.Write(new byte[] { 0x50, 0x4b, 0x05, 0x05, 3, 0, 1, 2, 3 });
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12), size + 9);
            output.Write(footer);
            File.WriteAllBytes(archive, output.ToArray());
        }
        using (var reference = ZipFile.OpenRead(archive)) Assert.Single(reference.Entries);
        using var batch = DropImport.Prepare([archive], Path.Combine(root, "owned"), new DropImportLimits(Entries: 1));
        Assert.Equal("signed directory PDF", File.ReadAllText(Assert.Single(batch.Files).Path));
    }

    [Fact]
    public void NestedFooterCannotMakeTheDecoderReadAnUnadmittedDirectory()
    {
        string archive = Path.Combine(root, "nested-footer.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            for (int index = 0; index < 5000; index++) zip.CreateEntry(index + ".txt");
        byte[] bytes = File.ReadAllBytes(archive);
        byte[] originalFooter = bytes[^22..];
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalFooter.AsSpan(12));
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalFooter.AsSpan(16));
        using (var output = new MemoryStream())
        {
            output.Write(bytes, 0, bytes.Length - 22);
            ulong zip64Position = (ulong)output.Position;
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            writer.Write(0x06064b50u); writer.Write(44ul); writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0u); writer.Write(0u); writer.Write(5000ul); writer.Write(5000ul);
            writer.Write((ulong)size); writer.Write((ulong)offset);
            byte[] outer = new byte[22];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outer, 0x06054b50);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(outer.AsSpan(16), (uint)output.Position);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(outer.AsSpan(20), 43);
            output.Write(outer);
            writer.Write(0x07064b50u); writer.Write(0u); writer.Write(zip64Position); writer.Write(1u);
            byte[] inner = new byte[22];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(inner, 0x06054b50);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(8), ushort.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(10), ushort.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(12), uint.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(inner.AsSpan(16), uint.MaxValue);
            output.Write(inner);
            output.WriteByte(0);
            File.WriteAllBytes(archive, output.ToArray());
        }
        var input = new ReadProbe(File.OpenRead(archive));
        string owned = Path.Combine(root, "owned");
        Exception? failure = Record.Exception(() => DropImport.Prepare([archive], owned,
            new DropImportLimits(Entries: 1), openArchive: _ => input));
        Assert.True(failure is InvalidDataException or DropImportException);
        Assert.InRange(input.BytesRead, 0, 70_000);
        Assert.False(Directory.Exists(owned));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationWhileInspectingDirectoryStopsBeforeExtraction(int readToCancel)
    {
        string archive = Archive(("a.pdf", "A"), ("b.pdf", "B"), ("c.pdf", "C"));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var probe = new ReadProbe(File.OpenRead(archive), () => { if (++calls == readToCancel) cancellation.Cancel(); });
        string owned = Path.Combine(root, "owned");
        Assert.ThrowsAny<OperationCanceledException>(() => DropImport.Prepare([archive], owned,
            cancellation: cancellation.Token, openArchive: _ => probe));
        Assert.False(Directory.Exists(owned));
        Assert.Equal(readToCancel, calls);
    }

    [Theory]
    [InlineData("extent")]
    [InlineData("offset")]
    [InlineData("record")]
    [InlineData("variable")]
    public void MalformedDirectoryIsRefusedWithoutReturningAnyDocuments(string kind)
    {
        string archive = Archive(("a.pdf", "A"));
        byte[] bytes = File.ReadAllBytes(archive);
        int end = bytes.Length - 22;
        int start = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        if (kind == "extent") System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), 1);
        else if (kind == "offset") System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue - 1);
        else if (kind == "record") bytes[start] = 0;
        else System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(start + 28), ushort.MaxValue);
        File.WriteAllBytes(archive, bytes);
        string owned = Path.Combine(root, "owned");
        Assert.Throws<InvalidDataException>(() => DropImport.Prepare([archive], owned));
        Assert.False(Directory.Exists(owned));
    }

    private sealed class ReadProbe(Stream input, Action? afterRead = null) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        { int read = input.Read(buffer, offset, count); BytesRead += read; afterRead?.Invoke(); return read; }
        public override int Read(Span<byte> buffer)
        { int read = input.Read(buffer); BytesRead += read; afterRead?.Invoke(); return read; }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CancelingInputs(string first, string second, CancellationTokenSource cancellation,
        Action? beforeCancel = null) : IReadOnlyList<string>
    {
        public int Count => 2;
        public string this[int index] => index == 0 ? first : second;
        public IEnumerator<string> GetEnumerator()
        {
            yield return first;
            beforeCancel?.Invoke();
            cancellation.Cancel();
            yield return second;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
