// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.entropy.encoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.roi.encoder;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.analysis;
using System;

namespace CoreJ2K.j2k.codestream.writer
{

    /// <summary> This class writes almost of the markers and marker segments in main header
    /// and in tile-part headers. It is created by the run() method of the Encoder
    /// instance.
    /// 
    /// A marker segment includes a marker and eventually marker segment
    /// parameters. It is designed by the three letter code of the marker
    /// associated with the marker segment. JPEG 2000 part I defines 6 types of
    /// markers:
    /// <ul> 
    /// <li>Delimiting : SOC,SOT,SOD,EOC (written in FileCodestreamWriter).</li>
    /// <li>Fixed information: SIZ.</li> 
    /// <li>Functional: COD,COC,RGN,QCD,QCC,POC.</li>
    /// <li> In bit-stream: SOP,EPH.</li>
    /// <li> Pointer: TLM,PLM,PLT,PPM,PPT.</li> 
    /// <li> Informational: CRG,COM.</li>
    /// </ul>
    /// 
    /// Main Header is written when Encoder instance calls encodeMainHeader
    /// whereas tile-part headers are written when the EBCOTRateAllocator instance
    /// calls encodeTilePartHeader.
    /// 
    /// </summary>
    /// <seealso cref="Encoder" />
    /// <seealso cref="Markers" />
    /// <seealso cref="EBCOTRateAllocator" />
    public class HeaderEncoder
    {
        /// <summary> Returns the parameters that are used in this class and implementing
        /// classes. It returns a 2D String array. Each of the 1D arrays is for a
        /// different option, and they have 3 elements. The first element is the
        /// option name, the second one is the synopsis, the third one is a long
        /// description of what the parameter is and the fourth is its default
        /// value. The synopsis or description may be 'null', in which case it is
        /// assumed that there is no synopsis or description of the option,
        /// respectively. Null may be returned if no options are supported.
        /// 
        /// </summary>
        /// <returns> the options name, their synopsis and their explanation, or null
        /// if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary> Returns the byte-buffer used to store the codestream header.
        /// The returned array may be larger than the valid data; use
        /// <see cref="BufferLength"/> to know how many bytes are valid.
        /// </summary>
        /// <returns> The underlying byte array of the header buffer (no copy).
        /// 
        /// </returns>
        protected internal virtual byte[] Buffer => baos.GetBuffer();

        /// <summary> Returns the length of the header.
        /// 
        /// </summary>
        /// <returns> The length of the header in bytes
        /// 
        /// </returns>
        public virtual int Length => (int)hbuf.BaseStream.Length;

        /// <summary> Returns the number of bytes used in the codestream header's buffer.
        /// 
        /// </summary>
        /// <returns> Header length in buffer (without any header overhead)
        /// 
        /// </returns>
        protected internal virtual int BufferLength => (int)baos.Length;

        /// <summary>The prefix for the header encoder options: 'H' </summary>
        public const char OPT_PREFIX = 'H';

        /// <summary>The list of parameters that are accepted for the header encoder
        /// module. Options for this modules start with 'H'. 
        /// </summary>
        private static readonly string[][] pinfo = { 
            new string[] { "Hjj2000_COM", null, "Writes or not the JJ2000 COM marker in the " + "codestream", "off" }, 
            new string[] { "HCOM", "<Comment 1>[#<Comment 2>[#<Comment3...>]]", "Adds COM marker segments in the codestream. Comments must be " + "separated with '#' and are written into distinct maker segments.", null },
            new string[] { "Htlm", "on|off", "Writes TLM (Tile-part Lengths) markers in the main header " + "for fast random tile access. This requires collecting tile-part " + "lengths during encoding.", "off" },
            new string[] { "Hplt", "on|off", "Writes PLT (Packet Length) markers in tile-part headers " + "for fast packet access. This requires collecting packet lengths " + "during encoding.", "off" },
            new string[] { "Hppm", "on|off", "Writes PPM (Packed Packet headers, Main header) markers " + "for fast packet header access. This stores all packet headers in " + "the main header.", "off" },
            new string[] { "Hppt", "on|off", "Writes PPT (Packed Packet headers, Tile-part header) markers " + "for fast packet header access. This stores packet headers in each " + "tile-part header. Cannot be used with Hppm.", "off" }
        };

