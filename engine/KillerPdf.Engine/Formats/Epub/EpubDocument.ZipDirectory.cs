using System.Buffers.Binary;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private static void ValidateZipDirectory(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        int firstCandidate = Math.Max(0, bytes.Length - 65557);
        for (int offset = bytes.Length - 22; offset >= firstCandidate; offset--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) != 0x06054b50 ||
                offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 20)..]) != bytes.Length)
                continue;
            int declaredCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 10)..]);
            uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 12)..]);
            uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 16)..]);
            if (declaredCount == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue ||
                (offset >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset - 20)..]) == 0x07064b50))
                throw new InvalidDataException("ZIP64 EPUB containers are unsupported.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..]) != 0)
                throw new InvalidDataException("Multi-volume EPUB ZIP containers are unsupported.");
            if (declaredCount > 4096)
                throw new InvalidDataException("EPUB exceeds the resource count limit.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 8)..]) != declaredCount)
                throw new InvalidDataException("Invalid EPUB ZIP directory entry count.");
            if (directoryOffset > offset || directorySize > offset - directoryOffset || directoryOffset + directorySize != offset)
                throw new InvalidDataException("Invalid EPUB ZIP directory bounds.");
            int cursor = (int)directoryOffset;
            int actualCount = 0;
            while (cursor < offset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++actualCount > declaredCount || actualCount > 4096)
                    throw new InvalidDataException("Invalid EPUB ZIP directory entry count.");
                if (offset - cursor < 46 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]) != 0x02014b50)
                    throw new InvalidDataException("Invalid EPUB ZIP directory header.");
                int recordLength = 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 28)..]) +
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 30)..]) +
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 32)..]);
                if (recordLength > offset - cursor)
                    throw new InvalidDataException("Invalid EPUB ZIP directory record length.");
                uint localOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 42)..]);
                uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 20)..]);
                if (localOffset == uint.MaxValue || compressedSize == uint.MaxValue ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 24)..]) == uint.MaxValue)
                    throw new InvalidDataException("ZIP64 EPUB containers are unsupported.");
                if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 34)..]) != 0)
                    throw new InvalidDataException("Multi-volume EPUB ZIP containers are unsupported.");
                if (localOffset > directoryOffset || directoryOffset - localOffset < 30 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)localOffset..]) != 0x04034b50)
                    throw new InvalidDataException("Invalid EPUB ZIP resource bounds.");
                long dataOffset = localOffset + 30L + BinaryPrimitives.ReadUInt16LittleEndian(bytes[((int)localOffset + 26)..]) +
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[((int)localOffset + 28)..]);
                if (dataOffset > directoryOffset || compressedSize > directoryOffset - dataOffset)
                    throw new InvalidDataException("Invalid EPUB ZIP resource bounds.");
                cursor += recordLength;
            }
            if (actualCount != declaredCount)
                throw new InvalidDataException("Invalid EPUB ZIP directory entry count.");
            return;
        }
        throw new InvalidDataException("Missing EPUB ZIP directory terminator.");
    }
}
