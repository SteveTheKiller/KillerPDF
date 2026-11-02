using System.IO;
using System.IO.Compression;

namespace KillerPDF.Services;

internal sealed record DropImportLimits(int Entries = 10_000, int Depth = 64,
    long FileBytes = 1L << 30, long TotalBytes = 2L << 30);

internal sealed record DropImportFile(string Path, string SuggestedName, bool Temporary);

internal enum DropImportFailure { EntryLimit, DepthLimit, FileLimit, TotalLimit, UnsafePath, Link }
internal sealed class DropImportException(DropImportFailure failure) : IOException("Import refused: " + failure)
{
    internal DropImportFailure Failure { get; } = failure;
}

internal sealed class DropImportBatch(string directory, IReadOnlyList<DropImportFile> files, bool expanded) : IDisposable
{
    private HashSet<string> kept = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<DropImportFile> Files { get; } = files;
    internal bool Expanded { get; } = expanded;
    internal void KeepFiles() => KeepFiles(Files.Where(file => file.Temporary).Select(file => file.Path));
    internal void KeepFiles(IEnumerable<string> paths) => kept.UnionWith(paths);
    public void Dispose()
    {
        if (kept.Count == 0) { Cleanup(directory); return; }
        foreach (var file in Files.Where(file => file.Temporary && !kept.Contains(file.Path)))
        {
            try { File.Delete(file.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Cleanup(string directory)
    {
        // The process still tracks this directory and retries cleanup if a scanner holds a file.
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class DropImport
{
    private static void AdmitArchive(Stream input, int remainingEntries, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        byte[] tail = new byte[(int)Math.Min(input.Length, 65_557)];
        input.Position = input.Length - tail.Length;
        input.ReadExactly(tail);
        cancellation.ThrowIfCancellationRequested();
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) != 0x06054b50) continue;
            // ZipArchive chooses the last footer signature. Never admit an earlier footer if
            // that downstream choice has a different extent hidden in its comment or trailer.
            if (index + 22 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) != tail.Length)
                throw new InvalidDataException("The archive has ambiguous trailing directory data.");
            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 10));
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 4)) != 0)
                throw new InvalidDataException("Multi-disk archives are not supported.");
            long directoryEnd = input.Length - tail.Length + index;
            long directorySize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 12));
            long directoryStart = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 16));
            if (count == ushort.MaxValue || directorySize == uint.MaxValue || directoryStart == uint.MaxValue)
            {
                if (directoryEnd < 20) throw new InvalidDataException("The ZIP64 locator is missing.");
                input.Position = directoryEnd - 20;
                byte[] locator = new byte[20];
                input.ReadExactly(locator);
                ulong position = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1
                    || position > (ulong)(directoryEnd - 20) || directoryEnd - 20 - (long)position < 56)
                    throw new InvalidDataException("The ZIP64 locator is invalid.");
                cancellation.ThrowIfCancellationRequested();
                input.Position = (long)position;
                byte[] footer = new byte[56];
                input.ReadExactly(footer);
                ulong size = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(4));
                ulong total = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(32));
                ulong extent = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(40));
                ulong offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(48));
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06064b50
                    || size != (ulong)(directoryEnd - 20 - (long)position - 12)
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(16)) != 0
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(24)) != total
                    || extent > long.MaxValue || offset > long.MaxValue)
                    throw new InvalidDataException("The ZIP64 directory footer is invalid.");
                if (total > (ulong)remainingEntries) throw new DropImportException(DropImportFailure.EntryLimit);
                count = (int)total;
                directoryStart = (long)offset;
                directorySize = (long)extent;
                directoryEnd = (long)position;
            }
            else if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 8)) != count)
                throw new InvalidDataException("The archive directory count is inconsistent.");
            if (count > remainingEntries) throw new DropImportException(DropImportFailure.EntryLimit);
            if (directoryStart > directoryEnd || directorySize != directoryEnd - directoryStart)
                throw new InvalidDataException("The archive directory extent is invalid.");
            // Each record has a fixed header and three unsigned 16-bit variable lengths. This
            // bounds metadata work by the admitted entry budget without reading variable data.
            if (directorySize > (long)remainingEntries * (46 + 3L * ushort.MaxValue) + 6 + ushort.MaxValue)
                throw new DropImportException(DropImportFailure.EntryLimit);
            input.Position = directoryStart;
            byte[] header = new byte[46];
            int actualCount = 0;
            while (input.Position < directoryEnd)
            {
                cancellation.ThrowIfCancellationRequested();
                if (directoryEnd - input.Position < 4)
                    throw new InvalidDataException("The archive directory record is incomplete.");
                input.ReadExactly(header.AsSpan(0, 4));
                cancellation.ThrowIfCancellationRequested();
                uint signature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (signature == 0x05054b50)
                {
                    if (directoryEnd - input.Position < 2)
                        throw new InvalidDataException("The archive directory signature is incomplete.");
                    input.ReadExactly(header.AsSpan(4, 2));
                    int signatureBytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                    if (signatureBytes != directoryEnd - input.Position)
                        throw new InvalidDataException("The archive directory signature extent is invalid.");
                    input.Seek(signatureBytes, SeekOrigin.Current);
                    break;
                }
                if (signature != 0x02014b50 || directoryEnd - input.Position < 42)
                    throw new InvalidDataException("The archive directory record is invalid.");
                input.ReadExactly(header.AsSpan(4));
                cancellation.ThrowIfCancellationRequested();
                if (actualCount >= remainingEntries) throw new DropImportException(DropImportFailure.EntryLimit);
                int variableBytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                    + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))
                    + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
                if (variableBytes > directoryEnd - input.Position)
                    throw new InvalidDataException("The archive directory record extends outside its directory.");
                input.Seek(variableBytes, SeekOrigin.Current);
                actualCount++;
            }
            if (actualCount != count) throw new InvalidDataException("The archive directory count is inconsistent.");
            cancellation.ThrowIfCancellationRequested();
            input.Position = 0;
            return;
        }
        throw new InvalidDataException("The archive directory footer is missing.");
    }

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    internal static bool SupportsFile(string path) => Supported.Contains(Path.GetExtension(path));
    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint crc = value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            table[value] = crc;
        }
        return table;
    }

    internal static DropImportBatch Prepare(IReadOnlyList<string> paths, string directory,
        DropImportLimits? limits = null, CancellationToken cancellation = default,
        Func<string, Stream>? openArchive = null)
    {
        cancellation.ThrowIfCancellationRequested();
        limits ??= new DropImportLimits();
        if (limits.Entries < 1 || limits.Depth < 0 || limits.FileBytes < 0 || limits.TotalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Import storage must be a new owned directory.");
        Directory.CreateDirectory(directory);
        var files = new List<DropImportFile>();
        bool expanded = false;
        int entries = 0;
        long totalBytes = 0;
        void CountEntry()
        {
            cancellation.ThrowIfCancellationRequested();
            if (entries >= limits.Entries) throw new DropImportException(DropImportFailure.EntryLimit);
            entries++;
        }
        void Collect(string folder, int depth, List<string> found)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(folder))
            {
                CountEntry();
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DropImportException(DropImportFailure.Link);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth >= limits.Depth) throw new DropImportException(DropImportFailure.DepthLimit);
                    Collect(child, depth + 1, found);
                }
                else if (Supported.Contains(Path.GetExtension(child))) found.Add(child);
            }
        }
        try
        {
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DropImportException(DropImportFailure.Link);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    expanded = true;
                    var found = new List<string>();
                    Collect(path, 0, found);
                    foreach (string file in found.OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(file => file, StringComparer.Ordinal))
                        files.Add(new DropImportFile(file, Path.GetFileName(file), false));
                }
                else if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    expanded = true;
                    using Stream archiveStream = openArchive?.Invoke(path) ?? File.OpenRead(path);
                    AdmitArchive(archiveStream, limits.Entries - entries, cancellation);
                    using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                    if (archive.Entries.Count > limits.Entries - entries)
                        throw new DropImportException(DropImportFailure.EntryLimit);
                    entries += archive.Entries.Count;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string name = entry.FullName.Replace('\\', '/').TrimEnd('/');
                        string[] segments = name.Split('/');
                        if (!names.Add(name) || segments.Any(segment => string.IsNullOrEmpty(segment)
                            || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                            || segment != segment.TrimEnd(' ', '.')))
                            throw new DropImportException(DropImportFailure.UnsafePath);
                        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new DropImportException(DropImportFailure.Link);
                        if (segments.Length - 1 > limits.Depth)
                            throw new DropImportException(DropImportFailure.DepthLimit);
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')
                            || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0
                            || ((entry.ExternalAttributes >> 16) & 0xF000) == 0x4000) continue;
                        string extension = Path.GetExtension(segments[^1]);
                        if (!Supported.Contains(extension)) continue;
                        if (entry.Length > limits.FileBytes)
                            throw new DropImportException(DropImportFailure.FileLimit);
                        if (entry.Length > limits.TotalBytes - totalBytes)
                            throw new DropImportException(DropImportFailure.TotalLimit);
                        string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
                        using (Stream input = entry.Open())
                        using (var destination = new FileStream(output, FileMode.CreateNew))
                        {
                            var buffer = new byte[65536];
                            long fileBytes = 0;
                            uint crc = uint.MaxValue;
                            while (true)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                int read = input.Read(buffer, 0, buffer.Length);
                                if (read == 0) break;
                                if (read > limits.FileBytes - fileBytes)
                                    throw new DropImportException(DropImportFailure.FileLimit);
                                if (read > limits.TotalBytes - totalBytes)
                                    throw new DropImportException(DropImportFailure.TotalLimit);
                                destination.Write(buffer, 0, read);
                                for (int index = 0; index < read; index++)
                                    crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[index]) & 255];
                                fileBytes += read;
                                totalBytes += read;
                            }
                            if (fileBytes != entry.Length || ~crc != entry.Crc32)
                                throw new InvalidDataException("The archive entry's decoded content does not match its declared size and checksum.");
                        }
                        files.Add(new DropImportFile(output, segments[^1], true));
                    }
                }
                else
                {
                    CountEntry();
                    if (Supported.Contains(Path.GetExtension(path)))
                        files.Add(new DropImportFile(path, Path.GetFileName(path), false));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return new DropImportBatch(directory, files, expanded);
        }
        catch
        {
            DropImportBatch.Cleanup(directory);
            throw;
        }
    }
}