        /// <summary>Nominal range bit of the component defining default values in QCD for
        /// main header 
        /// </summary>
        private int defimgn;

        /// <summary>Nominal range bit of the component defining default values in QCD for
        /// tile headers 
        /// </summary>
        private int deftilenr;

        /// <summary>The number of components in the image </summary>
        private readonly int nComp;

        /// <summary>Whether or not to write the JJ2000 COM marker segment </summary>
        private readonly bool enJJ2KMarkSeg = true;

        /// <summary>Other COM marker segments specified in the command line </summary>
        private readonly string otherCOMMarkSeg = null;

        /// <summary>The ByteArrayOutputStream to store header data. This handler is kept
        /// in order to use methods not accessible from a general
        /// DataOutputStream. For the other methods, it's better to use variable
        /// hbuf.
        /// 
        /// </summary>
        /// <seealso cref="hbuf">
        /// </seealso>
        protected internal System.IO.MemoryStream baos;

        /// <summary>The DataOutputStream to store header data. This kind of object is
        /// useful to write short, int, .... It's constructor takes baos as
        /// parameter.
        /// 
        /// </summary>
        /// <seealso cref="baos" />
        protected internal System.IO.BinaryWriter hbuf;

        /// <summary>The image data reader. Source of original data info </summary>
        protected internal ImgData origSrc;

        /// <summary>An array specifying, for each component,if the data was signed or not
        /// 
        /// </summary>
        protected internal bool[] isOrigSig;

        /// <summary>Reference to the rate allocator </summary>
        protected internal PostCompRateAllocator ralloc;

        /// <summary>Reference to the DWT module </summary>
        protected internal ForwardWT dwt;

        /// <summary>Reference to the tiler module </summary>
        protected internal Tiler tiler;

        /// <summary>Reference to the ROI module </summary>
        protected internal ROIScaler roiSc;

        /// <summary>The encoder specifications </summary>
        protected internal EncoderSpecs encSpec;

        // Marker writers
        private readonly markers.CODMarkerWriter codWriter;
        private readonly markers.COCMarkerWriter cocWriter;
        private readonly markers.QCDMarkerWriter qcdWriter;
        private readonly markers.QCCMarkerWriter qccWriter;
        private readonly markers.SIZMarkerWriter sizWriter;
        private readonly markers.POCMarkerWriter pocWriter;
        private readonly markers.RGNMarkerWriter rgnWriter;
        private readonly markers.COMMarkerWriter comWriter;
        private readonly markers.SOTMarkerWriter sotWriter;

        // Section managers
        private readonly MainHeaderWriter mainHeaderWriter;
        private readonly TileHeaderWriter tileHeaderWriter;

        /// <summary>TLM data collected during encoding for writing TLM markers</summary>
        private metadata.TilePartLengthsData tlmData;

        /// <summary>Whether or not to write TLM markers</summary>
        private readonly bool useTLM;

        /// <summary>PLT data collected during encoding for writing PLT markers</summary>
        private metadata.PacketLengthsData pltData;

        /// <summary>Whether or not to write PLT markers</summary>
        private readonly bool usePLT;

        /// <summary>PPM data collected during encoding for writing PPM markers</summary>
        private System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>> ppmData;

        /// <summary>Whether or not to write PPM markers</summary>
        private readonly bool usePPM;

        /// <summary>PPT data collected during encoding for writing PPT markers</summary>
        private System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>> pptData;

        /// <summary>Whether or not to write PPT markers</summary>
        private readonly bool usePPT;

        /// <summary>
        /// Non-linearity point transformation (NLT) marker segments to emit in the main
        /// header (ISO/IEC 15444-2). When non-empty, the SIZ Rsiz field is set to signal
        /// Part 2 extensions and a CAP marker is written. Null/empty for Part 1 output.
        /// </summary>
        public System.Collections.Generic.IList<NLTMarkerSegment> NLTSegments { get; set; }

        /// <summary>
        /// Variable DC offset (DCO) marker segment to emit (ISO/IEC 15444-2). When non-null,
        /// triggers Part 2 capability signaling. Null for Part 1 output.
        /// </summary>
        public DCOMarkerSegment DcoSegment { get; set; }

