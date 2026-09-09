// Copyright (c) 2026 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections.Generic;
using System.Text;
using CoreJ2K.j2k.fileformat;

namespace CoreJ2K.j2k.fileformat.metadata
{
    /// <summary>
    /// Low-level helpers for reading and writing the 8-byte box framing
    /// (LBox + TBox) used throughout the JP2/JPX family of file formats, plus
    /// big-endian integer accessors over a byte buffer.
    /// </summary>
    /// <remarks>
    /// These helpers operate purely on in-memory byte buffers so that the JPX box
    /// model classes can own their own binary (de)serialization independently of
    /// the streaming reader/writer. Only the standard 8-byte header is handled;
    /// the 16-byte Extended Length (XLBox) form is not expected for the metadata
    /// boxes modelled here and is rejected during parsing.
    /// </remarks>
    internal static class Jp2BoxIO
    {
        public static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        public static uint ReadU32(byte[] b, int o) =>
            ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        public static ulong ReadU64(byte[] b, int o)
        {
            ulong hi = ReadU32(b, o);
            ulong lo = ReadU32(b, o + 4);
            return (hi << 32) | lo;
        }

        public static void WriteU16(List<byte> dst, ushort v)
        {
            dst.Add((byte)(v >> 8));
            dst.Add((byte)v);
        }

        public static void WriteU32(List<byte> dst, uint v)
        {
            dst.Add((byte)(v >> 24));
            dst.Add((byte)(v >> 16));
            dst.Add((byte)(v >> 8));
            dst.Add((byte)v);
        }

        public static void WriteU64(List<byte> dst, ulong v)
        {
            WriteU32(dst, (uint)(v >> 32));
            WriteU32(dst, (uint)v);
        }

        /// <summary>
        /// Frames the supplied content as a complete box (LBox + TBox + content)
        /// and appends it to <paramref name="dst"/>.
        /// </summary>
        public static void WriteBox(List<byte> dst, int boxType, byte[] content)
        {
            WriteU32(dst, (uint)(content.Length + 8)); // LBox
            WriteU32(dst, (uint)boxType);              // TBox
            dst.AddRange(content);
        }

        /// <summary>
        /// Parses the sequence of child boxes contained in <paramref name="payload"/>
        /// between <paramref name="start"/> (inclusive) and <paramref name="end"/>
        /// (exclusive), returning each child as raw type + content bytes.
        /// </summary>
        public static List<Jp2BoxData> ParseChildren(byte[] payload, int start, int end)
        {
            var children = new List<Jp2BoxData>();
            var pos = start;

            while (pos + 8 <= end)
            {
                var lbox = (long)ReadU32(payload, pos);
                var tbox = (int)ReadU32(payload, pos + 4);

                int contentStart = pos + 8;
                int contentEnd;

                if (lbox == 0)
                {
                    // Box extends to the end of the enclosing payload.
                    contentEnd = end;
                }
                else if (lbox == 1)
                {
                    // Extended length (XLBox) is not modelled for these boxes; stop
                    // parsing rather than risk misinterpreting the buffer.
                    break;
                }
                else
                {
                    contentEnd = pos + (int)lbox;
                }

                if (contentEnd < contentStart || contentEnd > end)
                    break; // Truncated / malformed; preserve what we have.

                var content = new byte[contentEnd - contentStart];
                Array.Copy(payload, contentStart, content, 0, content.Length);
                children.Add(new Jp2BoxData { BoxType = tbox, Content = content });

                if (lbox == 0)
                    break;

                pos = contentEnd;
            }

            return children;
        }

        /// <summary>Serializes a list of child boxes back into a contiguous payload.</summary>
        public static byte[] WriteChildren(IEnumerable<Jp2BoxData> children)
        {
            var dst = new List<byte>();
            foreach (var child in children)
                WriteBox(dst, child.BoxType, child.Content ?? Array.Empty<byte>());
            return dst.ToArray();
        }
    }

