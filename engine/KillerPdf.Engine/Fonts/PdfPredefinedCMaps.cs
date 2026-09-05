// Adobe CMap data: https://github.com/adobe-type-tools/cmap-resources
// Unicode collection conversions: https://github.com/pdfminer/pdfminer.six
// Data was converted from pdfminer.six 20251230 byte tries into contiguous CID ranges.
// No pdfminer executable code is included or required.
//
// Copyright 1990-2023 Adobe. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
//
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
//
// Neither the name of Adobe nor the names of its contributors may be
// used to endorse or promote products derived from this software without
// specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// Copyright (c) 2004-2016 Yusuke Shinyama <yusuke at shinyama dot jp>
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
// KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace KillerPdf.Engine.Fonts;

internal sealed class PdfPredefinedCMaps
{
    private readonly record struct Range(int Length, uint First, uint Last, uint Cid);
    private readonly Range[] _ranges;
    private static readonly ConcurrentDictionary<string, PdfPredefinedCMaps> EncodingCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<uint, string>> UnicodeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, PdfToUnicodeMap> UnicodeMapCache = new(StringComparer.Ordinal);

    private PdfPredefinedCMaps(string encoded)
    {
        byte[] data = Decompress(encoded);
        if (data.Length % 13 != 0) throw new FormatException("Invalid predefined font mapping data.");
        _ranges = new Range[data.Length / 13];
        for (int i = 0; i < _ranges.Length; i++)
        {
            var row = data.AsSpan(i * 13, 13);
            _ranges[i] = new Range(row[0], BinaryPrimitives.ReadUInt32BigEndian(row[1..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[5..]), BinaryPrimitives.ReadUInt32BigEndian(row[9..]));
        }
    }

    internal static PdfPredefinedCMaps? Find(string name) => PdfPredefinedCMapData.Encodings.TryGetValue(name, out string? data)
        ? EncodingCache.GetOrAdd(name, _ => new PdfPredefinedCMaps(data)) : null;

    internal static PdfToUnicodeMap? FindUnicodeMap(string name)
    {
        const string suffix = "-UCS2";
        if (!name.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string collection = name[..^suffix.Length];
        string dataName = collection + "-H";
        if (!PdfPredefinedUnicodeData.Collections.TryGetValue(dataName, out string? encoded))
            return null;
        return UnicodeMapCache.GetOrAdd(name,
            _ => PdfToUnicodeMap.Create(ReadUnicode(encoded), 2));
    }

    internal uint Cid(uint code)
    {
        for (int length = 1; length <= 4; length++)
            if (TryCid(code, length, out uint cid)) return cid;
        return 0;
    }

    private bool TryCid(uint code, int length, out uint cid)
    {
        int low = 0, high = _ranges.Length - 1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            var range = _ranges[mid];
            if (range.Length < length || (range.Length == length && range.Last < code)) low = mid + 1;
            else if (range.Length > length || range.First > code) high = mid - 1;
            else { cid = range.Cid + code - range.First; return true; }
        }
        cid = 0;
        return false;
    }

    internal IReadOnlyList<PdfDecodedCharacter> Decode(ReadOnlyMemory<byte> input, PdfToUnicodeMap unicode, Func<uint, string?> fallback)
    {
        var result = new List<PdfDecodedCharacter>();
        var bytes = input.Span;
        for (int offset = 0; offset < bytes.Length;)
        {
            uint code = 0;
            bool found = false;
            for (int length = 1; length <= 4 && offset + length <= bytes.Length; length++)
            {
                code = (code << 8) | bytes[offset + length - 1];
                if (!TryCid(code, length, out _)) continue;
                result.Add(new PdfDecodedCharacter(code, length, unicode.Lookup(code, length) ?? fallback(code) ?? "\uFFFD"));
                offset += length;
                found = true;
                break;
            }
            if (!found)
            {
                result.Add(new PdfDecodedCharacter(bytes[offset], 1, "\uFFFD"));
                offset++;
            }
        }
        return result.AsReadOnly();
    }

    internal static string? Unicode(string collection, bool vertical, uint cid)
    {
        string name = collection + (vertical ? "-V" : "-H");
        if (!PdfPredefinedUnicodeData.Collections.TryGetValue(name, out string? encoded)) return null;
        return UnicodeCache.GetOrAdd(name, _ => ReadUnicode(encoded)).GetValueOrDefault(cid);
    }

    private static Dictionary<uint, string> ReadUnicode(string encoded)
    {
        byte[] data = Decompress(encoded);
        var result = new Dictionary<uint, string>();
        for (int offset = 0; offset < data.Length;)
        {
            if (offset + 3 > data.Length) throw new FormatException("Invalid predefined Unicode mapping data.");
            uint cid = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            int length = data[offset + 2];
            offset += 3;
            if (offset + length > data.Length) throw new FormatException("Invalid predefined Unicode text data.");
            result[cid] = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
        }
        return result;
    }

    private static byte[] Decompress(string encoded)
    {
        using var input = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