        /// <summary>Multiple Component Transform array (MCT) marker segments to emit (Part 2).</summary>
        public System.Collections.Generic.IList<MctArrayMarkerSegment> MctArrays { get; set; }

        /// <summary>Multiple Component Collection (MCC) marker segments to emit (Part 2).</summary>
        public System.Collections.Generic.IList<MccMarkerSegment> MccSegments { get; set; }

        /// <summary>Multiple Component transformation Ordering (MCO) marker segment to emit (Part 2).</summary>
        public McoMarkerSegment McoSegment { get; set; }

        /// <summary>Component Bit Depth (CBD) marker segment to emit (Part 2).</summary>
        public CbdMarkerSegment CbdSegment { get; set; }

        /// <summary>
        /// Arbitrary Transformation Kernel (ATK) marker segment to emit (Part 2). When
        /// non-null, triggers Part 2 capability signaling; the COD/COC transformation
        /// bytes reference the kernel through the custom analysis filters' FilterType.
        /// Null for Part 1 output.
        /// </summary>
        public AtkMarkerSegment AtkSegment { get; set; }

        /// <summary> Initializes the header writer with the references to the coding chain.
        /// 
        /// </summary>
        /// <param name="origsrc">The original image data (before any component mixing,
        /// tiling, etc.)
        /// 
        /// </param>
        /// <param name="isorigsig">An array specifying for each component if it was
        /// originally signed or not.
        /// 
        /// </param>
        /// <param name="dwt">The discrete wavelet transform module.
        /// 
        /// </param>
        /// <param name="tiler">The tiler module.
        /// 
        /// </param>
        /// <param name="encSpec">The encoder specifications
        /// 
        /// </param>
        /// <param name="roiSc">The ROI scaler module.
        /// 
        /// </param>
        /// <param name="ralloc">The post compression rate allocator.
        /// 
        /// </param>
        /// <param name="pl">ParameterList instance.
        /// 
        /// </param>
        public HeaderEncoder(ImgData origsrc, bool[] isorigsig, ForwardWT dwt, Tiler tiler, EncoderSpecs encSpec, ROIScaler roiSc, PostCompRateAllocator ralloc, ParameterList pl)
        {
            pl.checkList(OPT_PREFIX, ParameterList.toNameArray(pinfo));
            if (origsrc.NumComps != isorigsig.Length)
            {
                throw new ArgumentException();
            }
            origSrc = origsrc;
            isOrigSig = isorigsig;
            this.dwt = dwt;
            this.tiler = tiler;
            this.encSpec = encSpec;
            this.roiSc = roiSc;
            this.ralloc = ralloc;

            baos = new System.IO.MemoryStream();
            hbuf = new Util.EndianBinaryWriter(baos, true);
            nComp = origsrc.NumComps;
            enJJ2KMarkSeg = pl.GetBooleanParameter("Hjj2000_COM");
            otherCOMMarkSeg = pl.GetParameter("HCOM");
            useTLM = pl.GetBooleanParameter("Htlm");
            usePLT = pl.GetBooleanParameter("Hplt");
            usePPM = pl.GetBooleanParameter("Hppm");
            usePPT = pl.GetBooleanParameter("Hppt");

            // Validate PPM/PPT usage (can't use both)
            if (usePPM && usePPT)
            {
                throw new ArgumentException("Cannot use both PPM (Hppm) and PPT (Hppt) markers. Choose one or neither.");
            }

            // Initialize data structures for PPM/PPT
            if (usePPM)
            {
                ppmData = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>>();
            }
            if (usePPT)
            {
                pptData = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>>();
            }

            // Initialize marker writers
            codWriter = new markers.CODMarkerWriter(encSpec, dwt, ralloc);
            cocWriter = new markers.COCMarkerWriter(encSpec, dwt, nComp);
            qcdWriter = new markers.QCDMarkerWriter(encSpec, dwt);
            qccWriter = new markers.QCCMarkerWriter(encSpec, dwt, nComp);
            sizWriter = new markers.SIZMarkerWriter(origSrc, isOrigSig, tiler, nComp);
            pocWriter = new markers.POCMarkerWriter(encSpec, nComp);
            rgnWriter = new markers.RGNMarkerWriter(encSpec, nComp);
            comWriter = new markers.COMMarkerWriter(enJJ2KMarkSeg, otherCOMMarkSeg);
            sotWriter = new markers.SOTMarkerWriter();

            // Initialize section managers
            mainHeaderWriter = new MainHeaderWriter(encSpec, dwt, nComp);
            tileHeaderWriter = new TileHeaderWriter(encSpec, dwt, nComp);
        }

