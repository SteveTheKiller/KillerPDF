using System.Numerics;
using KillerPdf.Engine.Formats.Djvu;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

/// <summary>
/// A ZP-coder encoder for building DjVu test streams (BZZ directories and JB2 masks). It mirrors the decoder's
/// interval arithmetic: the decoder keeps a 16-bit window <c>c</c> of the code value with <c>a &lt;= c &lt; 0x10000</c>,
/// so the encoder keeps the window's upper bound and the offset between the window and the code value, and the
/// finished code value is the largest one every decision allows.
/// </summary>
internal sealed class DjvuTestZpEncoder
{
    private readonly bool _ordinaryUnmodeled;
    private int _a;
    private int _upper = 0x10000;
    // The offset D (code value = window + D) as a bit string and its low 32 bits, so carries stay cheap.
    // It starts with zero bits of headroom because D dips below zero while a window is raised past the code value.
    private readonly List<byte> _prefix = new(new byte[40]);
    private long _low;
    private int _shifts;

    internal DjvuTestZpEncoder(bool ordinaryUnmodeled) => _ordinaryUnmodeled = ordinaryUnmodeled;

    /// <summary>Codes one decision with the context byte at <paramref name="index"/> of <paramref name="contexts"/>, or unmodeled when index is negative.</summary>
    internal void Bit(int bit, byte[]? contexts, int index)
    {
        var state = index < 0 ? default : DjvuZpDecoder.States[contexts![index]];
        int split = index < 0 ? 0x8000 + (_ordinaryUnmodeled ? _a >> 1 : (_a * 3) >> 3)
            : Math.Min(_a + state.Delta, 0x6000 + ((_a * 2 + state.Delta) >> 2));
        int most = index < 0 ? 0 : contexts![index] & 1;
        if (bit == most)
        {
            if (index >= 0 && split >= 0x8000 && _a >= state.Threshold) contexts![index] = state.More;
            _a = split;
        }
        else
        {
            _upper = split <= _upper ? 0x10000 : _upper + 0x10000 - split;
            _a = (_a + 0x10000 - split) & 0xffff;
            Add(split - 0x10000);
            if (index >= 0) contexts![index] = state.Less;
        }
        while (_a >= 0x8000)
        {
            _a = (_a << 1) & 0xffff;
            _upper = 2 * _upper - 0x10000;
            _prefix.Add((byte)((_low >> 31) & 1));
            _low = ((_low << 1) & 0xffffffffL);
            _shifts++;
            Add(0x10000);
        }
    }

    // D += delta, with the carry or borrow going into the prefix.
    private void Add(long delta)
    {
        _low += delta;
        if (_low >= 1L << 32)
        {
            _low -= 1L << 32;
            for (int i = _prefix.Count - 1; i >= 0; i--)
            {
                if (_prefix[i] == 0) { _prefix[i] = 1; return; }
                _prefix[i] = 0;
            }
            throw new InvalidOperationException("Carry out of the code value.");
        }
        if (_low < 0)
        {
            _low += 1L << 32;
            for (int i = _prefix.Count - 1; i >= 0; i--)
            {
                if (_prefix[i] == 1) { _prefix[i] = 0; return; }
                _prefix[i] = 1;
            }
            throw new InvalidOperationException("Borrow out of the code value.");
        }
    }

    /// <summary>The code bytes, padded with one bits as the decoder pads past the end.</summary>
    internal byte[] Finish()
    {
        Add(_upper - 1);
        var bits = new List<byte>(_prefix);
        for (int i = 31; i >= 0; i--) bits.Add((byte)((_low >> i) & 1));
        // The code value has 16 + shifts bits; D was kept 32 bits wider than the window.
        int drop = bits.Count - (16 + _shifts);
        if (drop < 0) throw new InvalidOperationException("The code value is narrower than its window.");
        for (int i = 0; i < drop; i++)
            if (bits[i] != 0) throw new InvalidOperationException("The code value is wider than its window.");
        bits.RemoveRange(0, drop);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bytes.Length * 8; i++)
            if (i >= bits.Count || bits[i] == 1) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }
}

/// <summary>Builders of DjVu test files: BZZ blocks, bundled documents with a real directory, and JB2 pages.</summary>
internal static class DjvuTestStreams
{
    internal static byte[] Chunk(string id, byte[] payload) => DjvuJb2Tests.Chunk(id, payload);