    /// <summary>
    /// A generic, fully round-trippable representation of a JP2/JPX box that this
    /// library recognizes structurally but does not decode into a strongly-typed
    /// model (for example the child boxes of a Codestream Header or Compositing
    /// Layer Header superbox). The <see cref="Content"/> is the raw box payload
    /// excluding the 8-byte header.
    /// </summary>
    public class Jp2BoxData
    {
        /// <summary>Gets or sets the box type (TBox), e.g. 0x636f6c72 for 'colr'.</summary>
        public int BoxType { get; set; }

        /// <summary>Gets or sets the raw box payload (DBox), excluding the 8-byte header.</summary>
        public byte[] Content { get; set; } = Array.Empty<byte>();

        /// <summary>Gets the four-character type code as a string (e.g. "colr").</summary>
        public string TypeString
        {
            get
            {
                var chars = new char[4];
                chars[0] = (char)((BoxType >> 24) & 0xFF);
                chars[1] = (char)((BoxType >> 16) & 0xFF);
                chars[2] = (char)((BoxType >> 8) & 0xFF);
                chars[3] = (char)(BoxType & 0xFF);
                return new string(chars);
            }
        }

        public override string ToString() => $"Box '{TypeString}' ({Content?.Length ?? 0} bytes)";
    }

    /// <summary>
    /// Represents a Number List box ('nlst') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// A Number List enumerates the entities (the whole rendered result, individual
    /// codestreams, or compositing layers) to which an enclosing Association box applies.
    /// </summary>
    public class NumberListBox
    {
        /// <summary>AN value indicating the complete rendered result (the whole file).</summary>
        public const uint RenderedResult = 0x00000000u;

        private const uint CodestreamTag = 0x01000000u;
        private const uint CompositingLayerTag = 0x02000000u;
        private const uint IndexMask = 0x00FFFFFFu;

        /// <summary>
        /// Gets the raw association numbers (AN values). The most significant byte
        /// selects the kind (0 = rendered result, 1 = codestream, 2 = compositing layer)
        /// and the lower three bytes carry the zero-based index.
        /// </summary>
        public List<uint> Entries { get; } = new List<uint>();

        /// <summary>Adds an association to the complete rendered result.</summary>
        public NumberListBox AddRenderedResult()
        {
            Entries.Add(RenderedResult);
            return this;
        }

        /// <summary>Adds an association to a specific codestream by zero-based index.</summary>
        public NumberListBox AddCodestream(uint index)
        {
            Entries.Add(CodestreamTag | (index & IndexMask));
            return this;
        }

        /// <summary>Adds an association to a specific compositing layer by zero-based index.</summary>
        public NumberListBox AddCompositingLayer(uint index)
        {
            Entries.Add(CompositingLayerTag | (index & IndexMask));
            return this;
        }

        /// <summary>Returns true if the supplied AN value refers to a codestream.</summary>
        public static bool IsCodestream(uint an) => (an & 0xFF000000u) == CodestreamTag;

        /// <summary>Returns true if the supplied AN value refers to a compositing layer.</summary>
        public static bool IsCompositingLayer(uint an) => (an & 0xFF000000u) == CompositingLayerTag;

        /// <summary>Returns the zero-based index carried by an AN value.</summary>
        public static uint IndexOf(uint an) => an & IndexMask;

        /// <summary>Parses a Number List box from its content bytes (excluding header).</summary>
        public static NumberListBox Parse(byte[] content)
        {
            var box = new NumberListBox();
            for (var o = 0; o + 4 <= content.Length; o += 4)
                box.Entries.Add(Jp2BoxIO.ReadU32(content, o));
            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>(Entries.Count * 4);
            foreach (var an in Entries)
                Jp2BoxIO.WriteU32(dst, an);
            return dst.ToArray();
        }

        public override string ToString() => $"Number List Box: {Entries.Count} entrie(s)";
    }

    /// <summary>
    /// Represents an Association box ('asoc') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// The Association box is a superbox that binds a set of metadata boxes to the
    /// entities identified by its first sub-box (conventionally a Number List box).
    /// </summary>
    public class AssociationBox
    {
        /// <summary>
        /// Gets or sets the Number List box that identifies the associated entities.
        /// When present it is written as the first child of the association.
        /// </summary>
        public NumberListBox? NumberList { get; set; }

