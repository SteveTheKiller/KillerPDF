using CoreJ2K.j2k.codestream.metadata;
using CoreJ2K.j2k.decoder;
using CoreJ2K.j2k.entropy;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.io;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.synthesis;
/// <summary> CVS identifier:
/// 
/// 
/// Class:                   PktDecoder
/// 
/// Description:             Reads packets heads and keeps location of
/// code-blocks' codewords
/// 
/// 
/// 
/// COPYRIGHT:
/// 
/// This software module was originally developed by Rapha�l Grosbois and
/// Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
/// Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
/// Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
/// Centre France S.A) in the course of development of the JPEG2000
/// standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
/// software module is an implementation of a part of the JPEG 2000
/// Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
/// Systems AB and Canon Research Centre France S.A (collectively JJ2000
/// Partners) agree not to assert against ISO/IEC and users of the JPEG
/// 2000 Standard (Users) any of their rights under the copyright, not
/// including other intellectual property rights, for this software module
/// with respect to the usage by ISO/IEC and Users of this software module
/// or modifications thereof for use in hardware or software products
/// claiming conformance to the JPEG 2000 Standard. Those intending to use
/// this software module in hardware or software products are advised that
/// their use may infringe existing patents. The original developers of
/// this software module, JJ2000 Partners and ISO/IEC assume no liability
/// for use of this software module or modifications thereof. No license
/// or right to this software module is granted for non JPEG 2000 Standard
/// conforming products. JJ2000 Partners have full right to use this
/// software module for his/her own purpose, assign or donate this
/// software module to any third party and to inhibit third parties from
/// using this software module for non JPEG 2000 Standard conforming
/// products. This copyright notice must be included in all copies or
/// derivative works of this software module.
/// 
/// Copyright (c) 1999/2000 JJ2000 Partners.
/// 
/// </summary>
using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreJ2K.j2k.codestream.reader
{

    /// <summary> This class is used to read packet's head and body. All the members must be
    /// re-initialized at the beginning of each tile thanks to the restart()
    /// method.
    /// 
    /// </summary>
    public class PktDecoder
    {

        /// <summary>Reference to the codestream reader agent </summary>
        private readonly BitstreamReaderAgent src;

        /// <summary>Flag indicating whether packed packet header was used for this tile </summary>
        private bool pph = false;

        /// <summary>The packed packet header if it was used </summary>
        private System.IO.MemoryStream pphbais;

        /// <summary>Reference to decoder specifications </summary>
        private readonly DecoderSpecs decSpec;

        /// <summary>Reference to the HeaderDecoder </summary>
        private readonly HeaderDecoder hd;

        /// <summary>Initial value of the state variable associated with code-block
        /// length.</summary>
        private readonly int INIT_LBLOCK = 3;

        /// <summary>The wrapper to read bits for the packet heads </summary>
        private readonly PktHeaderBitReader bin;

        /// <summary>Reference to the stream where to read from </summary>
        private readonly RandomAccessIO ehs;

        /// <summary> Maximum number of precincts :
        /// 
        /// <ul>
        /// <li> 1st dim: component index.</li>
        /// <li> 2nd dim: resolution level index.</li>
        /// </ul>
        /// 
        /// </summary>
        private Coord[][] numPrec;

        /// <summary>Index of the current tile </summary>
        private int tIdx;

        /// <summary> Array containing the coordinates, width, height, indexes, ... of the
        /// precincts in the current tile:
        /// 
        /// <ul>
        /// <li> 1st dim: component index.</li>
        /// <li> 2nd dim: resolution level index.</li>
        /// <li> 3rd dim: precinct index.</li>
        /// </ul>
        /// 
        /// </summary>
        private PrecInfo[][][] ppinfo;

        /// <summary> Lblock value used to read code size information in each packet head:
        /// 
        /// <ul>
        /// <li> 1st dim: component index.</li>
        /// <li> 2nd dim: resolution level index.</li>
        /// <li> 3rd dim: subband index.</li>
        /// <li> 4th/5th dim: code-block index (vert. and horiz.).</li>
        /// </ul> 
        /// 
        /// </summary>
        private int[][][][][] lblock;

        /// <summary> Tag tree used to read inclusion informations in packet's head:
        /// 
        /// <ul>   
        /// <li> 1st dim: component index.</li>
        /// <li> 2nd dim: resolution level index.</li>
        /// <li> 3rd dim: precinct index.</li> 
        /// <li> 4th dim: subband index.</li>
        /// 
        /// </summary>
        private TagTreeDecoder[][][][] ttIncl;

        /// <summary> Tag tree used to read bit-depth information in packet's head:
        /// 
        /// <ul>
        /// <li> 1st dim: component index.</li>
        /// <li> 2nd dim: resolution level index.</li>
        /// <li> 3rd dim: precinct index.</li>
        /// <li> 4th dim: subband index.</li>
        /// </ul>
        /// 
        /// </summary>
        private TagTreeDecoder[][][][] ttMaxBP;

        /// <summary>Number of layers in the current tile </summary>
        private int nl = 0;

        /// <summary>The number of components </summary>
        private int nc;

        /// <summary>Whether SOP marker segment are used </summary>
        private bool sopUsed = false;

        /// <summary>Whether EPH marker are used </summary>
        private bool ephUsed = false;

        /// <summary>Index of the current packet in the tile. Used with SOP marker segment
        /// 
        /// </summary>
        private int pktIdx;

        /// <summary>List of code-blocks found in last read packet head (one list
        /// per subband) 
        /// </summary>
        private List<CBlkCoordInfo>[] cblks;

        /// <summary>Number of codeblocks encountered. used for ncb quit condition</summary>
        private int ncb;

        /// <summary>Maximum number of codeblocks to read before ncb quit condition is
        /// reached 
        /// </summary>
        private readonly int maxCB;

        /// <summary>Flag indicating whether ncb quit condition has been reached </summary>
        private bool ncbQuit;

        /// <summary>The tile in which the ncb quit condition was reached </summary>
        private int tQuit;

        /// <summary>The component in which the ncb quit condition was reached </summary>
        private int cQuit;

        /// <summary>The subband in which the ncb quit condition was reached </summary>
        private int sQuit;

        /// <summary>The resolution in which the ncb quit condition was reached </summary>
        private int rQuit;

        /// <summary>The x position of the last code block before ncb quit reached </summary>
        private int xQuit;

        /// <summary>The y position of the last code block before ncb quit reached  </summary>
        private int yQuit;

        /// <summary>True if truncation mode is used. False if it is parsing mode </summary>
        private readonly bool isTruncMode;

        /// <summary>Cached decomposition levels from the previous tile, used to skip
        /// scaffold reallocation when the tile geometry is identical.</summary>
        private int[] _prevMdl;

        /// <summary>Cached numPrec grid from the previous tile for geometry comparison.</summary>
        private Coord[][] _prevNumPrec;

        /// <summary>Cached tile-component dimensions from the previous tile. A partial (edge) tile
        /// can share <see cref="_prevMdl"/> and <see cref="_prevNumPrec"/> with a full tile yet be
        /// smaller, so reusing the code-block scaffold would carry stale (full-tile) code-block
        /// geometry. These guard the reuse against that.</summary>
        private int[] _prevTCW;
        private int[] _prevTCH;

        /// <summary>PLT (Packet Length) marker segment data for fast packet access</summary>
        private readonly PacketLengthsData pltData;

        /// <summary>Current packet index within the current tile (for PLT lookup)</summary>
        private int currentPacketIndex;

        /// <summary>Whether PLT fast-path is available and should be used</summary>
        private bool usePLTFastPath;

        /// <summary> Creates an empty PktDecoder object associated with given decoder
        /// specifications and HeaderDecoder. This object must be initialized
        /// thanks to the restart method before being used.
        /// 
        /// </summary>
        /// <param name="decSpec">The decoder specifications.</param>
        /// <param name="hd">The HeaderDecoder instance.</param>
        /// <param name="ehs">The stream where to read data from.</param>
        /// <param name="src">The bit stream reader agent.</param>
        /// <param name="isTruncMode">Whether truncation mode is required.</param>
        /// <param name="maxCB">The maximum number of code-blocks to read before ncbquit</param>
        public PktDecoder(DecoderSpecs decSpec, HeaderDecoder hd, RandomAccessIO ehs, BitstreamReaderAgent src, bool isTruncMode, int maxCB)
        {
            this.decSpec = decSpec;
            this.hd = hd;
            this.ehs = ehs;
            this.isTruncMode = isTruncMode;
            bin = new PktHeaderBitReader(ehs);
            this.src = src;
            ncb = 0;
            ncbQuit = false;
            this.maxCB = maxCB;

            this.pltData = hd.GetPLTData();
            this.usePLTFastPath = (pltData != null && pltData.HasPacketLengths);
            this.currentPacketIndex = 0;

            if (usePLTFastPath)
            {
                FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                    "PLT markers available - fast packet access enabled");
            }
        }


        /// <summary> Re-initialize the PktDecoder instance at the beginning of a new tile.
        /// 
        /// </summary>
        /// <param name="nc">The number of components in this tile
        /// 
        /// </param>
        /// <param name="mdl">The maximum number of decomposition level in each component
        /// of this tile
        /// 
        /// </param>
        /// <param name="nl">The number of layers in  this tile
        /// 
        /// </param>
        /// <param name="cbI">The code-blocks array
        /// 
        /// </param>
        /// <param name="pph">Flag indicating whether packed packet headers was used
        /// 
        /// </param>
        /// <param name="pphbais">Stream containing the packed packet headers
        /// 
        /// </param>
        public virtual CBlkInfo[][][][][] restart(int nc, int[] mdl, int nl, CBlkInfo[][][][][] cbI, bool pph, System.IO.MemoryStream pphbais)
        {
            this.nc = nc;
            this.nl = nl;
            tIdx = src.TileIdx;
            this.pph = pph;
            this.pphbais = pphbais;

            sopUsed = decSpec.sops.GetBoolTileDef(tIdx);
            pktIdx = 0;
            ephUsed = decSpec.ephs.GetBoolTileDef(tIdx);

            currentPacketIndex = 0;

            // Update PLT fast-path availability for this tile
            usePLTFastPath = (pltData != null &&
                              pltData.HasPacketLengths &&
                              pltData.GetPacketCount(tIdx) > 0);

            // Check whether the tile geometry matches the previous tile so we can
            // reuse the TagTreeDecoder scaffold instead of reallocating it.
            bool sameGeometry = (ttIncl != null) && (_prevMdl != null) &&
                                 (numPrec != null) && (_prevNumPrec != null) &&
                                 (_prevMdl.Length == nc) &&
                                 (_prevTCW != null) && (_prevTCH != null) && (_prevTCW.Length == nc);
            if (sameGeometry)
            {
                for (var c2 = 0; c2 < nc && sameGeometry; c2++)
                {
                    if (_prevMdl[c2] != mdl[c2] ||
                        _prevNumPrec[c2].Length != mdl[c2] + 1)
                    {
                        sameGeometry = false;
                        break;
                    }
                    // A partial (edge) tile can match mdl and numPrec but be smaller; the code-block
                    // geometry depends on the actual tile-component size, so it must match too.
                    if (_prevTCW[c2] != src.GetTileCompWidth(tIdx, c2, mdl[c2]) ||
                        _prevTCH[c2] != src.GetTileCompHeight(tIdx, c2, mdl[c2]))
                    {
                        sameGeometry = false;
                        break;
                    }
                    for (var r2 = 0; r2 <= mdl[c2] && sameGeometry; r2++)
                    {
                        if (_prevNumPrec[c2][r2].x != numPrec[c2][r2].x ||
                            _prevNumPrec[c2][r2].y != numPrec[c2][r2].y)
                            sameGeometry = false;
                    }
                }
            }

            if (!sameGeometry)
            {
                cbI = new CBlkInfo[nc][][][][];
                lblock = new int[nc][][][][];
                ttIncl = new TagTreeDecoder[nc][][][];
                ttMaxBP = new TagTreeDecoder[nc][][][];
                numPrec = new Coord[nc][];
                ppinfo = new PrecInfo[nc][][];
            }

            // Used to compute the maximum number of precincts for each resolution
            // level
            int tcx0, tcy0, tcx1, tcy1; // Current tile position in the domain of
                                        // the image component
            int trx0, try0, trx1, try1; // Current tile position in the reduced
                                        // resolution image domain
                                        //int xrsiz, yrsiz; // Component sub-sampling factors

            SubbandSyn root, sb;
            int mins, maxs;
            Coord nBlk = null;
            var cb0x = src.CbULX;
            var cb0y = src.CbULY;

            for (var c = 0; c < nc; c++)
            {
                if (!sameGeometry)
                {
                    cbI[c] = new CBlkInfo[mdl[c] + 1][][][];
                    lblock[c] = new int[mdl[c] + 1][][][];
                    ttIncl[c] = new TagTreeDecoder[mdl[c] + 1][][];
                    ttMaxBP[c] = new TagTreeDecoder[mdl[c] + 1][][];
                    numPrec[c] = new Coord[mdl[c] + 1];
                    ppinfo[c] = new PrecInfo[mdl[c] + 1][];
                }

                // Get the tile-component coordinates on the reference grid
                tcx0 = src.GetResULX(c, mdl[c]);
                tcy0 = src.GetResULY(c, mdl[c]);
                tcx1 = tcx0 + src.GetTileCompWidth(tIdx, c, mdl[c]);
                tcy1 = tcy0 + src.GetTileCompHeight(tIdx, c, mdl[c]);

                for (var r = 0; r <= mdl[c]; r++)
                {
                    // Tile's coordinates in the reduced resolution image domain
                    trx0 = (int)Math.Ceiling(tcx0 / (double)(1 << (mdl[c] - r)));
                    try0 = (int)Math.Ceiling(tcy0 / (double)(1 << (mdl[c] - r)));
                    trx1 = (int)Math.Ceiling(tcx1 / (double)(1 << (mdl[c] - r)));
                    try1 = (int)Math.Ceiling(tcy1 / (double)(1 << (mdl[c] - r)));

                    // Calculate the maximum number of precincts for each
                    // resolution level taking into account tile specific options.
                    double twoppx = GetPPX(tIdx, c, r);
                    double twoppy = GetPPY(tIdx, c, r);
                    if (!sameGeometry)
                    {
                        numPrec[c][r] = new Coord();
                    }
                    if (trx1 > trx0)
                    {
                        numPrec[c][r].x = (int)Math.Ceiling((trx1 - cb0x) / twoppx) - (int)Math.Floor((trx0 - cb0x) / twoppx);
                    }
                    else
                    {
                        numPrec[c][r].x = 0;
                    }
                    if (try1 > try0)
                    {
                        numPrec[c][r].y = (int)Math.Ceiling((try1 - cb0y) / twoppy) - (int)Math.Floor((try0 - cb0y) / twoppy);
                    }
                    else
                    {
                        numPrec[c][r].y = 0;
                    }

                    // First and last subbands indexes
                    mins = (r == 0) ? 0 : 1;
                    maxs = (r == 0) ? 1 : 4;

                    var maxPrec = numPrec[c][r].x * numPrec[c][r].y;

                    if (!sameGeometry)
                    {
                        ttIncl[c][r] = new TagTreeDecoder[maxPrec][];
                        for (var i = 0; i < maxPrec; i++)
                            ttIncl[c][r][i] = new TagTreeDecoder[maxs + 1];
                        ttMaxBP[c][r] = new TagTreeDecoder[maxPrec][];
                        for (var i2 = 0; i2 < maxPrec; i2++)
                            ttMaxBP[c][r][i2] = new TagTreeDecoder[maxs + 1];
                        lblock[c][r] = new int[maxs + 1][][];
                        ppinfo[c][r] = new PrecInfo[maxPrec];
                        cbI[c][r] = new CBlkInfo[maxs + 1][][];
                    }

                    fillPrecInfo(c, r, mdl[c]);

                    root = src.GetSynSubbandTree(tIdx, c);
                    for (var s = mins; s < maxs; s++)
                    {
                        sb = (SubbandSyn)root.GetSubbandByIdx(r, s);
                        nBlk = sb.numCb;

                        // Reuse the existing scaffold only when its code-block grid actually matches
                        // this subband's grid. Even with matching mdl/numPrec, the per-subband grid
                        // can differ (e.g. partial-edge tiles), in which case we must reallocate
                        // rather than index past the previous tile's smaller grid.
                        var gridMatches = sameGeometry
                            && cbI[c][r][s] != null && cbI[c][r][s].Length == nBlk.y
                            && lblock[c][r][s] != null && lblock[c][r][s].Length == nBlk.y
                            && (nBlk.y == 0
                                || (cbI[c][r][s][0] != null && cbI[c][r][s][0].Length == nBlk.x
                                    && lblock[c][r][s][0] != null && lblock[c][r][s][0].Length == nBlk.x));

                        if (!gridMatches)
                        {
                            cbI[c][r][s] = new CBlkInfo[nBlk.y][];
                            for (var i3 = 0; i3 < nBlk.y; i3++)
                            {
                                cbI[c][r][s][i3] = new CBlkInfo[nBlk.x];
                            }
                            lblock[c][r][s] = new int[nBlk.y][];
                            for (var i4 = 0; i4 < nBlk.y; i4++)
                            {
                                lblock[c][r][s][i4] = new int[nBlk.x];
                            }
                        }
                        else
                        {
                            // Reuse scaffold: reset existing CBlkInfo objects in-place
                            // so readPkt can reuse them without allocating new instances.
                            for (var i3 = 0; i3 < nBlk.y; i3++)
                            {
                                var row = cbI[c][r][s][i3];
                                if (row == null) continue;
                                for (var i4 = 0; i4 < nBlk.x; i4++)
                                {
                                    row[i4]?.Reset(nl);
                                }
                            }
                        }

                        for (var i = nBlk.y - 1; i >= 0; i--)
                        {
                            ArrayUtil.intArraySet(lblock[c][r][s][i], INIT_LBLOCK);
                        }
                    } // loop on subbands
                } // End loop on resolution levels
            } // End loop on components

            // Save geometry for next tile comparison
            if (_prevMdl == null || _prevMdl.Length != nc)
                _prevMdl = new int[nc];
            mdl.CopyTo(_prevMdl, 0);
            _prevNumPrec = numPrec;

            if (_prevTCW == null || _prevTCW.Length != nc)
            {
                _prevTCW = new int[nc];
                _prevTCH = new int[nc];
            }
            for (var c = 0; c < nc; c++)
            {
                _prevTCW[c] = src.GetTileCompWidth(tIdx, c, mdl[c]);
                _prevTCH[c] = src.GetTileCompHeight(tIdx, c, mdl[c]);
            }

            return cbI;
        }

        /// <summary>Resets an existing TagTreeDecoder in-place or creates a new one if the
        /// slot is null. Avoids allocation when dimensions are unchanged.</summary>
        private static void ResetOrCreate(ref TagTreeDecoder slot, int h, int w)
        {
            if (slot == null)
                slot = new TagTreeDecoder(h, w);
            else
                slot.Reset(h, w);
        }

        /// <summary>Integer floor division for positive divisor.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int FloorDiv(int a, int b) => a / b - (a % b != 0 && (a ^ b) < 0 ? 1 : 0);

        /// <summary>Integer ceiling division for non-negative numerator and positive divisor.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int CeilDiv(int a, int b) => (a + b - 1) / b;

        /// <summary> Retrieves precincts and code-blocks coordinates in the given resolution,
        /// level and component. Finishes TagTreeEncoder initialization as well.</summary>
        /// <param name="c">Component index.</param>
        /// <param name="r">Resolution level index.</param>
        /// <param name="mdl">Number of decomposition level in component <tt>c</tt>.</param>
        private void fillPrecInfo(int c, int r, int mdl)
        {
            if (ppinfo[c][r].Length == 0)
                return;

            var tileI = src.GetTile(null);
            var nTiles = src.GetNumTiles(null);

            int xt0siz = src.TilePartULX;
            int yt0siz = src.TilePartULY;
            int xtsiz  = src.NomTileWidth;
            int ytsiz  = src.NomTileHeight;
            int x0siz  = hd.ImgULX;
            int y0siz  = hd.ImgULY;
            int xsiz   = hd.ImgWidth;
            int ysiz   = hd.ImgHeight;

            var tx0 = (tileI.x == 0) ? x0siz : xt0siz + tileI.x * xtsiz;
            var ty0 = (tileI.y == 0) ? y0siz : yt0siz + tileI.y * ytsiz;
            var tx1 = (tileI.x != nTiles.x - 1) ? xt0siz + (tileI.x + 1) * xtsiz : xsiz;
            var ty1 = (tileI.y != nTiles.y - 1) ? yt0siz + (tileI.y + 1) * ytsiz : ysiz;

            int xrsiz = hd.GetCompSubsX(c);
            int yrsiz = hd.GetCompSubsY(c);

            int tcx0 = src.GetResULX(c, mdl);
            int tcy0 = src.GetResULY(c, mdl);
            int tcx1 = tcx0 + src.GetTileCompWidth(tIdx, c, mdl);
            int tcy1 = tcy0 + src.GetTileCompHeight(tIdx, c, mdl);

            int ndl  = mdl - r;
            int shift = 1 << ndl;
            int trx0 = CeilDiv(tcx0, shift);
            int try0 = CeilDiv(tcy0, shift);
            int trx1 = CeilDiv(tcx1, shift);
            int try1 = CeilDiv(tcy1, shift);

            int cb0x = src.CbULX;
            int cb0y = src.CbULY;

            // GetPPX/GetPPY always return powers of two
            int twoppx  = GetPPX(tIdx, c, r);
            int twoppy  = GetPPY(tIdx, c, r);
            int twoppx2 = twoppx >> 1;
            int twoppy2 = twoppy >> 1;

            int maxPrec = ppinfo[c][r].Length;
            int nPrec   = 0;

            int istart = FloorDiv(try0 - cb0y, twoppy);
            int iend   = FloorDiv(try1 - 1 - cb0y, twoppy);
            int jstart = FloorDiv(trx0 - cb0x, twoppx);
            int jend   = FloorDiv(trx1 - 1 - cb0x, twoppx);

            int prg_w = twoppx << ndl;
            int prg_h = twoppy << ndl;

            int acb0x, acb0y;
            SubbandSyn sb = null;
            var root = src.GetSynSubbandTree(tIdx, c);

            int p0x, p0y, p1x, p1y;
            int s0x, s0y, s1x, s1y;
            int cw, ch;
            int kstart, kend, lstart, lend, k0, l0;
            int prg_ulx, prg_uly;
            int tmp1, tmp2;

            for (var i = istart; i <= iend; i++)
            {
                for (var j = jstart; j <= jend; j++, nPrec++)
                {
                    prg_ulx = (j == jstart && (trx0 - cb0x) % (xrsiz * twoppx) != 0)
                        ? tx0
                        : cb0x + j * xrsiz * (twoppx << ndl);
                    prg_uly = (i == istart && (try0 - cb0y) % (yrsiz * twoppy) != 0)
                        ? ty0
                        : cb0y + i * yrsiz * (twoppy << ndl);

                    // Reuse or create the PrecInfo slot
                    if (ppinfo[c][r][nPrec] == null)
                        ppinfo[c][r][nPrec] = new PrecInfo(r, cb0x + j * twoppx, cb0y + i * twoppy, twoppx, twoppy, prg_ulx, prg_uly, prg_w, prg_h);
                    else
                        ppinfo[c][r][nPrec].Reset(r, cb0x + j * twoppx, cb0y + i * twoppy, twoppx, twoppy, prg_ulx, prg_uly, prg_w, prg_h);

                    if (r == 0)
                    {
                        acb0x = cb0x;
                        acb0y = cb0y;

                        p0x = acb0x + j * twoppx;
                        p1x = p0x + twoppx;
                        p0y = acb0y + i * twoppy;
                        p1y = p0y + twoppy;

                        sb  = (SubbandSyn)root.GetSubbandByIdx(0, 0);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;

                        cw  = sb.nomCBlkW;
                        ch  = sb.nomCBlkH;
                        k0     = FloorDiv(sb.ulcy - acb0y, ch);
                        kstart = FloorDiv(s0y - acb0y, ch);
                        kend   = FloorDiv(s1y - 1 - acb0y, ch);
                        l0     = FloorDiv(sb.ulcx - acb0x, cw);
                        lstart = FloorDiv(s0x - acb0x, cw);
                        lend   = FloorDiv(s1x - 1 - acb0x, cw);

                        if (s1x - s0x <= 0 || s1y - s0y <= 0)
                        {
                            ppinfo[c][r][nPrec].nblk[0] = 0;
                            ResetOrCreate(ref ttIncl[c][r][nPrec][0], 0, 0);
                            ResetOrCreate(ref ttMaxBP[c][r][nPrec][0], 0, 0);
                        }
                        else
                        {
                            int kRows = kend - kstart + 1;
                            int lCols = lend - lstart + 1;
                            ResetOrCreate(ref ttIncl[c][r][nPrec][0], kRows, lCols);
                            ResetOrCreate(ref ttMaxBP[c][r][nPrec][0], kRows, lCols);
                            ppinfo[c][r][nPrec].nblk[0] = kRows * lCols;

                            var existing0 = ppinfo[c][r][nPrec].cblk[0];
                            if (existing0 == null || existing0.Length != kRows || (kRows > 0 && existing0[0]?.Length != lCols))
                            {
                                existing0 = new CBlkCoordInfo[kRows][];
                                for (var i2 = 0; i2 < kRows; i2++)
                                    existing0[i2] = new CBlkCoordInfo[lCols];
                                ppinfo[c][r][nPrec].cblk[0] = existing0;
                            }

                            for (var k = kstart; k <= kend; k++)
                            {
                                for (var l = lstart; l <= lend; l++)
                                {
                                    var slot = existing0[k - kstart][l - lstart];
                                    if (slot == null)
                                        slot = existing0[k - kstart][l - lstart] = new CBlkCoordInfo(k - k0, l - l0);
                                    else
                                        slot.Reset(k - k0, l - l0);

                                    slot.ulx = (l == l0) ? sb.ulx : sb.ulx + l * cw - (sb.ulcx - acb0x);
                                    slot.uly = (k == k0) ? sb.uly : sb.uly + k * ch - (sb.ulcy - acb0y);

                                    tmp1 = acb0x + l * cw;       tmp1 = (tmp1 > sb.ulcx) ? tmp1 : sb.ulcx;
                                    tmp2 = acb0x + (l + 1) * cw; tmp2 = (tmp2 > sb.ulcx + sb.w) ? sb.ulcx + sb.w : tmp2;
                                    slot.w = tmp2 - tmp1;
                                    tmp1 = acb0y + k * ch;       tmp1 = (tmp1 > sb.ulcy) ? tmp1 : sb.ulcy;
                                    tmp2 = acb0y + (k + 1) * ch; tmp2 = (tmp2 > sb.ulcy + sb.h) ? sb.ulcy + sb.h : tmp2;
                                    slot.h = tmp2 - tmp1;
                                }
                            }
                        }
                    }
                    else
                    {
                        // HL subband (s=1)
                        acb0x = 0; acb0y = cb0y;
                        p0x = acb0x + j * twoppx2; p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2; p1y = p0y + twoppy2;
                        sb  = (SubbandSyn)root.GetSubbandByIdx(r, 1);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;
                        cw = sb.nomCBlkW; ch = sb.nomCBlkH;
                        k0 = FloorDiv(sb.ulcy - acb0y, ch); kstart = FloorDiv(s0y - acb0y, ch); kend = FloorDiv(s1y - 1 - acb0y, ch);
                        l0 = FloorDiv(sb.ulcx - acb0x, cw); lstart = FloorDiv(s0x - acb0x, cw); lend = FloorDiv(s1x - 1 - acb0x, cw);
                        FillSubband(c, r, nPrec, 1, sb, acb0x, acb0y, k0, l0, kstart, kend, lstart, lend, s0x, s1x, s0y, s1y);

                        // LH subband (s=2)
                        acb0x = cb0x; acb0y = 0;
                        p0x = acb0x + j * twoppx2; p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2; p1y = p0y + twoppy2;
                        sb  = (SubbandSyn)root.GetSubbandByIdx(r, 2);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;
                        cw = sb.nomCBlkW; ch = sb.nomCBlkH;
                        k0 = FloorDiv(sb.ulcy - acb0y, ch); kstart = FloorDiv(s0y - acb0y, ch); kend = FloorDiv(s1y - 1 - acb0y, ch);
                        l0 = FloorDiv(sb.ulcx - acb0x, cw); lstart = FloorDiv(s0x - acb0x, cw); lend = FloorDiv(s1x - 1 - acb0x, cw);
                        FillSubband(c, r, nPrec, 2, sb, acb0x, acb0y, k0, l0, kstart, kend, lstart, lend, s0x, s1x, s0y, s1y);

                        // HH subband (s=3)
                        acb0x = 0; acb0y = 0;
                        p0x = acb0x + j * twoppx2; p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2; p1y = p0y + twoppy2;
                        sb  = (SubbandSyn)root.GetSubbandByIdx(r, 3);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;
                        cw = sb.nomCBlkW; ch = sb.nomCBlkH;
                        k0 = FloorDiv(sb.ulcy - acb0y, ch); kstart = FloorDiv(s0y - acb0y, ch); kend = FloorDiv(s1y - 1 - acb0y, ch);
                        l0 = FloorDiv(sb.ulcx - acb0x, cw); lstart = FloorDiv(s0x - acb0x, cw); lend = FloorDiv(s1x - 1 - acb0x, cw);
                        FillSubband(c, r, nPrec, 3, sb, acb0x, acb0y, k0, l0, kstart, kend, lstart, lend, s0x, s1x, s0y, s1y);
                    }
                }
            }
        }

        /// <summary>Fills one subband slot within a precinct, reusing existing
        /// <see cref="CBlkCoordInfo"/> instances in-place when the grid dimensions match.</summary>
        private void FillSubband(int c, int r, int nPrec, int s, SubbandSyn sb,
            int acb0x, int acb0y, int k0, int l0, int kstart, int kend, int lstart, int lend,
            int s0x, int s1x, int s0y, int s1y)
        {
            int cw = sb.nomCBlkW, ch = sb.nomCBlkH;
            if (s1x - s0x <= 0 || s1y - s0y <= 0)
            {
                ppinfo[c][r][nPrec].nblk[s] = 0;
                ResetOrCreate(ref ttIncl[c][r][nPrec][s], 0, 0);
                ResetOrCreate(ref ttMaxBP[c][r][nPrec][s], 0, 0);
                return;
            }

            int kRows = kend - kstart + 1;
            int lCols = lend - lstart + 1;
            ResetOrCreate(ref ttIncl[c][r][nPrec][s], kRows, lCols);
            ResetOrCreate(ref ttMaxBP[c][r][nPrec][s], kRows, lCols);
            ppinfo[c][r][nPrec].nblk[s] = kRows * lCols;

            var existing = ppinfo[c][r][nPrec].cblk[s];
            if (existing == null || existing.Length != kRows || (kRows > 0 && existing[0]?.Length != lCols))
            {
                existing = new CBlkCoordInfo[kRows][];
                for (var i = 0; i < kRows; i++)
                    existing[i] = new CBlkCoordInfo[lCols];
                ppinfo[c][r][nPrec].cblk[s] = existing;
            }

            for (var k = kstart; k <= kend; k++)
            {
                for (var l = lstart; l <= lend; l++)
                {
                    var slot = existing[k - kstart][l - lstart];
                    if (slot == null)
                        slot = existing[k - kstart][l - lstart] = new CBlkCoordInfo(k - k0, l - l0);
                    else
                        slot.Reset(k - k0, l - l0);

                    slot.ulx = (l == l0) ? sb.ulx : sb.ulx + l * cw - (sb.ulcx - acb0x);
                    slot.uly = (k == k0) ? sb.uly : sb.uly + k * ch - (sb.ulcy - acb0y);

                    int tmp1 = acb0x + l * cw;       tmp1 = (tmp1 > sb.ulcx) ? tmp1 : sb.ulcx;
                    int tmp2 = acb0x + (l + 1) * cw; tmp2 = (tmp2 > sb.ulcx + sb.w) ? sb.ulcx + sb.w : tmp2;
                    slot.w = tmp2 - tmp1;
                    tmp1 = acb0y + k * ch;       tmp1 = (tmp1 > sb.ulcy) ? tmp1 : sb.ulcy;
                    tmp2 = acb0y + (k + 1) * ch; tmp2 = (tmp2 > sb.ulcy + sb.h) ? sb.ulcy + sb.h : tmp2;
                    slot.h = tmp2 - tmp1;
                }
            }
        }

        /// <summary> Gets the number of precincts in a given component and resolution level.
        /// 
        /// </summary>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <param name="r">Resolution index
        /// 
        /// </param>
        public virtual int GetNumPrecinct(int c, int r)
        {
            return numPrec[c][r].x * numPrec[c][r].y;
        }

        /// <summary> Read specified packet head and found length of each code-block's piece
        /// of codewords as well as number of skipped most significant bit-planes.
        /// 
        /// </summary>
        /// <param name="l">layer index
        /// 
        /// </param>
        /// <param name="r">Resolution level index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <param name="p">Precinct index
        /// 
        /// </param>
        /// <param name="cbI">CBlkInfo array of relevant component and resolution
        /// level.
        /// 
        /// </param>
        /// <param name="nb">The number of bytes to read in each tile before reaching
        /// output rate (used by truncation mode)
        /// 
        /// </param>
        /// <returns> True if specified output rate or EOF is reached.
        /// 
        /// </returns>
        public virtual bool readPktHead(int l, int r, int c, int p, CBlkInfo[][][] cbI, int[] nb)
        {

            CBlkInfo ccb;
            int nSeg; // number of segment to read
            int cbLen; // Length of cblk's code-words
            int ltp; // last truncation point index
            int passtype; // coding pass type
            TagTreeDecoder tdIncl, tdBD;
            int tmp, tmp2, totnewtp, lblockCur, tpidx;
            var sumtotnewtp = 0;
            Coord cbc;
            var startPktHead = ehs.Pos;
            if (startPktHead >= ehs.length())
            {
                // EOF reached at the beginning of this packet head
                return true;
            }

            var tIdx = src.TileIdx;

            if (usePLTFastPath && !pph)  // Don't use PLT with packed packet headers
            {
                var pktLength = GetPacketLengthFromPLT(tIdx, currentPacketIndex);
                if (pktLength > 0)
                {
                    // We have PLT data - skip packet header parsing!
                    // We still need to update code-block info, but we can skip
                    // the expensive tag tree parsing

                    // Note: This is a simplified fast-path. Full implementation
                    // would need to:
                    // 1. Still read the empty packet bit
                    // 2. Update code-block inclusion flags
                    // 3. Use PLT length to skip to next packet

                    FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.INFO,
                        $"Using PLT fast-path for packet {currentPacketIndex}: length={pktLength}");

                    currentPacketIndex++;

                    // For now, fall through to normal parsing
                    // Full implementation would directly seek using PLT length
                }
            }

            PktHeaderBitReader bin;
            int mend, nend;
            int b;
            SubbandSyn sb;
            var root = src.GetSynSubbandTree(tIdx, c);

            // If packed packet headers was used, use separate stream for reading
            // of packet headers
            bin = pph ? new PktHeaderBitReader(pphbais) : this.bin;

            var mins = (r == 0) ? 0 : 1;
            var maxs = (r == 0) ? 1 : 4;

            var precFound = false;
            for (var s = mins; s < maxs; s++)
            {
                if (p < ppinfo[c][r].Length)
                {
                    precFound = true;
                }
            }
            if (!precFound)
            {
                return false;
            }

            var prec = ppinfo[c][r][p];

            // Synchronize for bit-reading
            bin.sync();

            // If packet is empty there is no info in it (i.e. no code-blocks)
            if (bin.readBit() == 0)
            {
                // No code-block is included
                cblks = new List<CBlkCoordInfo>[maxs + 1];
                for (var s = mins; s < maxs; s++)
                {
                    cblks[s] = new List<CBlkCoordInfo>(10);
                }
                pktIdx++;

                // If truncation mode, checks if output rate is reached
                // unless ncb quit condition is used in which case headers
                // are not counted
                if (isTruncMode && maxCB == -1)
                {
                    tmp = ehs.Pos - startPktHead;
                    if (tmp > nb[tIdx])
                    {
                        nb[tIdx] = 0;
                        return true;
                    }
                    else
                    {
                        nb[tIdx] -= tmp;
                    }
                }

                // Read EPH marker if needed
                if (ephUsed)
                {
                    readEPHMarker(bin);
                }
                return false;
            }

            // Packet is not empty => decode info
            // Loop on each subband in this resolution level
            if (cblks == null || cblks.Length < maxs + 1)
            {
                cblks = new List<CBlkCoordInfo>[maxs + 1];
            }

            for (var s = mins; s < maxs; s++)
            {
                if (cblks[s] == null)
                {
                    cblks[s] = new List<CBlkCoordInfo>(10);
                }
                else
                {
                    cblks[s].Clear();
                }
                sb = (SubbandSyn)root.GetSubbandByIdx(r, s);
                // No code-block in this precinct
                if (prec.nblk[s] == 0)
                {
                    // Go to next subband
                    continue;
                }

                tdIncl = ttIncl[c][r][p][s];
                tdBD = ttMaxBP[c][r][p][s];

                mend = (prec.cblk[s] == null) ? 0 : prec.cblk[s].Length;
                for (var m = 0; m < mend; m++)
                {
                    // Vertical code-blocks
                    nend = (prec.cblk[s][m] == null) ? 0 : prec.cblk[s][m].Length;
                    for (var n = 0; n < nend; n++)
                    {
                        // Horizontal code-blocks
                        cbc = prec.cblk[s][m][n].idx;
                        b = cbc.x + cbc.y * sb.numCb.x;

                        ccb = cbI[s][cbc.y][cbc.x];

                        try
                        {
                            // If code-block not included in previous layer(s)
                            if (ccb == null || ccb.ctp == 0)
                            {
                                if (ccb == null)
                                {
                                    ccb = cbI[s][cbc.y][cbc.x] = new CBlkInfo(prec.cblk[s][m][n].ulx, prec.cblk[s][m][n].uly, prec.cblk[s][m][n].w, prec.cblk[s][m][n].h, nl);
                                }
                                ccb.pktIdx[l] = pktIdx;

                                // Read inclusion using tag-tree
                                tmp = tdIncl.update(m, n, l + 1, bin);
                                if (tmp > l)
                                {
                                    // Not included
                                    continue;
                                }

                                // Read bit-depth using tag-tree
                                tmp = 1; // initialization
                                for (tmp2 = 1; tmp >= tmp2; tmp2++)
                                {
                                    tmp = tdBD.update(m, n, tmp2, bin);
                                }
                                ccb.msbSkipped = tmp2 - 2;

                                // New code-block => at least one truncation point
                                totnewtp = 1;
                                ccb.addNTP(l, 0);

                                // Check whether ncb quit condition is reached
                                ncb++;

                                if (maxCB != -1 && !ncbQuit && ncb == maxCB)
                                {
                                    // ncb quit condition reached
                                    ncbQuit = true;
                                    tQuit = tIdx;
                                    cQuit = c;
                                    sQuit = s;
                                    rQuit = r;
                                    xQuit = cbc.x;
                                    yQuit = cbc.y;
                                }
                            }
                            else
                            {
                                // If code-block already included in one of
                                // the previous layers.

                                ccb.pktIdx[l] = pktIdx;

                                // If not included
                                if (bin.readBit() != 1)
                                {
                                    continue;
                                }

                                // At least 1 more truncation point than
                                // prev. packet
                                totnewtp = 1;
                            }

                            // Read new truncation points
                            if (bin.readBit() == 1)
                            {
                                // if bit is 1
                                totnewtp++;

                                // if next bit is 0 do nothing
                                if (bin.readBit() == 1)
                                {
                                    //if is 1
                                    totnewtp++;

                                    tmp = bin.readBits(2);
                                    totnewtp += tmp;

                                    // If next 2 bits are not 11 do nothing
                                    if (tmp == 0x3)
                                    {
                                        //if 11
                                        tmp = bin.readBits(5);
                                        totnewtp += tmp;

                                        // If next 5 bits are not 11111 do nothing
                                        if (tmp == 0x1F)
                                        {
                                            //if 11111
                                            totnewtp += bin.readBits(7);
                                        }
                                    }
                                }
                            }
                            ccb.addNTP(l, totnewtp);
                            sumtotnewtp += totnewtp;
                            cblks[s].Add(prec.cblk[s][m][n]);

                            // Code-block length

                            // -- Compute the number of bit to read to obtain
                            // code-block length.  
                            // numBits = betaLamda + log2(totnewtp);

                            // The length is signalled for each segment in
                            // addition to the final one. The total length is the
                            // sum of all segment lengths.

                            // If regular termination in use, then there is one
                            // segment per truncation point present. Otherwise, if
                            // selective arithmetic bypass coding mode is present,
                            // then there is one termination per bypass/MQ and
                            // MQ/bypass transition. Otherwise, the only
                            // termination is at the end of the code-block.
                            var options = ((int)decSpec.ecopts.GetTileCompVal(tIdx, c));

                            if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                            {
                                // Regular termination in use, one segment per new
                                // pass (i.e. truncation point)
                                nSeg = totnewtp;
                            }
                            else if ((options & StdEntropyCoderOptions.OPT_BYPASS) != 0)
                            {
                                // Selective arithmetic coding bypass coding mode
                                // in use, but no regular termination 1 segment up
                                // to the end of the last pass of the 4th most
                                // significant bit-plane, and, in each following
                                // bit-plane, one segment up to the end of the 2nd
                                // pass and one up to the end of the 3rd pass.

                                if (ccb.ctp <= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX)
                                {
                                    nSeg = 1;
                                }
                                else
                                {
                                    nSeg = 1; // One at least for last pass
                                              // And one for each other terminated pass
                                    for (tpidx = ccb.ctp - totnewtp; tpidx < ccb.ctp - 1; tpidx++)
                                    {
                                        if (tpidx >= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX - 1)
                                        {
                                            passtype = (tpidx + StdEntropyCoderOptions.NUM_EMPTY_PASSES_IN_MS_BP) % StdEntropyCoderOptions.NUM_PASSES;
                                            if (passtype == 1 || passtype == 2)
                                            {
                                                // bypass coding just before MQ
                                                // pass or MQ pass just before
                                                // bypass coding => terminated
                                                nSeg++;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Nothing special in use, just one segment
                                nSeg = 1;
                            }

                            // Reads lblock increment (common to all segments)
                            while (bin.readBit() != 0)
                            {
                                lblock[c][r][s][cbc.y][cbc.x]++;
                            }

                            if (nSeg == 1)
                            {
                                // Only one segment in packet
                                cbLen = bin.readBits(lblock[c][r][s][cbc.y][cbc.x] + MathUtil.log2(totnewtp));
                            }
                            else
                            {
                                // We must read one length per segment
                                ccb.segLen[l] = new int[nSeg];
                                cbLen = 0;
                                int j;
                                if ((options & StdEntropyCoderOptions.OPT_TERM_PASS) != 0)
                                {
                                    // Regular termination: each pass is terminated
                                    for (tpidx = ccb.ctp - totnewtp, j = 0; tpidx < ccb.ctp; tpidx++, j++)
                                    {

                                        lblockCur = lblock[c][r][s][cbc.y][cbc.x];

                                        tmp = bin.readBits(lblockCur);
                                        ccb.segLen[l][j] = tmp;
                                        cbLen += tmp;
                                    }
                                }
                                else
                                {
                                    // Bypass coding: only some passes are
                                    // terminated
                                    ltp = ccb.ctp - totnewtp - 1;
                                    for (tpidx = ccb.ctp - totnewtp, j = 0; tpidx < ccb.ctp - 1; tpidx++)
                                    {
                                        if (tpidx >= StdEntropyCoderOptions.FIRST_BYPASS_PASS_IDX - 1)
                                        {
                                            passtype = (tpidx + StdEntropyCoderOptions.NUM_EMPTY_PASSES_IN_MS_BP) % StdEntropyCoderOptions.NUM_PASSES;
                                            if (passtype == 0)
                                                continue;

                                            lblockCur = lblock[c][r][s][cbc.y][cbc.x];
                                            tmp = bin.readBits(lblockCur + MathUtil.log2(tpidx - ltp));
                                            ccb.segLen[l][j] = tmp;
                                            cbLen += tmp;
                                            ltp = tpidx;
                                            j++;
                                        }
                                    }
                                    // Last pass has always the length sent
                                    lblockCur = lblock[c][r][s][cbc.y][cbc.x];
                                    tmp = bin.readBits(lblockCur + MathUtil.log2(tpidx - ltp));
                                    cbLen += tmp;
                                    ccb.segLen[l][j] = tmp;
                                }
                            }
                            ccb.len[l] = cbLen;

                            // If truncation mode, checks if output rate is reached
                            // unless ncb and lbody quit conditions used.
                            if (isTruncMode && maxCB == -1)
                            {
                                tmp = ehs.Pos - startPktHead;
                                if (tmp > nb[tIdx])
                                {
                                    nb[tIdx] = 0;
                                    // Remove found information in this code-block
                                    if (l == 0)
                                    {
                                        cbI[s][cbc.y][cbc.x] = null;
                                    }
                                    else
                                    {
                                        ccb.off[l] = ccb.len[l] = 0;
                                        ccb.ctp -= ccb.ntp[l];
                                        ccb.ntp[l] = 0;
                                        ccb.pktIdx[l] = -1;
                                    }
                                    return true;
                                }
                            }
                        }
                        catch (System.IO.EndOfStreamException)
                        {
                            // Remove found information in this code-block
                            if (l == 0)
                            {
                                cbI[s][cbc.y][cbc.x] = null;
                            }
                            else
                            {
                                ccb.off[l] = ccb.len[l] = 0;
                                ccb.ctp -= ccb.ntp[l];
                                ccb.ntp[l] = 0;
                                ccb.pktIdx[l] = -1;
                            }
                            //                         throw new EOFException();
                            return true;
                        }
                    } // End loop on horizontal code-blocks
                } // End loop on vertical code-blocks
            } // End loop on subbands

            // Read EPH marker if needed
            if (ephUsed)
            {
                readEPHMarker(bin);
            }

            pktIdx++;

            // If truncation mode, checks if output rate is reached
            if (isTruncMode && maxCB == -1)
            {
                tmp = ehs.Pos - startPktHead;
                if (tmp > nb[tIdx])
                {
                    nb[tIdx] = 0;
                    return true;
                }
                else
                {
                    nb[tIdx] -= tmp;
                }
            }
            return false;
        }

        /// <summary> Reads specified packet body in order to find offset of each
        /// code-block's piece of codeword. This use the list of found code-blocks
        /// in previous red packet head.
        /// 
        /// </summary>
        /// <param name="l">layer index
        /// 
        /// </param>
        /// <param name="r">Resolution level index
        /// 
        /// </param>
        /// <param name="c">Component index
        /// 
        /// </param>
        /// <param name="p">Precinct index
        /// 
        /// </param>
        /// <param name="cbI">CBlkInfo array of relevant component and resolution
        /// level.
        /// 
        /// </param>
        /// <param name="nb">The remaining number of bytes to read from the bit stream in
        /// each tile before reaching the decoding rate (in truncation mode)
        /// 
        /// </param>
        /// <returns> True if decoding rate is reached 
        /// 
        /// </returns>
        public virtual bool readPktBody(int l, int r, int c, int p, CBlkInfo[][][] cbI, int[] nb)
        {
            var curOff = ehs.Pos;
            //Coord curCB;
            CBlkInfo ccb;
            var stopRead = false;
            var tIdx = src.TileIdx;
            Coord cbc;

            var precFound = false;
            var mins = (r == 0) ? 0 : 1;
            var maxs = (r == 0) ? 1 : 4;

            if (usePLTFastPath && cblks != null)
            {
                // With PLT, we know exact packet lengths
                // We can seek directly without reading individual code-block data

                for (var s = mins; s < maxs; s++)
                {
                    if (cblks[s] == null)
                        continue;

                    for (var numCB = 0; numCB < cblks[s].Count; numCB++)
                    {
                        cbc = cblks[s][numCB].idx;
                        ccb = cbI[s][cbc.y][cbc.x];

                        // Code-block offset is current position
                        ccb.off[l] = curOff;

                        // **PLT OPTIMIZATION**: Length is known from packet header
                        // No need to seek and read - we already have the length
                        curOff += ccb.len[l];

                        // Check truncation/ncb quit conditions
                        if (isTruncMode && ccb.len[l] > nb[tIdx])
                        {
                            if (l == 0)
                                cbI[s][cbc.y][cbc.x] = null;
                            else
                            {
                                ccb.off[l] = ccb.len[l] = 0;
                                ccb.ctp -= ccb.ntp[l];
                                ccb.ntp[l] = 0;
                                ccb.pktIdx[l] = -1;
                            }
                            stopRead = true;
                        }

                        if (!stopRead && isTruncMode)
                        {
                            nb[tIdx] -= ccb.len[l];
                        }

                        // Check ncb quit condition
                        if (ncbQuit && r == rQuit && s == sQuit &&
                            cbc.x == xQuit && cbc.y == yQuit &&
                            tIdx == tQuit && c == cQuit)
                        {
                            cbI[s][cbc.y][cbc.x] = null;
                            stopRead = true;
                        }
                    }
                }

                // Seek to end of packet using PLT data
                ehs.seek(curOff);

                return stopRead;
            }

            for (var s = mins; s < maxs; s++)
            {
                if (p < ppinfo[c][r].Length)
                {
                    precFound = true;
                }
            }
            if (!precFound)
            {
                return false;
            }

            for (var s = mins; s < maxs; s++)
            {
                for (var numCB = 0; numCB < cblks[s].Count; numCB++)
                {
                    cbc = cblks[s][numCB].idx;
                    ccb = cbI[s][cbc.y][cbc.x];
                    ccb.off[l] = curOff;
                    curOff += ccb.len[l];
                    try
                    {
                        ehs.seek(curOff);
                    }
                    catch (System.IO.EndOfStreamException)
                    {
                        if (l == 0)
                        {
                            cbI[s][cbc.y][cbc.x] = null;
                        }
                        else
                        {
                            ccb.off[l] = ccb.len[l] = 0;
                            ccb.ctp -= ccb.ntp[l];
                            ccb.ntp[l] = 0;
                            ccb.pktIdx[l] = -1;
                        }
                        throw new System.IO.EndOfStreamException();
                    }

                    // If truncation mode
                    if (isTruncMode)
                    {
                        if (stopRead || ccb.len[l] > nb[tIdx])
                        {
                            // Remove found information in this code-block
                            if (l == 0)
                            {
                                cbI[s][cbc.y][cbc.x] = null;
                            }
                            else
                            {
                                ccb.off[l] = ccb.len[l] = 0;
                                ccb.ctp -= ccb.ntp[l];
                                ccb.ntp[l] = 0;
                                ccb.pktIdx[l] = -1;
                            }
                            stopRead = true;
                        }
                        if (!stopRead)
                        {
                            nb[tIdx] -= ccb.len[l];
                        }
                    }
                    // If ncb quit condition reached
                    if (ncbQuit && r == rQuit && s == sQuit && cbc.x == xQuit && cbc.y == yQuit && tIdx == tQuit && c == cQuit)
                    {
                        cbI[s][cbc.y][cbc.x] = null;
                        stopRead = true;
                    }
                } // Loop on code-blocks
            } // End loop on subbands

            // Seek to the end of the packet
            ehs.seek(curOff);

            return stopRead;
        }


        /// <summary> Returns the precinct partition width for the specified component,
        /// resolution level and tile.
        /// 
        /// </summary>
        /// <param name="t">the tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component (between 0 and C-1)
        /// 
        /// </param>
        /// <param name="r">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> the precinct partition width for the specified component,
        /// resolution level and tile.
        /// 
        /// </returns>
        public int GetPPX(int t, int c, int r)
        {
            return decSpec.pss.GetPPX(t, c, r);
        }

        /// <summary> Returns the precinct partition height for the specified component,
        /// resolution level and tile.
        /// 
        /// </summary>
        /// <param name="t">the tile index
        /// 
        /// </param>
        /// <param name="c">The index of the component (between 0 and C-1)
        /// 
        /// </param>
        /// <param name="rl">The resolution level, from 0 to L.
        /// 
        /// </param>
        /// <returns> the precinct partition height in the specified component, for
        /// the specified resolution level, for the current tile.
        /// 
        /// </returns>
        public int GetPPY(int t, int c, int rl)
        {
            return decSpec.pss.GetPPY(t, c, rl);
        }

        /// <summary>
        /// Checks if PLT (Packet Length) markers are available for fast packet access.
        /// When PLT markers are present, packet lengths can be determined without
        /// parsing packet headers, enabling 5-10x faster packet operations.
        /// </summary>
        /// <returns>True if PLT markers are available for the current tile</returns>
        public virtual bool SupportsFastPacketAccess()
        {
            return usePLTFastPath && pltData != null &&
                   pltData.GetPacketCount(tIdx) > 0;
        }

        /// <summary>
        /// Gets the packet length from PLT data if available.
        /// </summary>
        /// <param name="tileIdx">The tile index</param>
        /// <param name="packetIndex">The packet index within the tile</param>
        /// <returns>Packet length in bytes, or -1 if PLT data unavailable</returns>
        private int GetPacketLengthFromPLT(int tileIdx, int packetIndex)
        {
            if (!usePLTFastPath || pltData == null)
                return -1;

            var packets = pltData.GetPacketEntries(tileIdx).ToList();
            if (packetIndex < 0 || packetIndex >= packets.Count)
                return -1;

            return packets[packetIndex].PacketLength;
        }

        /// <summary> Try to read a SOP marker and check that its sequence number if not out
        /// of sequence. If so, an error is thrown.</summary>
        /// <param name="nBytes">The number of bytes left to read from each tile</param>
        /// <param name="p">Precinct index</param>
        /// <param name="r">Resolution level index</param>
        /// <param name="c">Component index</param>
        public virtual bool readSOPMarker(int[] nBytes, int p, int c, int r)
        {
            int val;
            var sopArray = new byte[6];
            var tIdx = src.TileIdx;
            var mins = (r == 0) ? 0 : 1;
            var maxs = (r == 0) ? 1 : 4;
            var precFound = false;
            for (var s = mins; s < maxs; s++)
            {
                if (p < ppinfo[c][r].Length)
                {
                    precFound = true;
                }
            }
            if (!precFound)
            {
                return false;
            }

            // If SOP markers are not used, return
            if (!sopUsed)
            {
                return false;
            }

            // Check if SOP is used for this packet
            var pos = ehs.Pos;
            if ((short)((ehs.read() << 8) | ehs.read()) != Markers.SOP)
            {
                ehs.seek(pos);
                return false;
            }
            ehs.seek(pos);

            // If length of SOP marker greater than remaining bytes to read for
            // this tile return true
            if (nBytes[tIdx] < 6)
            {
                return true;
            }
            nBytes[tIdx] -= 6;

            // Read marker into array 'sopArray'
            ehs.readFully(sopArray, 0, Markers.SOP_LENGTH);

            // Check if this is the correct marker (Markers.EPH is a negative short -> cast to short)
            var marker = (short)((sopArray[0] << 8) | sopArray[1]);
            if (marker != Markers.SOP)
            {
                throw new InvalidOperationException("Corrupted Bitstream: Could not parse SOP " + "marker !");
            }

            // Check if length is correct
            val = (sopArray[2] & 0xff);
            val <<= 8;
            val |= (sopArray[3] & 0xff);
            if (val != 4)
            {
                throw new InvalidOperationException("Corrupted Bitstream: Corrupted SOP marker !");
            }

            // Check if sequence number if ok
            val = (sopArray[4] & 0xff);
            val <<= 8;
            val |= (sopArray[5] & 0xff);

            if (!pph && val != pktIdx)
            {
                throw new InvalidOperationException("Corrupted Bitstream: SOP marker out of " + "sequence !");
            }
            if (pph && val != pktIdx - 1)
            {
                // if packed packet headers are used, packet header was read
                // before SOP marker segment
                throw new InvalidOperationException("Corrupted Bitstream: SOP marker out of " + "sequence !");
            }
            return false;
        }

        /// <summary> Try to read an EPH marker. If it is not possible then an Error is
        /// thrown.
        /// 
        /// </summary>
        /// <param name="bin">The packet header reader to read the EPH marker from
        /// 
        /// </param>
        public virtual void readEPHMarker(PktHeaderBitReader bin)
        {
            var ephArray = new byte[2];

            if (bin.usebais)
            {
                bin.bais.Read(ephArray, 0, Markers.EPH_LENGTH);
            }
            else
            {
                bin.bitReader.readFully(ephArray, 0, Markers.EPH_LENGTH);
            }

            // Check if this is the correct marker (Markers.EPH is a negative short -> cast to short)
            var marker = (short)((ephArray[0] << 8) | ephArray[1]);
            if (marker != Markers.EPH)
            {
                throw new InvalidOperationException("Corrupted Bitstream: Could not parse EPH " + "marker ! ");
            }
        }

        /// <summary> Get PrecInfo instance of the specified resolution level, component and
        /// precinct.
        /// 
        /// </summary>
        /// <param name="c">Component index.
        /// 
        /// </param>
        /// <param name="r">Resolution level index.
        /// 
        /// </param>
        /// <param name="p">Precinct index.
        /// 
        /// </param>
        public virtual PrecInfo GetPrecInfo(int c, int r, int p)
        {
            return ppinfo[c][r][p];
        }
    }
}