    internal static byte[] Form(string type, params byte[][] chunks)
        => Chunk("FORM", [.. System.Text.Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);

    // A BZZ stream of one block: the Burrows-Wheeler transform of the data and an end sentinel, move-to-front ranked
    // as Appendix 4 describes and coded with the ZP coder, then the zero block size that ends the stream.
    internal static byte[] Bzz(byte[] data)
    {
        int size = data.Length + 1;
        var text = new int[size];
        for (int i = 0; i < data.Length; i++) text[i] = data[i];
        text[size - 1] = -1;
        var order = Enumerable.Range(0, size).ToArray();
        Array.Sort(order, (x, y) =>
        {
            for (int k = 0; k < size; k++)
            {
                int c = text[(x + k) % size].CompareTo(text[(y + k) % size]);
                if (c != 0) return c;
            }
            return 0;
        });
        var encoder = new DjvuTestZpEncoder(ordinaryUnmodeled: true);
        for (int i = 23; i >= 0; i--) encoder.Bit((size >> i) & 1, null, -1);
        encoder.Bit(0, null, -1);
        var contexts = new byte[262];
        var alphabet = new byte[256];
        for (int i = 0; i < 256; i++) alphabet[i] = (byte)i;
        var frequencies = new int[4];
        int increment = 4, previous = 3;
        for (int row = 0; row < size; row++)
        {
            int last = text[(order[row] + size - 1) % size];
            int rank = last < 0 ? 256 : Array.IndexOf(alphabet, (byte)last);
            int context = Math.Min(previous, 2);
            if (rank == 0) encoder.Bit(1, contexts, context);
            else
            {
                encoder.Bit(0, contexts, context);
                if (rank == 1) encoder.Bit(1, contexts, context + 3);
                else
                {
                    encoder.Bit(0, contexts, context + 3);
                    for (int bits = 1; bits <= 7; bits++)
                    {
                        int first = 1 << bits;
                        if (rank < 2 * first && rank >= first)
                        {
                            encoder.Bit(1, contexts, first + 4);
                            int node = 1;
                            for (int k = bits - 1; k >= 0; k--)
                            {
                                int b = ((rank - first) >> k) & 1;
                                encoder.Bit(b, contexts, first + 5 + node - 1);
                                node = node * 2 + b;
                            }
                            break;
                        }
                        encoder.Bit(0, contexts, first + 4);
                    }
                }
            }
            previous = rank;
            if (rank == 256) continue;
            byte symbol = (byte)last;
            increment += increment;
            if (increment > 0x10000000)
            {
                increment >>= 24;
                for (int j = 0; j < 4; j++) frequencies[j] >>= 24;
            }
            int frequency = increment + (rank < 4 ? frequencies[rank] : 0);
            int destination = rank;
            while (destination > 3) { alphabet[destination] = alphabet[destination - 1]; destination--; }
            while (destination > 0 && frequency >= frequencies[destination - 1])
            {
                alphabet[destination] = alphabet[destination - 1];
                frequencies[destination] = frequencies[destination - 1];
                destination--;
            }
            alphabet[destination] = symbol;
            frequencies[destination] = frequency;
        }
        for (int i = 23; i >= 0; i--) encoder.Bit(0, null, -1);
        return encoder.Finish();
    }

    /// <summary>A bundled document whose directory lists each component's ID and kind (0 include, 1 page, 3 annotations).</summary>
    internal static byte[] Bundle(params (string Id, int Kind, byte[] Form)[] components)
    {
        var decoded = new List<byte>();
        foreach (var (_, _, form) in components)
        {
            int length = form.Length - (form.Length % 2 == 1 ? 1 : 0);
            decoded.AddRange([(byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        }
        foreach (var (_, kind, _) in components) decoded.Add((byte)kind);
        foreach (var (id, _, _) in components) decoded.AddRange([.. System.Text.Encoding.UTF8.GetBytes(id), (byte)0]);
        var compressed = Bzz([.. decoded]);
        var directory = new byte[3 + 4 * components.Length + compressed.Length];
        directory[0] = 0x81;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(1), (ushort)components.Length);
        compressed.CopyTo(directory, 3 + 4 * components.Length);
        int offset = 16 + Chunk("DIRM", directory).Length;
        for (int i = 0; i < components.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(3 + 4 * i), (uint)offset);
            offset += components[i].Form.Length;
        }
        return [.. "AT&T"u8, .. Form("DJVM", [Chunk("DIRM", directory), .. components.Select(c => c.Form)])];
    }

    // The Sjbz payload of DjvuJb2Tests.NestedDictionaryBundle's page: it requires a shared dictionary of two shapes
    // and then ends, so what a bundle makes of its includes decides the result.
    private static readonly byte[] RequiringSjbz = Convert.FromHexString("15004920B9CC8112F7");

    private static byte[] Info(int width, int height)
        => Chunk("INFO", [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 24, 0, 0x2c, 1, 22, 1]);

    /// <summary>
    /// A bundle of <paramref name="levels"/> include components, each holding <paramref name="fanOut"/> INCL chunks to
    /// the next and no Djbz, and a 64 x 40 page that includes the first and whose Sjbz requires a shared dictionary
    /// the bundle does not hold. Searching every INCL path visits fanOut^(levels - 1) times the last component.
    /// </summary>
    internal static byte[] NestedInclBundle(int levels, int fanOut)
    {
        var components = new List<(string, int, byte[])>();
        for (int level = 1; level <= levels; level++)
            components.Add(($"inc{level}.djvi", 0, Form("DJVI",
                level == levels ? [] : Enumerable.Range(0, fanOut).Select(_ => Chunk("INCL", System.Text.Encoding.ASCII.GetBytes($"inc{level + 1}.djvi"))).ToArray())));
        components.Add(("page.djvu", 1, Form("DJVU", Info(64, 40), Chunk("INCL", "inc1.djvi"u8.ToArray()),
            Chunk("Sjbz", RequiringSjbz))));
        return Bundle([.. components]);
    }
}
