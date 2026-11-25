using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The OCR DLLs live in a per-user cache. Between the hash check and LoadLibraryEx, another process of the
// same user must not be able to swap the file (KP-SEC-02), so the check hands back a handle that blocks it.
public sealed class VerifiedFileHoldTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kp-verified-hold-" + Guid.NewGuid().ToString("N"));

    public VerifiedFileHoldTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AFileThatDoesNotMatchIsReplacedAndThenCannotBeChangedOrDeletedWhileHeld()
    {
        string path = Path.Combine(_folder, "native.dll");
        File.WriteAllBytes(path, [9, 9, 9]);
        byte[] expected = [1, 2, 3, 4];

        using (FileStream held = VerifiedFileHold.Open(new MemoryStream(expected), path))
        {
            Assert.Equal(expected, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [6, 6, 6]));
            Assert.ThrowsAny<Exception>(() => File.Delete(path));
            Assert.ThrowsAny<Exception>(() => File.Move(path, path + ".moved"));
        }

        File.WriteAllBytes(path, [6]); // released once the hold is disposed
    }

    [Fact]
    public void AMatchingFileIsHeldWithoutBeingRewritten()
    {
        string path = Path.Combine(_folder, "native.dll");
        byte[] expected = [1, 2, 3, 4];
        File.WriteAllBytes(path, expected);
        DateTime written = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        using FileStream held = VerifiedFileHold.Open(new MemoryStream(expected), path);

        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, [6, 6, 6]));
    }
}
