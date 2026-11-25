using System.IO;
using System.Security.Cryptography;

namespace KillerPDF.Services
{
    /// <summary>
    /// Puts known bytes at a path and returns a read handle that keeps any other process from writing,
    /// renaming or deleting the file until it is disposed. A file checked by hash and then loaded later
    /// could otherwise be swapped in between.
    /// </summary>
    internal static class VerifiedFileHold
    {
        /// <summary>
        /// Leaves <paramref name="targetPath"/> alone when it already holds the bytes of
        /// <paramref name="expected"/>, otherwise replaces it, and returns the hold on the verified file.
        /// </summary>
        internal static FileStream Open(Stream expected, string targetPath)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
            byte[] expectedHash = SHA256.HashData(expected);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                FileStream? held = TryHold(targetPath);
                if (held is not null)
                {
                    // The hash is taken through the hold itself, so the checked bytes are the held bytes.
                    if (CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(held)))
                    {
                        held.Position = 0;
                        return held;
                    }
                    held.Dispose();
                }
                expected.Position = 0;
                Replace(expected, targetPath);
            }
            throw new IOException($"'{Path.GetFileName(targetPath)}' could not be written and verified.");
        }

        // Read access with read-only sharing: the loader can still open the file to map it, while a write,
        // rename or delete by anyone else fails with a sharing violation.
        private static FileStream? TryHold(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void Replace(Stream source, string targetPath)
        {
            string tmp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(dst);
                File.Move(tmp, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
