using System.Buffers.Binary;
using System.IO;

namespace KillerPDF.Services;

internal static partial class InstalledFontCatalog
{
    /// <summary>Chooses a real installed face by CSS weight without changing the editor's Boolean-style lookup.</summary>
    internal static (byte[] Bytes, int Weight)? EpubFaceBytes(string family, double requestedWeight)
    {
        if (string.IsNullOrWhiteSpace(family) || !double.IsFinite(requestedWeight)
            || requestedWeight is < 1 or > 1000) return null;
        EnsureIndexed();
        FaceFile? best = null;
        int bestWeight = 0, candidates = 0;
        (int Style, int Group, double Distance) bestRank = (int.MaxValue, int.MaxValue, double.MaxValue);
        foreach (var entry in Order)
        {
            if (!entry.Family.Equals(family, StringComparison.OrdinalIgnoreCase)) continue;
            if (++candidates > 256) throw new InvalidDataException("The EPUB font family exceeds its 256 installed-face limit.");
            var face = new FaceFile(entry.Path, entry.FaceIndex);
            int? weight;
            try
            {
                using var input = new FileStream(face.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                weight = ReadEpubWeight(input, face.FaceIndex);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            if (weight is null) continue;
            var weightRank = WeightRank(weight.Value, requestedWeight);
            var rank = (entry.Style is FaceStyle.Italic or FaceStyle.BoldItalic ? 1 : 0,
                weightRank.Group, weightRank.Distance);
            if (rank.CompareTo(bestRank) >= 0) continue;
            best = face;
            bestWeight = weight.Value;
            bestRank = rank;
        }
        if (best is not { } chosen) return null;
        try
        {
            using var input = new FileStream(chosen.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 64L << 20) return null;
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (IsCollection(bytes))
            {
                var standalone = ExtractTtcFace(bytes, chosen.FaceIndex);
                if (standalone is null) return null;
                bytes = standalone;
            }
            using var captured = new MemoryStream(bytes, writable: false);
            // Do not report metadata from a different file revision than the captured face.
            if (ReadEpubWeight(captured, 0) != bestWeight) return null;
            return (bytes, bestWeight);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static (int Group, double Distance) WeightRank(int weight, double target)
    {
        if (target is >= 400 and <= 500)
        {
            if (weight >= target && weight <= 500) return (0, weight - target);
            if (weight < target) return (1, target - weight);
            return (2, weight - 500);
        }
        if (target < 400) return weight <= target ? (0, target - weight) : (1, weight - target);
        return weight >= target ? (0, weight - target) : (1, target - weight);
    }

    private static int? ReadEpubWeight(Stream input, int faceIndex)
    {
        Span<byte> header = stackalloc byte[12];
        if (!ReadAt(0, header)) return null;
        long faceOffset = 0;
        if (header[..4].SequenceEqual("ttcf"u8))
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
            if (faceIndex < 0 || faceIndex >= count || count > 65535) return null;
            Span<byte> offset = stackalloc byte[4];
            if (!ReadAt(12L + 4L * faceIndex, offset)) return null;
            faceOffset = BinaryPrimitives.ReadUInt32BigEndian(offset);
            if (!ReadAt(faceOffset, header)) return null;
        }
        else if (faceIndex != 0) return null;
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (signature is not (0x00010000 or 0x4F54544F or 0x74727565)) return null;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (tables is < 1 or > 4096) return null;
        Span<byte> record = stackalloc byte[16];
        Span<byte> field = stackalloc byte[2];
        int? found = null;
        for (int index = 0; index < tables; index++)
        {
            if (!ReadAt(faceOffset + 12 + 16L * index, record)) return null;
            if (!record[..4].SequenceEqual("OS/2"u8)) continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (found is not null || length < 6 || offset > input.Length - length) return null;
            if (!ReadAt((long)offset + 4, field)) return null;
            int value = BinaryPrimitives.ReadUInt16BigEndian(field);
            if (value is < 1 or > 1000) return null;
            found = value;
        }
        return found;

        bool ReadAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > input.Length - destination.Length) return false;
            input.Position = offset;
            input.ReadExactly(destination);
            return true;
        }
    }
}