        /// <summary> Resets the contents of this HeaderEncoder to its initial state. It
        /// erases all the data in the header buffer and reactualizes the
        /// headerLength field of the bit stream writer.
        /// 
        /// </summary>
        public virtual void reset()
        {
            // CONVERSION PROBLEM?
            //baos.reset();
            baos.SetLength(0);
            hbuf = new Util.EndianBinaryWriter(baos, true); //new System.IO.BinaryWriter(baos);
        }

        /// <summary> Writes the header to the specified BinaryDataOutput.
        /// 
        /// </summary>
        /// <param name="out">Where to write the header.
        /// 
        /// </param>
        public virtual void writeTo(BinaryDataOutput outStream)
        {
            int i, len;
            byte[] buf;

            buf = Buffer;
            len = Length;

            for (i = 0; i < len; i++)
            {
                outStream.writeByte(buf[i]);
            }
        }

        /// <summary> Writes the header to the specified OutputStream.
        /// 
        /// </summary>
        /// <param name="out">Where to write the header.
        /// 
        /// </param>
        public virtual void writeTo(System.IO.Stream outStream)
        {
            outStream.Write(Buffer, 0, BufferLength);
        }

        /// <summary> Start Of Codestream marker (SOC) signalling the beginning of a
        /// codestream.
        /// 
        /// </summary>
        private void writeSOC()
        {
            hbuf.Write(Markers.SOC);
        }

        /// <summary> Writes TLM marker segment(s) in the main header.
        /// TLM markers contain tile-part lengths for fast random tile access.
        /// 
        /// Multiple TLM markers may be written if there are many tiles.
        /// 
        /// </summary>
        /// <param name="tlm">The TLM data to write
        /// 
        /// </param>
        protected internal virtual void writeTLM(CoreJ2K.j2k.codestream.metadata.TilePartLengthsData tlm)
        {
            TLMMarkerWriter.WriteTLM(hbuf, tlm);
        }

        /// <summary> Writes PLM marker segment(s) in the main header.
        /// PLM markers contain packet lengths for all tiles, allowing decoders to
        /// quickly locate packets without parsing packet headers.
        /// 
        /// Multiple PLM markers may be written if there are many packets.
        /// 
        /// </summary>
        /// <param name="plm">The PLM data to write
        /// 
        /// </param>
        protected internal virtual void writePLM(CoreJ2K.j2k.codestream.metadata.PacketLengthsData plm)
        {
            PLMMarkerWriter.WritePLM(hbuf, plm);
        }

        /// <summary>
        /// Gets whether TLM markers should be written.
        /// </summary>
        public virtual bool IsTLMEnabled => useTLM;

        /// <summary>
        /// Sets the TLM data to be written in the main header.
        /// This should be called after all tiles have been encoded.
        /// </summary>
        /// <param name="tlm">The collected tile-part length data</param>
        public virtual void SetTLMData(metadata.TilePartLengthsData tlm)
        {
            tlmData = tlm;
        }

        /// <summary>
        /// Gets whether PLT markers should be written.
        /// </summary>
        public virtual bool IsPLTEnabled => usePLT;

        /// <summary>
        /// Sets the PLT data to be written in the tile-part headers.
        /// This should be called before encoding each tile-part header.
        /// </summary>
        /// <param name="plt">The collected packet length data</param>
        public virtual void SetPLTData(metadata.PacketLengthsData plt)
        {
            pltData = plt;
        }

        /// <summary>
        /// Gets whether PPM markers should be written.
        /// </summary>
        public virtual bool IsPPMEnabled => usePPM;