        /// <summary>
        /// Gets the remaining child boxes carried by this association (labels, XML,
        /// nested associations, etc.), preserved in document order as raw boxes.
        /// </summary>
        public List<Jp2BoxData> Children { get; } = new List<Jp2BoxData>();

        /// <summary>Parses an Association box from its content bytes (excluding header).</summary>
        public static AssociationBox Parse(byte[] content)
        {
            var box = new AssociationBox();
            var children = Jp2BoxIO.ParseChildren(content, 0, content.Length);

            foreach (var child in children)
            {
                if (box.NumberList == null && child.BoxType == FileFormatBoxes.NUMBER_LIST_BOX)
                    box.NumberList = NumberListBox.Parse(child.Content);
                else
                    box.Children.Add(child);
            }

            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>();
            if (NumberList != null)
                Jp2BoxIO.WriteBox(dst, FileFormatBoxes.NUMBER_LIST_BOX, NumberList.GetContentBytes());
            foreach (var child in Children)
                Jp2BoxIO.WriteBox(dst, child.BoxType, child.Content ?? Array.Empty<byte>());
            return dst.ToArray();
        }

        public override string ToString() =>
            $"Association Box: {(NumberList != null ? NumberList.Entries.Count + " entity ref(s), " : "")}{Children.Count} child box(es)";
    }

    /// <summary>
    /// A single Data Entry URL box ('url ') as carried inside a Data Reference box.
    /// It records where externally-referenced data may be found.
    /// </summary>
    public class DataEntryUrl
    {
        /// <summary>Gets or sets the version byte (VERS), normally 0.</summary>
        public byte Version { get; set; }

        /// <summary>Gets or sets the 3-byte flags field (FLAG).</summary>
        public byte[] Flags { get; set; } = new byte[3];

        /// <summary>Gets or sets the URL (LOC), stored without its null terminator.</summary>
        public string Url { get; set; } = string.Empty;

        public override string ToString() => $"Data Entry URL: {Url}";
    }

    /// <summary>
    /// Represents a Data Reference box ('dtbl') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// It holds an ordered list of Data Entry URL boxes that Fragment List boxes refer
    /// to by one-based index in order to locate data stored outside the current file.
    /// </summary>
    public class DataReferenceBox
    {
        /// <summary>Gets the ordered list of data entry URLs (one-based when referenced).</summary>
        public List<DataEntryUrl> Entries { get; } = new List<DataEntryUrl>();

        /// <summary>Adds a URL entry and returns this box for chaining.</summary>
        public DataReferenceBox AddUrl(string url)
        {
            Entries.Add(new DataEntryUrl { Url = url });
            return this;
        }

        /// <summary>Parses a Data Reference box from its content bytes (excluding header).</summary>
        public static DataReferenceBox Parse(byte[] content)
        {
            var box = new DataReferenceBox();
            if (content.Length < 2)
                return box;

            var ndr = Jp2BoxIO.ReadU16(content, 0);
            var children = Jp2BoxIO.ParseChildren(content, 2, content.Length);

            var count = 0;
            foreach (var child in children)
            {
                if (count >= ndr)
                    break;
                if (child.BoxType != FileFormatBoxes.URL_BOX)
                    continue;

                var entry = new DataEntryUrl();
                var c = child.Content;
                if (c.Length >= 4)
                {
                    entry.Version = c[0];
                    entry.Flags = new[] { c[1], c[2], c[3] };
                    var urlBytes = c.Length > 4 ? new byte[c.Length - 4] : Array.Empty<byte>();
                    if (urlBytes.Length > 0)
                        Array.Copy(c, 4, urlBytes, 0, urlBytes.Length);
                    entry.Url = Encoding.UTF8.GetString(urlBytes).TrimEnd('\0');
                }
                box.Entries.Add(entry);
                count++;
            }

            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>();
            Jp2BoxIO.WriteU16(dst, (ushort)Entries.Count); // NDR

            foreach (var entry in Entries)
            {
                var urlBytes = Encoding.UTF8.GetBytes(entry.Url ?? string.Empty);
                var urlContent = new List<byte>(urlBytes.Length + 5)
                {
                    entry.Version,
                    entry.Flags.Length > 0 ? entry.Flags[0] : (byte)0,
                    entry.Flags.Length > 1 ? entry.Flags[1] : (byte)0,
                    entry.Flags.Length > 2 ? entry.Flags[2] : (byte)0
                };
                urlContent.AddRange(urlBytes);
                urlContent.Add(0); // null terminator (LOC is a null-terminated string)
                Jp2BoxIO.WriteBox(dst, FileFormatBoxes.URL_BOX, urlContent.ToArray());
            }

            return dst.ToArray();
        }

