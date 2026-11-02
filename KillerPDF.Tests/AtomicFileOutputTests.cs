using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AtomicFileOutputTests
{
    [Fact]
    public async Task ScopedFileOperationsFlowToAWorkerWithoutLeakingAfterDisposal()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        using var cancellation = new CancellationTokenSource();
        var files = new AtomicFileOutput.FileOperations
        {
            FlushToDisk = stream => { stream.Flush(flushToDisk: true); cancellation.Cancel(); }
        };
        try
        {
            using (AtomicFileOutput.UseFileOperations(files))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                    AtomicFileOutput.Write(destination, "Canceled replacement"u8.ToArray(), cancellation.Token)));
            Assert.Empty(Directory.GetFiles(directory));
            AtomicFileOutput.Write(destination, "Complete replacement"u8.ToArray());
            Assert.Equal("Complete replacement", File.ReadAllText(destination));
            Assert.Equal(new[] { destination }, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforePublicationLeavesDestinationUntouched(bool existing)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        if (existing) File.WriteAllText(destination, "Original");
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => AtomicFileOutput.Write(destination, stream =>
            {
                stream.Write("Canceled replacement"u8);
                cancellation.Cancel();
            }, cancellation.Token));
            Assert.Equal(existing, File.Exists(destination));
            if (existing) Assert.Equal("Original", File.ReadAllText(destination));
            Assert.Equal(existing ? new[] { destination } : Array.Empty<string>(), Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    // A forced disk flush costs about 1.5 ms per file, which made a 15,000-file batch resave
    // 19% slower than 1.8.91. The flush only protects an older copy of the destination, so a
    // bulk write skips it for a file that does not exist yet and keeps it for a replacement.
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void DiskFlushProtectsOnlyAnExistingDestination(bool bulk, bool existing, int expectedFlushes)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        if (existing) File.WriteAllText(path, "Previous");
        byte[] replacement = "Complete replacement"u8.ToArray();
        int flushes = 0;
        var files = new AtomicFileOutput.FileOperations { FlushToDisk = stream => { flushes++; stream.Flush(flushToDisk: true); } };
        try
        {
            if (bulk) AtomicFileOutput.WriteBulk(path, replacement, files);
            else AtomicFileOutput.Write(path, stream => stream.Write(replacement), default, files);
            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
            Assert.Equal(expectedFlushes, flushes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedWritePublishesAllBytes(bool asynchronous, bool existing)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        if (existing) File.WriteAllText(path, "Previous");
        byte[] replacement = "Complete replacement"u8.ToArray();
        try
        {
            if (asynchronous) await AtomicFileOutput.WriteAsync(path, replacement);
            else AtomicFileOutput.Write(path, replacement);
            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public void LockedDestinationSurvivesPublicationFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Previous"u8.ToArray();
        File.WriteAllBytes(path, original);
        try
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => AtomicFileOutput.Write(path, "Replacement"u8.ToArray()));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteFailurePreservesDestinationAndCleansStaging(bool asynchronous)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Existing destination"u8.ToArray();
        File.WriteAllBytes(path, original);
        try
        {
            if (asynchronous)
                await Assert.ThrowsAsync<IOException>(() => AtomicFileOutput.WriteAsync(path, async stream =>
                {
                    await stream.WriteAsync("Partial replacement"u8.ToArray());
                    throw new IOException("Injected failure after write.");
                }));
            else
                Assert.Throws<IOException>(() => AtomicFileOutput.Write(path, stream =>
                {
                    stream.Write("Partial replacement"u8);
                    throw new IOException("Injected failure after write.");
                }));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task CancellationAfterAsyncWritePreservesDestination()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Existing destination"u8.ToArray();
        File.WriteAllBytes(path, original);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFileOutput.WriteAsync(path, async stream =>
            {
                await stream.WriteAsync("Partial replacement"u8.ToArray());
                cancellation.Cancel();
            }, cancellation.Token));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
    [Theory]
    [InlineData(false, 1176)]
    [InlineData(false, 1177)]
    [InlineData(true, 1176)]
    [InlineData(true, 1177)]
    public async Task PartialReplacementRetainsCompleteRecoveryFilesWhenRestorationFails(bool asynchronous, int nativeError)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        byte[] original = "Original document"u8.ToArray();
        byte[] replacement = "Complete edited document"u8.ToArray();
        File.WriteAllBytes(destination, original);
        string? staged = null, saved = null;
        var files = new AtomicFileOutput.FileOperations
        {
            Replace = (incoming, target, backup) =>
            {
                staged = incoming;
                Assert.Equal(replacement, File.ReadAllBytes(incoming));
                if (nativeError == 1177) { saved = backup ?? target + ".native-backup"; File.Move(target, saved); }
                else File.Delete(target);
                throw new IOException("Replacement failed after changing the destination name.", unchecked((int)(0x80070000u + nativeError)));
            },
            Move = (source, target) => throw new IOException("Publication and restoration are unavailable.")
        };
        try
        {
            Exception? error = asynchronous
                ? await Record.ExceptionAsync(() => AtomicFileOutput.WriteAsync(destination, stream => stream.WriteAsync(replacement).AsTask(), default, files))
                : Record.Exception(() => AtomicFileOutput.Write(destination, stream => stream.Write(replacement), default, files));
            Assert.IsAssignableFrom<IOException>(error);
            Assert.NotNull(staged);
            Assert.True(File.Exists(staged), "The only complete incoming document must remain recoverable.");
            Assert.Equal(replacement, File.ReadAllBytes(staged!));
            Assert.Contains(staged!, error!.Message);
            if (nativeError == 1177) Assert.Equal(original, File.ReadAllBytes(saved!));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    public void FailedRenameRestoresACompleteImage(int nativeError)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        File.WriteAllText(destination, "Original");
        string? staged = null;
        var files = new AtomicFileOutput.FileOperations
        {
            Replace = (source, target, backup) =>
            {
                staged = source;
                if (nativeError == 1177) File.Move(target, backup ?? target + ".native-backup");
                else File.Delete(target);
                throw new IOException("Partial replacement.", unchecked((int)(0x80070000u + nativeError)));
            },
            Move = (source, target) => { if (source == staged) throw new IOException("Incoming rename failed."); File.Move(source, target); }
        };
        try
        {
            Exception? error = Record.Exception(() => AtomicFileOutput.Write(destination, stream => stream.Write("Replacement"u8), default, files));
            Assert.True(File.Exists(destination));
            Assert.Equal(nativeError == 1177 ? "Original" : "Replacement", File.ReadAllText(destination));
            if (nativeError == 1177) { Assert.IsAssignableFrom<IOException>(error); Assert.Equal("Replacement", File.ReadAllText(staged!)); }
            else Assert.Null(error);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CleanupFailureAfterPublicationDoesNotReportFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        File.WriteAllText(destination, "Original");
        var files = new AtomicFileOutput.FileOperations { Delete = path => throw new IOException("Cleanup is unavailable.") };
        try
        {
            AtomicFileOutput.Write(destination, stream => stream.Write("Replacement"u8), default, files);
            Assert.Equal("Replacement", File.ReadAllText(destination));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CancellationAfterPartialReplacementRestoresTheOriginal()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        File.WriteAllText(destination, "Original");
        using var cancellation = new CancellationTokenSource();
        var files = new AtomicFileOutput.FileOperations
        {
            Replace = (source, target, backup) =>
            {
                File.Move(target, backup ?? target + ".native-backup");
                cancellation.Cancel();
                throw new IOException("Partial replacement.", unchecked((int)0x80070499));
            }
        };
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => AtomicFileOutput.Write(destination, stream => stream.Write("Replacement"u8), cancellation.Token, files));
            Assert.Equal("Original", File.ReadAllText(destination));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedReplacementUsesAProtectedRenameFallback(bool failIncomingRename)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "document.pdf");
        File.WriteAllText(destination, "Original");
        string? staged = null;
        var files = new AtomicFileOutput.FileOperations
        {
            Replace = (source, target, backup) => { staged = source; throw new PlatformNotSupportedException("Replacement is unsupported."); },
            Move = (source, target) => { if (failIncomingRename && source == staged) throw new IOException("Incoming rename failed."); File.Move(source, target); }
        };
        try
        {
            Exception? error = Record.Exception(() => AtomicFileOutput.Write(destination, stream => stream.Write("Replacement"u8), default, files));
            Assert.Equal(failIncomingRename ? "Original" : "Replacement", File.ReadAllText(destination));
            if (failIncomingRename) { Assert.IsAssignableFrom<IOException>(error); Assert.Equal("Replacement", File.ReadAllText(staged!)); }
            else { Assert.Null(error); Assert.Equal(new[] { destination }, Directory.GetFiles(directory)); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