        /// <summary>
        /// Adds a packet header to the PPM data for the main header.
        /// </summary>
        /// <param name="tileIdx">The tile index</param>
        /// <param name="packetHeader">The packet header data</param>
        public virtual void AddPPMPacketHeader(int tileIdx, byte[] packetHeader)
        {
            if (!usePPM)
                return;

            if (!ppmData.ContainsKey(tileIdx))
            {
                ppmData[tileIdx] = new System.Collections.Generic.List<byte[]>();
            }
            ppmData[tileIdx].Add(packetHeader);
        }

        /// <summary>
        /// Sets the PPM data to be written in the main header.
        /// This should be called after all packets have been encoded.
        /// </summary>
        /// <param name="ppm">The collected packet header data</param>
        public virtual void SetPPMData(System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>> ppm)
        {
            ppmData = ppm;
        }

        /// <summary>
        /// Gets whether PPT markers should be written.
        /// </summary>
        public virtual bool IsPPTEnabled => usePPT;

        /// <summary>
        /// Adds a packet header to the PPT data for a tile-part header.
        /// </summary>
        /// <param name="tileIdx">The tile index</param>
        /// <param name="packetHeader">The packet header data</param>
        public virtual void AddPPTPacketHeader(int tileIdx, byte[] packetHeader)
        {
            if (!usePPT)
                return;

            if (!pptData.ContainsKey(tileIdx))
            {
                pptData[tileIdx] = new System.Collections.Generic.List<byte[]>();
            }
            pptData[tileIdx].Add(packetHeader);
        }

        /// <summary>
        /// Sets the PPT data to be written in tile-part headers.
        /// This should be called before encoding each tile-part header.
        /// </summary>
        /// <param name="tileIdx">The tile index</param>
        /// <param name="ppt">The collected packet header data for this tile</param>
        public virtual void SetPPTData(int tileIdx, System.Collections.Generic.List<byte[]> ppt)
        {
            if (!usePPT)
                return;

            pptData[tileIdx] = ppt;
        }

        /// <summary>
        /// Writes PPM marker segment(s) in the main header.
        /// PPM markers contain packet headers for all tiles, allowing decoders to
        /// quickly locate packets without parsing packet headers in tile-parts.
        /// </summary>
        /// <param name="ppm">The PPM data to write</param>
        protected internal virtual void writePPM(System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<byte[]>> ppm)
        {
            PPMMarkerWriter.WritePPM(hbuf, ppm);
        }

        /// <summary>
        /// Writes PPT marker segment(s) in a tile-part header.
        /// PPT markers contain packet headers for this tile-part, allowing decoders to
        /// quickly locate packets without parsing packet headers in packet bodies.
        /// </summary>
        /// <param name="tileIdx">The tile index</param>
        /// <param name="ppt">The PPT data to write</param>
        protected internal virtual void writePPT(int tileIdx, System.Collections.Generic.List<byte[]> ppt)
        {
            PPTMarkerWriter.WritePPT(hbuf, ppt);
        }