        public override string ToString() => $"Data Reference Box: {Entries.Count} URL(s)";
    }

    /// <summary>
    /// A single fragment entry within a Fragment List box: a (offset, length)
    /// span located in the data source identified by <see cref="DataReference"/>.
    /// </summary>
    public class Fragment
    {
        /// <summary>Gets or sets the byte offset of the fragment (OFF).</summary>
        public ulong Offset { get; set; }

        /// <summary>Gets or sets the length of the fragment in bytes (LEN).</summary>
        public uint Length { get; set; }

        /// <summary>
        /// Gets or sets the data reference index (DR). 0 means the current file;
        /// a positive value is a one-based index into the Data Reference box.
        /// </summary>
        public ushort DataReference { get; set; }

        public override string ToString() => $"Fragment[off={Offset}, len={Length}, dr={DataReference}]";
    }

    /// <summary>
    /// Represents a Fragment List box ('flst') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// It lists the fragments (each a byte span in some data source) that together make
    /// up a logical entity such as a fragmented codestream.
    /// </summary>
    public class FragmentListBox
    {
        /// <summary>Gets the ordered list of fragments.</summary>
        public List<Fragment> Fragments { get; } = new List<Fragment>();

        /// <summary>Adds a fragment and returns this box for chaining.</summary>
        public FragmentListBox AddFragment(ulong offset, uint length, ushort dataReference = 0)
        {
            Fragments.Add(new Fragment { Offset = offset, Length = length, DataReference = dataReference });
            return this;
        }

        /// <summary>Parses a Fragment List box from its content bytes (excluding header).</summary>
        public static FragmentListBox Parse(byte[] content)
        {
            var box = new FragmentListBox();
            if (content.Length < 2)
                return box;

            var nf = Jp2BoxIO.ReadU16(content, 0);
            var o = 2;
            for (var i = 0; i < nf && o + 14 <= content.Length; i++, o += 14)
            {
                box.Fragments.Add(new Fragment
                {
                    Offset = Jp2BoxIO.ReadU64(content, o),
                    Length = Jp2BoxIO.ReadU32(content, o + 8),
                    DataReference = Jp2BoxIO.ReadU16(content, o + 12)
                });
            }

            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>(2 + Fragments.Count * 14);
            Jp2BoxIO.WriteU16(dst, (ushort)Fragments.Count); // NF
            foreach (var f in Fragments)
            {
                Jp2BoxIO.WriteU64(dst, f.Offset);
                Jp2BoxIO.WriteU32(dst, f.Length);
                Jp2BoxIO.WriteU16(dst, f.DataReference);
            }
            return dst.ToArray();
        }

        public override string ToString() => $"Fragment List Box: {Fragments.Count} fragment(s)";
    }

    /// <summary>
    /// Represents a Fragment Table box ('ftbl') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// This superbox wraps a single Fragment List box and is used to describe a codestream
    /// whose data is stored as fragments (possibly in external files).
    /// </summary>
    public class FragmentTableBox
    {
        /// <summary>Gets or sets the contained Fragment List box.</summary>
        public FragmentListBox? FragmentList { get; set; }