        /// <summary> Write main header. JJ2000 main header corresponds to the following
        /// sequence of marker segments:
        /// 
        /// <ol>
        /// <li>SOC</li>
        /// <li>SIZ</li>
        /// <li>COD</li>
        /// <li>COC (if needed)</li>
        /// <li>QCD</li>
        /// <li>QCC (if needed)</li>
        /// <li>POC (if needed)</li>
        /// </ol>
        /// 
        /// </summary>
        public virtual void encodeMainHeader()
        {
            // +---------------------------------+
            // |    SOC marker segment           |
            // +---------------------------------+
            writeSOC();

            // Part 2: signal extended capabilities in Rsiz when any Part 2 marker is present.
            var hasNlt = NLTSegments != null && NLTSegments.Count > 0;
            var hasMct = (MctArrays != null && MctArrays.Count > 0)
                         || (MccSegments != null && MccSegments.Count > 0)
                         || McoSegment != null;
            var hasDco = DcoSegment != null;
            var hasAtk = AtkSegment != null;
            var hasPart2 = hasNlt || hasMct || hasDco || hasAtk;
            if (hasPart2)
            {
                sizWriter.Rsiz = Markers.RSIZ_EXTENSIONS;
            }

            // +---------------------------------+
            // |    Image and tile SIZe (SIZ)    |
            // +---------------------------------+
            sizWriter.Write(hbuf);

            // +---------------------------------+
            // |  Extended CAPabilities (CAP)    |  (Part 2)
            // +---------------------------------+
            if (hasPart2)
            {
                writeCAP();
            }

            // +-------------------------------------------+
            // |  Arbitrary Transformation Kernel (ATK)     |  (Part 2)
            // +-------------------------------------------+
            // Written before COD so the transformation byte's kernel reference resolves.
            if (hasAtk)
            {
                AtkSegment.Write(hbuf);
            }

            // +-------------------------------------------+
            // |  Multiple Component Transform (MCT family) |  (Part 2)
            // +-------------------------------------------+
            if (hasMct)
            {
                if (CbdSegment != null) CbdSegment.Write(hbuf);
                if (MctArrays != null)
                    foreach (var arr in MctArrays) arr.Write(hbuf);
                if (MccSegments != null)
                    foreach (var mcc in MccSegments) mcc.Write(hbuf);
                if (McoSegment != null) McoSegment.Write(hbuf);
            }

            // +-------------------------------+
            // |   COding style Default (COD)  |
            // +-------------------------------+
            var isEresUsed = ((string)encSpec.tts.GetDefault()).Equals("predict");
            codWriter.Write(hbuf, true, 0, nComp);

            // +---------------------------------+
            // |   COding style Component (COC)  |
            // +---------------------------------+
            for (int i = 0; i < nComp; i++)
            {
                if (mainHeaderWriter.ShouldWriteCOC(i, isEresUsed))
                {
                    cocWriter.Write(hbuf, true, 0, i);
                }
            }

            // +-------------------------------+
            // |   Quantization Default (QCD)  |
            // +-------------------------------+
            defimgn = qcdWriter.WriteMain(hbuf);

            // +-------------------------------+
            // | Quantization Component (QCC)  |
            // +-------------------------------+
            for (int i = 0; i < nComp; i++)
            {
                if (mainHeaderWriter.ShouldWriteQCC(i, defimgn))
                {
                    qccWriter.WriteMain(hbuf, i);
                }
            }

            // +--------------------------+
            // |    POC maker segment     |
            // +--------------------------+
            if (mainHeaderWriter.ShouldWritePOC())
            {
                pocWriter.Write(hbuf, true, 0);
            }

            // +--------------------------+
            // |    TLM marker segment    |
            // +--------------------------+
            if (tlmData != null && tlmData.HasTilePartLengths)
            {
                writeTLM(tlmData);
            }

            // +--------------------------+
            // |    PLT marker segment    |
            // +--------------------------+
            if (pltData != null && pltData.HasPacketLengths)
            {
                writePLM(pltData);
            }

            // +--------------------------+
            // |    PPM marker segment    |
            // +--------------------------+
            if (ppmData != null && ppmData.Count > 0)
            {
                writePPM(ppmData);
            }

            // +-------------------------------+
            // |  Variable DC offset (DCO)     |  (Part 2)
            // +-------------------------------+
            if (hasDco)
            {
                DcoSegment.Write(hbuf);
            }

            // +--------------------------------------+
            // |  Non-Linearity point transform (NLT) |  (Part 2)
            // +--------------------------------------+
            if (hasNlt)
            {
                foreach (var nlt in NLTSegments)
                {
                    nlt.Write(hbuf);
                }
            }

            // +--------------------------+
            // |    Comments (COM)       |
            // +--------------------------+
            comWriter.Write(hbuf);
        }

        /// <summary>
        /// Writes a minimal CAP (extended capabilities) marker segment (ISO/IEC 15444-2)
        /// declaring that the codestream uses JPEG 2000 Part 2 capabilities. The Part bit
        /// in Pcap follows the bit-(32 - part) convention; a single zero-valued Ccap field
        /// accompanies it.
        /// </summary>
        private void writeCAP()
        {
            // Pcap bit for Part 2 (bit numbered 32 - part, counting the MSB as bit 1).
            const uint pcap = 1u << (32 - 2);

            hbuf.Write(Markers.CAP);     // CAP marker
            hbuf.Write((short)8);        // Lcap = Lcap(2) + Pcap(4) + one Ccap(2)
            hbuf.Write(pcap);            // Pcap
            hbuf.Write((short)0);        // Ccap for Part 2 (no specific sub-capabilities flagged)
        }

        /// <summary> Writes tile-part header. JJ2000 tile-part header corresponds to the
        /// following sequence of marker segments:
        /// 
        /// <ol> 
        /// <li>SOT</li> 
        /// <li>COD (if needed)</li> 
        /// <li>COC (if needed)</li> 
        /// <li>QCD (if needed)</li> 
        /// <li>QCC (if needed)</li> 
        /// <li>RGN (if needed)</li> 
        /// <li>POC (if needed)</li>
        /// <li>SOD</li> 
        /// </ol>
        /// 
        /// </summary>
        /// <param name="length">The length of the current tile-part.
        /// 
        /// </param>
        /// <param name="tileIdx">Index of the tile to write
        /// 
        /// </param>
        public virtual void encodeTilePartHeader(int tileLength, int tileIdx)
        {
            var numTiles = ralloc.GetNumTiles(null);
            ralloc.SetTile(tileIdx % numTiles.x, tileIdx / numTiles.x);

            // +--------------------------+
            // |    SOT maker segment     |
            // +--------------------------+
            sotWriter.Write(hbuf, tileIdx, tileLength);

            // +--------------------------+
            // |    COD maker segment     |
            // +--------------------------+
            var isEresUsed = ((string)encSpec.tts.GetDefault()).Equals("predict");
            var tileCODwritten = false;
            
            if (tileHeaderWriter.ShouldWriteCOD(tileIdx, isEresUsed))
            {
                codWriter.Write(hbuf, false, tileIdx, nComp);
                tileCODwritten = true;
            }

            // +--------------------------+
            // |    COC maker segment     |
            // +--------------------------+
            for (var c = 0; c < nComp; c++)
            {
                if (tileHeaderWriter.ShouldWriteCOC(tileIdx, c, isEresUsed, tileCODwritten))
                {
                    cocWriter.Write(hbuf, false, tileIdx, c);
                }
            }

            // +--------------------------+
            // |    QCD maker segment     |
            // +--------------------------+
            var tileQCDwritten = false;
            if (tileHeaderWriter.ShouldWriteQCD(tileIdx))
            {
                deftilenr = qcdWriter.WriteTile(hbuf, tileIdx, deftilenr);
                tileQCDwritten = true;
            }
            else
            {
                deftilenr = defimgn;
            }

            // +--------------------------+
            // |    QCC maker segment     |
            // +--------------------------+
            for (var c = 0; c < nComp; c++)
            {
                if (tileHeaderWriter.ShouldWriteQCC(tileIdx, c, deftilenr, tileQCDwritten))
                {
                    qccWriter.WriteTile(hbuf, tileIdx, c);
                }
            }

            // +--------------------------+
            // |    RGN maker segment     |
            // +--------------------------+
            if (roiSc.useRoi() && (!roiSc.BlockAligned))
            {
                rgnWriter.Write(hbuf, tileIdx);
            }

            // +--------------------------+
            // |    POC maker segment     |
            // +--------------------------+
            if (tileHeaderWriter.ShouldWritePOC(tileIdx))
            {
                pocWriter.Write(hbuf, false, tileIdx);
            }

            // +--------------------------+
            // |    PLT marker segment    |
            // +--------------------------+
            if (pltData != null && pltData.GetPacketCount(tileIdx) > 0)
            {
                PLTMarkerWriter.WritePLT(hbuf.BaseStream, pltData, tileIdx, 0);
            }

            // +--------------------------+
            // |    PPT marker segment    |
            // +--------------------------+
            if (pptData != null && pptData.ContainsKey(tileIdx) && pptData[tileIdx].Count > 0)
            {
                writePPT(tileIdx, pptData[tileIdx]);
            }

            // +--------------------------+
            // |         SOD marker        |
            // +--------------------------+
            hbuf.Write(unchecked((byte)(Markers.SOD >>> 8)));
            hbuf.Write((byte)(Markers.SOD & 0x00FF));
        }
    }
}