        /// <summary>Parses a Fragment Table box from its content bytes (excluding header).</summary>
        public static FragmentTableBox Parse(byte[] content)
        {
            var box = new FragmentTableBox();
            foreach (var child in Jp2BoxIO.ParseChildren(content, 0, content.Length))
            {
                if (child.BoxType == FileFormatBoxes.FRAGMENT_LIST_BOX)
                {
                    box.FragmentList = FragmentListBox.Parse(child.Content);
                    break;
                }
            }
            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>();
            if (FragmentList != null)
                Jp2BoxIO.WriteBox(dst, FileFormatBoxes.FRAGMENT_LIST_BOX, FragmentList.GetContentBytes());
            return dst.ToArray();
        }

        public override string ToString() =>
            $"Fragment Table Box: {(FragmentList?.Fragments.Count ?? 0)} fragment(s)";
    }

    /// <summary>
    /// Represents a Cross Reference box ('cref') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// A Cross Reference box stands in for a box whose data is stored elsewhere; it carries
    /// a Fragment List box that locates the referenced data.
    /// </summary>
    public class CrossReferenceBox
    {
        /// <summary>Gets or sets the Fragment List box that locates the referenced data.</summary>
        public FragmentListBox? FragmentList { get; set; }

        /// <summary>Parses a Cross Reference box from its content bytes (excluding header).</summary>
        public static CrossReferenceBox Parse(byte[] content)
        {
            var box = new CrossReferenceBox();
            foreach (var child in Jp2BoxIO.ParseChildren(content, 0, content.Length))
            {
                if (child.BoxType == FileFormatBoxes.FRAGMENT_LIST_BOX)
                {
                    box.FragmentList = FragmentListBox.Parse(child.Content);
                    break;
                }
            }
            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes()
        {
            var dst = new List<byte>();
            if (FragmentList != null)
                Jp2BoxIO.WriteBox(dst, FileFormatBoxes.FRAGMENT_LIST_BOX, FragmentList.GetContentBytes());
            return dst.ToArray();
        }

        public override string ToString() =>
            $"Cross Reference Box: {(FragmentList?.Fragments.Count ?? 0)} fragment(s)";
    }

    /// <summary>
    /// Represents a Codestream Header box ('jpch') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// This superbox carries header boxes (image header, bits-per-component, palette,
    /// component mapping, etc.) that apply to an individual codestream in a multi-codestream
    /// JPX file. Its children are preserved as raw boxes for full round-trip fidelity.
    /// </summary>
    public class CodestreamHeaderBox
    {
        /// <summary>Gets the child boxes contained in this header superbox.</summary>
        public List<Jp2BoxData> Children { get; } = new List<Jp2BoxData>();

        /// <summary>Parses a Codestream Header box from its content bytes (excluding header).</summary>
        public static CodestreamHeaderBox Parse(byte[] content)
        {
            var box = new CodestreamHeaderBox();
            box.Children.AddRange(Jp2BoxIO.ParseChildren(content, 0, content.Length));
            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes() => Jp2BoxIO.WriteChildren(Children);

        public override string ToString() => $"Codestream Header Box: {Children.Count} child box(es)";
    }

    /// <summary>
    /// Represents a Compositing Layer Header box ('jplh') from JPEG 2000 Part 2 (ISO/IEC 15444-2).
    /// This superbox carries the boxes (colour group, channel definitions, opacity, etc.) that
    /// describe a single compositing layer. Its children are preserved as raw boxes for full
    /// round-trip fidelity.
    /// </summary>
    public class CompositingLayerHeaderBox
    {
        /// <summary>Gets the child boxes contained in this header superbox.</summary>
        public List<Jp2BoxData> Children { get; } = new List<Jp2BoxData>();

        /// <summary>Parses a Compositing Layer Header box from its content bytes (excluding header).</summary>
        public static CompositingLayerHeaderBox Parse(byte[] content)
        {
            var box = new CompositingLayerHeaderBox();
            box.Children.AddRange(Jp2BoxIO.ParseChildren(content, 0, content.Length));
            return box;
        }

        /// <summary>Serializes this box to its content bytes (excluding header).</summary>
        public byte[] GetContentBytes() => Jp2BoxIO.WriteChildren(Children);

        public override string ToString() => $"Compositing Layer Header Box: {Children.Count} child box(es)";
    }
}
