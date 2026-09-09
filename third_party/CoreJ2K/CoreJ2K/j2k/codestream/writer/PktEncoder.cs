/*
*
* COPYRIGHT:
* 
* This software module was originally developed by Rapha�l Grosbois and
* Diego Santa Cruz (Swiss Federal Institute of Technology-EPFL); Joel
* Askel�f (Ericsson Radio Systems AB); and Bertrand Berthelot, David
* Bouchard, F�lix Henry, Gerard Mozelle and Patrice Onno (Canon Research
* Centre France S.A) in the course of development of the JPEG2000
* standard as specified by ISO/IEC 15444 (JPEG 2000 Standard). This
* software module is an implementation of a part of the JPEG 2000
* Standard. Swiss Federal Institute of Technology-EPFL, Ericsson Radio
* Systems AB and Canon Research Centre France S.A (collectively JJ2000
* Partners) agree not to assert against ISO/IEC and users of the JPEG
* 2000 Standard (Users) any of their rights under the copyright, not
* including other intellectual property rights, for this software module
* with respect to the usage by ISO/IEC and Users of this software module
* or modifications thereof for use in hardware or software products
* claiming conformance to the JPEG 2000 Standard. Those intending to use
* this software module in hardware or software products are advised that
* their use may infringe existing patents. The original developers of
* this software module, JJ2000 Partners and ISO/IEC assume no liability
* for use of this software module or modifications thereof. No license
* or right to this software module is granted for non JPEG 2000 Standard
* conforming products. JJ2000 Partners have full right to use this
* software module for his/her own purpose, assign or donate this
* software module to any third party and to inhibit third parties from
* using this software module for non JPEG 2000 Standard conforming
* products. This copyright notice must be included in all copies or
* derivative works of this software module.
* 
* Copyright (c) 1999/2000 JJ2000 Partners.
* */
using CoreJ2K.j2k.encoder;
using CoreJ2K.j2k.entropy.encoder;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.util;
using CoreJ2K.j2k.wavelet.analysis;
using System;

namespace CoreJ2K.j2k.codestream.writer
{

    /// <summary> This class builds packets and keeps the state information of packet
    /// interdependencies. It also supports saving the state and reverting
    /// (restoring) to the last saved state, with the save() and restore() methods.
    /// 
    /// Each time the encodePacket() method is called a new packet is encoded,
    /// the packet header is returned by the method, and the packet body can be
    /// obtained with the getLastBodyBuf() and getLastBodyLen() methods.
    /// 
    /// </summary>
    public class PktEncoder
    {
        /// <summary> Returns the buffer of the body of the last encoded packet. The length
        /// of the body can be retrieved with the getLastBodyLen() method. The
        /// length of the array returned by this method may be larger than the
        /// actual body length.
        /// 
        /// </summary>
        /// <returns> The buffer of body of the last encoded packet.
        /// 
        /// </returns>
        /// <exception cref="IllegalArgumentException">If no packet has been coded since
        /// last reset(), last restore(), or object creation.
        /// 
        /// </exception>
        /// <seealso cref="getLastBodyLen" />
        public virtual byte[] LastBodyBuf
        {
            get
            {
                if (lbbuf == null)
                {
                    throw new ArgumentException();
                }
                return lbbuf;
            }

        }
        /// <summary> Returns the length of the body of the last encoded packet, in
        /// bytes. The body itself can be retrieved with the getLastBodyBuf()
        /// method.
        /// 
        /// </summary>
        /// <returns> The length of the body of last encoded packet, in bytes.
        /// 
        /// </returns>
        /// <seealso cref="getLastBodyBuf" />
        public virtual int LastBodyLen => lblen;

        /// <summary> Returns true if the current packet is writable i.e. should be written.
        /// Returns false otherwise.
        /// 
        /// </summary>
        public virtual bool PacketWritable => packetWritable;

        /// <summary> Tells if there was ROI information in the last written packet 
        /// 
        /// </summary>
        public virtual bool ROIinPkt => roiInPkt;

        /// <summary>Gives the length to read in current packet body to get all ROI
        /// information 
        /// </summary>
        public virtual int ROILen => roiLen;

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
        /// <returns> the options name, their synopsis and their explanation, 
        /// or null if no options are supported.
        /// 
        /// </returns>
        public static string[][] ParameterInfo => pinfo;

        /// <summary>The prefix for packet encoding options: 'P' </summary>
        public const char OPT_PREFIX = 'P';

        /// <summary>The list of parameters that is accepted for packet encoding.</summary>
        private static readonly string[][] pinfo = { new string[] { "Psop", "[<tile idx>] on|off" + "[ [<tile idx>] on|off ...]", "Specifies whether start of packet (SOP) markers should be used. " + "'on' enables, 'off' disables it.", "off" }, new string[] { "Peph", "[<tile idx>] on|off" + "[ [<tile  idx>] on|off ...]", "Specifies whether end of packet header (EPH) markers should be " + " used. 'on' enables, 'off' disables it.", "off" } };

        /// <summary>The initial value for the lblock </summary>
        private const int INIT_LBLOCK = 3;

        /// <summary>The source object </summary>
        private readonly CodedCBlkDataSrcEnc infoSrc;

        /// <summary>The encoder specs </summary>
        private readonly EncoderSpecs encSpec;

        /// <summary> The tag tree for inclusion information. The indexes are outlined
        /// below. Note that the layer indexes start at 1, therefore, the layer
        /// index minus 1 is used. The subband indices are used as they are defined
        /// in the Subband class. The tile indices start at 0 and follow a
        /// lexicographical order.
        /// 
        /// <ul>
        /// <li>1st index: tile index, in lexicographical order</li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: precinct index </li>
        /// <li>5th index: subband index </li>
        /// </ul>
        /// 
        /// </summary>
        private readonly TagTreeEncoder[][][][][] ttIncl;

        /// <summary> The tag tree for the maximum significant bit-plane. The indexes are
        /// outlined below. Note that the layer indexes start at 1, therefore, the
        /// layer index minus 1 is used. The subband indices are used as they are
        /// defined in the Subband class. The tile indices start at 0 and follow a
        /// lexicographical order.
        /// 
        /// <ul>
        /// <li>1st index: tile index, in lexicographical order</li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: precinct index </li>
        /// <li>5th index: subband index</li>
        /// </ul>
        /// 
        /// </summary>
        private readonly TagTreeEncoder[][][][][] ttMaxBP;

        /// <summary> The base number of bits for sending code-block length information
        /// (referred as Lblock in the JPEG 2000 standard). The indexes are
        /// outlined below. Note that the layer indexes start at 1, therefore, the
        /// layer index minus 1 is used. The subband indices are used as they are
        /// defined in the Subband class. The tile indices start at 0 and follow a
        /// lexicographical order.
        /// 
        /// <ul>
        /// <li>1st index: tile index, in lexicographical order </li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: subband index </li>
        /// <li>5th index: code-block index, in lexicographical order</li>
        /// </ul>
        /// 
        /// </summary>
        private readonly int[][][][][] lblock;

        /// <summary> The last encoded truncation point for each code-block. A negative value
        /// means that no information has been included for the block, yet. The
        /// indexes are outlined below. The subband indices are used as they are
        /// defined in the Subband class. The tile indices start at 0 and follow a
        /// lexicographical order. The code-block indices follow a lexicographical
        /// order within the subband tile.
        /// 
        /// What is actually stored is the index of the element in
        /// CBlkRateDistStats.truncIdxs that gives the real truncation point.
        /// 
        /// <ul>
        /// <li>1st index: tile index, in lexicographical order </li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: subband index</li>
        /// <li>5th index: code-block index, in lexicographical order </li>
        /// </ul>
        /// 
        /// </summary>
        private readonly int[][][][][] prevtIdxs;

        /// <summary> The saved base number of bits for sending code-block length
        /// information. It is used for restoring previous saved state by
        /// restore(). The indexes are outlined below. Note that the layer indexes
        /// start at 1, therefore, the layer index minus 1 is used. The subband
        /// indices are used as they are defined in the Subband class. The tile
        /// indices start at 0 and follow a lexicographical order.
        /// 
        /// <ul> 
        /// <li>1st index: tile index, in lexicographical order </li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: subband index </li>
        /// <li>5th index: code-block index, in lexicographical order</li>
        /// </ul>
        /// 
        /// </summary>
        private int[][][][][] bak_lblock = null!;

        /// <summary> The saved last encoded truncation point for each code-block. It is used
        /// for restoring previous saved state by restore(). A negative value means
        /// that no information has been included for the block, yet. The indexes
        /// are outlined below. The subband indices are used as they are defined in
        /// the Subband class. The tile indices start at 0 and follow a
        /// lexicographical order. The code-block indices follow a lexicographical
        /// order within the subband tile.
        /// 
        /// <ul>
        /// <li>1st index: tile index, in lexicographical order </li>
        /// <li>2nd index: component index </li>
        /// <li>3rd index: resolution level </li>
        /// <li>4th index: subband index </li>
        /// <li>5th index: code-block index, in lexicographical order </li>
        /// </ul>
        /// 
        /// </summary>
        private int[][][][][] bak_prevtIdxs = null!;

        /// <summary>The body buffer of the last encoded packet </summary>
        private byte[]? lbbuf;

        /// <summary>The body length of the last encoded packet </summary>
        private int lblen;

        /// <summary>The saved state </summary>
        private bool saved;

        /// <summary>Whether or not there is ROI information in the last encoded Packet </summary>
        private bool roiInPkt = false;

        /// <summary>Length to read in current packet body to get all the ROI information </summary>
        private int roiLen = 0;

        /// <summary> Array containing the coordinates, width, height, indexes, ... of the
        /// precincts.
        /// 
        /// <ul>
        /// <li> 1st dim: tile index.</li>
        /// <li> 2nd dim: component index.</li>
        /// <li> 3rd dim: resolution level index.</li>
        /// <li> 4th dim: precinct index.</li>
        /// </ul> 
        /// 
        /// </summary>
        private readonly PrecInfo[][][][] ppinfo;

        /// <summary>Whether or not the current packet is writable </summary>
        private bool packetWritable;

        /// <summary> Creates a new packet encoder object, using the information from the
        /// 'infoSrc' object. 
        /// 
        /// </summary>
        /// <param name="infoSrc">The source of information to construct the object.
        /// 
        /// </param>
        /// <param name="encSpec">The encoding parameters.
        /// 
        /// </param>
        /// <param name="numPrec">Maximum number of precincts in each tile, component
        /// and resolution level.
        /// 
        /// </param>
        /// <param name="pl">ParameterList instance that holds command line options
        /// 
        /// </param>
        public PktEncoder(CodedCBlkDataSrcEnc infoSrc, EncoderSpecs encSpec, Coord[][][]? numPrec, ParameterList pl)
        {
            this.infoSrc = infoSrc;
            this.encSpec = encSpec;

            // Check parameters
            pl.checkList(OPT_PREFIX, ParameterList.toNameArray(pinfo));

            // Get number of components and tiles
            var nc = infoSrc.NumComps;
            var nt = infoSrc.GetNumTiles();

            // Do initial allocation
            ttIncl = new TagTreeEncoder[nt][][][][];
            for (var i = 0; i < nt; i++)
            {
                ttIncl[i] = new TagTreeEncoder[nc][][][];
            }
            ttMaxBP = new TagTreeEncoder[nt][][][][];
            for (var i2 = 0; i2 < nt; i2++)
            {
                ttMaxBP[i2] = new TagTreeEncoder[nc][][][];
            }
            lblock = new int[nt][][][][];
            for (var i3 = 0; i3 < nt; i3++)
            {
                lblock[i3] = new int[nc][][][];
            }
            prevtIdxs = new int[nt][][][][];
            for (var i4 = 0; i4 < nt; i4++)
            {
                prevtIdxs[i4] = new int[nc][][][];
            }
            ppinfo = new PrecInfo[nt][][][];
            for (var i5 = 0; i5 < nt; i5++)
            {
                ppinfo[i5] = new PrecInfo[nc][][];
            }

            // Finish allocation
            SubbandAn root, sb;
            int maxs, mins;
            int mrl;
            //Coord tmpCoord = null;
            int numcb; // Number of code-blocks
                       //System.Collections.ArrayList cblks = null;
            infoSrc.SetTile(0, 0);
            for (var t = 0; t < nt; t++)
            {
                // Loop on tiles
                for (var c = 0; c < nc; c++)
                {
                    // Loop on components
                    // Get number of resolution levels
                    root = infoSrc.GetAnSubbandTree(t, c);
                    mrl = root.resLvl;

                    lblock[t][c] = new int[mrl + 1][][];
                    ttIncl[t][c] = new TagTreeEncoder[mrl + 1][][];
                    ttMaxBP[t][c] = new TagTreeEncoder[mrl + 1][][];
                    prevtIdxs[t][c] = new int[mrl + 1][][];
                    ppinfo[t][c] = new PrecInfo[mrl + 1][];

                    for (var r = 0; r <= mrl; r++)
                    {
                        // Loop on resolution levels
                        mins = (r == 0) ? 0 : 1;
                        maxs = (r == 0) ? 1 : 4;

                        var maxPrec = numPrec![t][c][r].x * numPrec![t][c][r].y;

                        ttIncl[t][c][r] = new TagTreeEncoder[maxPrec][];
                        for (var i6 = 0; i6 < maxPrec; i6++)
                        {
                            ttIncl[t][c][r][i6] = new TagTreeEncoder[maxs];
                        }
                        ttMaxBP[t][c][r] = new TagTreeEncoder[maxPrec][];
                        for (var i7 = 0; i7 < maxPrec; i7++)
                        {
                            ttMaxBP[t][c][r][i7] = new TagTreeEncoder[maxs];
                        }
                        prevtIdxs[t][c][r] = new int[maxs][];
                        lblock[t][c][r] = new int[maxs][];

                        // Precincts and code-blocks
                        ppinfo[t][c][r] = new PrecInfo[maxPrec];
                        fillPrecInfo(t, c, r);

                        for (var s = mins; s < maxs; s++)
                        {
                            // Loop on subbands
                            sb = (SubbandAn)root.GetSubbandByIdx(r, s);
                            numcb = sb.numCb.x * sb.numCb.y;

                            lblock[t][c][r][s] = new int[numcb];
                            ArrayUtil.intArraySet(lblock[t][c][r][s], INIT_LBLOCK);

                            prevtIdxs[t][c][r][s] = new int[numcb];
                            ArrayUtil.intArraySet(prevtIdxs[t][c][r][s], -1);
                        }
                    }
                }
                if (t != nt - 1)
                    infoSrc.NextTile();
            }
        }

        /// <summary> Retrives precincts and code-blocks coordinates in the given resolution,
        /// component and tile. It terminates TagTreeEncoder initialization as
        /// well.
        /// 
        /// </summary>
        /// <param name="t">Tile index.
        /// 
        /// </param>
        /// <param name="c">Component index.
        /// 
        /// </param>
        /// <param name="r">Resolution level index.
        /// 
        /// </param>
        private void fillPrecInfo(int t, int c, int r)
        {
            if (ppinfo[t][c][r].Length == 0)
                return; // No precinct in this
                        // resolution level

            var tileI = infoSrc.GetTile(null);
            var nTiles = infoSrc.GetNumTiles(null);

            var x0siz = infoSrc.ImgULX;
            var y0siz = infoSrc.ImgULY;
            var xsiz = x0siz + infoSrc.ImgWidth;
            var ysiz = y0siz + infoSrc.ImgHeight;
            var xt0siz = infoSrc.TilePartULX;
            var yt0siz = infoSrc.TilePartULY;
            var xtsiz = infoSrc.NomTileWidth;
            var ytsiz = infoSrc.NomTileHeight;

            var tx0 = (tileI.x == 0) ? x0siz : xt0siz + tileI.x * xtsiz;
            var ty0 = (tileI.y == 0) ? y0siz : yt0siz + tileI.y * ytsiz;
            var tx1 = (tileI.x != nTiles.x - 1) ? xt0siz + (tileI.x + 1) * xtsiz : xsiz;
            var ty1 = (tileI.y != nTiles.y - 1) ? yt0siz + (tileI.y + 1) * ytsiz : ysiz;

            var xrsiz = infoSrc.GetCompSubsX(c);
            var yrsiz = infoSrc.GetCompSubsY(c);

            var tcx0 = (int)Math.Ceiling(tx0 / (double)(xrsiz));
            var tcy0 = (int)Math.Ceiling(ty0 / (double)(yrsiz));
            var tcx1 = (int)Math.Ceiling(tx1 / (double)(xrsiz));
            var tcy1 = (int)Math.Ceiling(ty1 / (double)(yrsiz));

            var ndl = infoSrc.GetAnSubbandTree(t, c).resLvl - r;
            var trx0 = (int)Math.Ceiling(tcx0 / (double)(1 << ndl));
            var try0 = (int)Math.Ceiling(tcy0 / (double)(1 << ndl));
            var trx1 = (int)Math.Ceiling(tcx1 / (double)(1 << ndl));
            var try1 = (int)Math.Ceiling(tcy1 / (double)(1 << ndl));

            var cb0x = infoSrc.CbULX;
            var cb0y = infoSrc.CbULY;

            double twoppx = encSpec.pss.GetPPX(t, c, r);
            double twoppy = encSpec.pss.GetPPY(t, c, r);
            var twoppx2 = (int)(twoppx / 2);
            var twoppy2 = (int)(twoppy / 2);

            // Precincts are located at (cb0x+i*twoppx,cb0y+j*twoppy)
            // Valid precincts are those which intersect with the current
            // resolution level
            var maxPrec = ppinfo[t][c][r].Length;
            var nPrec = 0;

            var istart = (int)Math.Floor((try0 - cb0y) / twoppy);
            var iend = (int)Math.Floor((try1 - 1 - cb0y) / twoppy);
            var jstart = (int)Math.Floor((trx0 - cb0x) / twoppx);
            var jend = (int)Math.Floor((trx1 - 1 - cb0x) / twoppx);

            int acb0x, acb0y;

            var root = infoSrc.GetAnSubbandTree(t, c);
            SubbandAn? sb = null;

            int p0x, p0y, p1x, p1y; // Precinct projection in subband
            int s0x, s0y, s1x, s1y; // Active subband portion
            int cw, ch;
            int kstart, kend, lstart, lend, k0, l0;
            int prg_ulx, prg_uly;
            var prg_w = (int)twoppx << ndl;
            var prg_h = (int)twoppy << ndl;

            CBlkCoordInfo cb;

            for (var i = istart; i <= iend; i++)
            {
                // Vertical precincts
                for (var j = jstart; j <= jend; j++, nPrec++)
                {
                    // Horizontal precincts
                    if (j == jstart && (trx0 - cb0x) % (xrsiz * ((int)twoppx)) != 0)
                    {
                        prg_ulx = tx0;
                    }
                    else
                    {
                        prg_ulx = cb0x + j * xrsiz * ((int)twoppx << ndl);
                    }
                    if (i == istart && (try0 - cb0y) % (yrsiz * ((int)twoppy)) != 0)
                    {
                        prg_uly = ty0;
                    }
                    else
                    {
                        prg_uly = cb0y + i * yrsiz * ((int)twoppy << ndl);
                    }

                    ppinfo[t][c][r][nPrec] = new PrecInfo(r, (int)(cb0x + j * twoppx), (int)(cb0y + i * twoppy), (int)twoppx, (int)twoppy, prg_ulx, prg_uly, prg_w, prg_h);

                    if (r == 0)
                    {
                        // LL subband
                        acb0x = cb0x;
                        acb0y = cb0y;

                        p0x = acb0x + j * (int)twoppx;
                        p1x = p0x + (int)twoppx;
                        p0y = acb0y + i * (int)twoppy;
                        p1y = p0y + (int)twoppy;

                        sb = (SubbandAn)root.GetSubbandByIdx(0, 0);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;

                        // Code-blocks are located at (acb0x+k*cw,acb0y+l*ch)
                        cw = sb.nomCBlkW;
                        ch = sb.nomCBlkH;
                        k0 = (int)Math.Floor((sb.ulcy - acb0y) / (double)ch);
                        kstart = (int)Math.Floor((s0y - acb0y) / (double)ch);
                        kend = (int)Math.Floor((s1y - 1 - acb0y) / (double)ch);
                        l0 = (int)Math.Floor((sb.ulcx - acb0x) / (double)cw);
                        lstart = (int)Math.Floor((s0x - acb0x) / (double)cw);
                        lend = (int)Math.Floor((s1x - 1 - acb0x) / (double)cw);

                        if (s1x - s0x <= 0 || s1y - s0y <= 0)
                        {
                            ppinfo[t][c][r][nPrec].nblk[0] = 0;
                            ttIncl[t][c][r][nPrec][0] = new TagTreeEncoder(0, 0);
                            ttMaxBP[t][c][r][nPrec][0] = new TagTreeEncoder(0, 0);
                        }
                        else
                        {
                            ttIncl[t][c][r][nPrec][0] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            ttMaxBP[t][c][r][nPrec][0] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            var tmpArray = new CBlkCoordInfo[kend - kstart + 1][];
                            for (var i2 = 0; i2 < kend - kstart + 1; i2++)
                            {
                                tmpArray[i2] = new CBlkCoordInfo[lend - lstart + 1];
                            }
                            ppinfo[t][c][r][nPrec].cblk[0] = tmpArray;
                            ppinfo[t][c][r][nPrec].nblk[0] = (kend - kstart + 1) * (lend - lstart + 1);

                            for (var k = kstart; k <= kend; k++)
                            {
                                // Vertical cblks
                                for (var l = lstart; l <= lend; l++)
                                {
                                    // Horiz. cblks

                                    cb = new CBlkCoordInfo(k - k0, l - l0);
                                    ppinfo[t][c][r][nPrec].cblk[0][k - kstart][l - lstart] = cb;
                                } // Horizontal code-blocks
                            } // Vertical code-blocks
                        }
                    }
                    else
                    {
                        // HL, LH and HH subbands
                        // HL subband
                        acb0x = 0;
                        acb0y = cb0y;

                        p0x = acb0x + j * twoppx2;
                        p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2;
                        p1y = p0y + twoppy2;

                        sb = (SubbandAn)root.GetSubbandByIdx(r, 1);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;

                        // Code-blocks are located at (acb0x+k*cw,acb0y+l*ch)
                        cw = sb.nomCBlkW;
                        ch = sb.nomCBlkH;
                        k0 = (int)Math.Floor((sb.ulcy - acb0y) / (double)ch);
                        kstart = (int)Math.Floor((s0y - acb0y) / (double)ch);
                        kend = (int)Math.Floor((s1y - 1 - acb0y) / (double)ch);
                        l0 = (int)Math.Floor((sb.ulcx - acb0x) / (double)cw);
                        lstart = (int)Math.Floor((s0x - acb0x) / (double)cw);
                        lend = (int)Math.Floor((s1x - 1 - acb0x) / (double)cw);

                        if (s1x - s0x <= 0 || s1y - s0y <= 0)
                        {
                            ppinfo[t][c][r][nPrec].nblk[1] = 0;
                            ttIncl[t][c][r][nPrec][1] = new TagTreeEncoder(0, 0);
                            ttMaxBP[t][c][r][nPrec][1] = new TagTreeEncoder(0, 0);
                        }
                        else
                        {
                            ttIncl[t][c][r][nPrec][1] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            ttMaxBP[t][c][r][nPrec][1] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            var tmpArray2 = new CBlkCoordInfo[kend - kstart + 1][];
                            for (var i3 = 0; i3 < kend - kstart + 1; i3++)
                            {
                                tmpArray2[i3] = new CBlkCoordInfo[lend - lstart + 1];
                            }
                            ppinfo[t][c][r][nPrec].cblk[1] = tmpArray2;
                            ppinfo[t][c][r][nPrec].nblk[1] = (kend - kstart + 1) * (lend - lstart + 1);

                            for (var k = kstart; k <= kend; k++)
                            {
                                // Vertical cblks
                                for (var l = lstart; l <= lend; l++)
                                {
                                    // Horiz. cblks
                                    cb = new CBlkCoordInfo(k - k0, l - l0);
                                    ppinfo[t][c][r][nPrec].cblk[1][k - kstart][l - lstart] = cb;
                                } // Horizontal code-blocks
                            } // Vertical code-blocks
                        }

                        // LH subband
                        acb0x = cb0x;
                        acb0y = 0;

                        p0x = acb0x + j * twoppx2;
                        p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2;
                        p1y = p0y + twoppy2;

                        sb = (SubbandAn)root.GetSubbandByIdx(r, 2);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;

                        // Code-blocks are located at (acb0x+k*cw,acb0y+l*ch)
                        cw = sb.nomCBlkW;
                        ch = sb.nomCBlkH;
                        k0 = (int)Math.Floor((sb.ulcy - acb0y) / (double)ch);
                        kstart = (int)Math.Floor((s0y - acb0y) / (double)ch);
                        kend = (int)Math.Floor((s1y - 1 - acb0y) / (double)ch);
                        l0 = (int)Math.Floor((sb.ulcx - acb0x) / (double)cw);
                        lstart = (int)Math.Floor((s0x - acb0x) / (double)cw);
                        lend = (int)Math.Floor((s1x - 1 - acb0x) / (double)cw);

                        if (s1x - s0x <= 0 || s1y - s0y <= 0)
                        {
                            ppinfo[t][c][r][nPrec].nblk[2] = 0;
                            ttIncl[t][c][r][nPrec][2] = new TagTreeEncoder(0, 0);
                            ttMaxBP[t][c][r][nPrec][2] = new TagTreeEncoder(0, 0);
                        }
                        else
                        {
                            ttIncl[t][c][r][nPrec][2] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            ttMaxBP[t][c][r][nPrec][2] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            var tmpArray3 = new CBlkCoordInfo[kend - kstart + 1][];
                            for (var i4 = 0; i4 < kend - kstart + 1; i4++)
                            {
                                tmpArray3[i4] = new CBlkCoordInfo[lend - lstart + 1];
                            }
                            ppinfo[t][c][r][nPrec].cblk[2] = tmpArray3;
                            ppinfo[t][c][r][nPrec].nblk[2] = (kend - kstart + 1) * (lend - lstart + 1);

                            for (var k = kstart; k <= kend; k++)
                            {
                                // Vertical cblks
                                for (var l = lstart; l <= lend; l++)
                                {
                                    // Horiz cblks
                                    cb = new CBlkCoordInfo(k - k0, l - l0);
                                    ppinfo[t][c][r][nPrec].cblk[2][k - kstart][l - lstart] = cb;
                                } // Horizontal code-blocks
                            } // Vertical code-blocks
                        }

                        // HH subband
                        acb0x = 0;
                        acb0y = 0;

                        p0x = acb0x + j * twoppx2;
                        p1x = p0x + twoppx2;
                        p0y = acb0y + i * twoppy2;
                        p1y = p0y + twoppy2;

                        sb = (SubbandAn)root.GetSubbandByIdx(r, 3);
                        s0x = (p0x < sb.ulcx) ? sb.ulcx : p0x;
                        s1x = (p1x > sb.ulcx + sb.w) ? sb.ulcx + sb.w : p1x;
                        s0y = (p0y < sb.ulcy) ? sb.ulcy : p0y;
                        s1y = (p1y > sb.ulcy + sb.h) ? sb.ulcy + sb.h : p1y;

                        // Code-blocks are located at (acb0x+k*cw,acb0y+l*ch)
                        cw = sb.nomCBlkW;
                        ch = sb.nomCBlkH;
                        k0 = (int)Math.Floor((sb.ulcy - acb0y) / (double)ch);
                        kstart = (int)Math.Floor((s0y - acb0y) / (double)ch);
                        kend = (int)Math.Floor((s1y - 1 - acb0y) / (double)ch);
                        l0 = (int)Math.Floor((sb.ulcx - acb0x) / (double)cw);
                        lstart = (int)Math.Floor((s0x - acb0x) / (double)cw);
                        lend = (int)Math.Floor((s1x - 1 - acb0x) / (double)cw);

                        if (s1x - s0x <= 0 || s1y - s0y <= 0)
                        {
                            ppinfo[t][c][r][nPrec].nblk[3] = 0;
                            ttIncl[t][c][r][nPrec][3] = new TagTreeEncoder(0, 0);
                            ttMaxBP[t][c][r][nPrec][3] = new TagTreeEncoder(0, 0);
                        }
                        else
                        {
                            ttIncl[t][c][r][nPrec][3] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            ttMaxBP[t][c][r][nPrec][3] = new TagTreeEncoder(kend - kstart + 1, lend - lstart + 1);
                            var tmpArray4 = new CBlkCoordInfo[kend - kstart + 1][];
                            for (var i5 = 0; i5 < kend - kstart + 1; i5++)
                            {
                                tmpArray4[i5] = new CBlkCoordInfo[lend - lstart + 1];
                            }
                            ppinfo[t][c][r][nPrec].cblk[3] = tmpArray4;
                            ppinfo[t][c][r][nPrec].nblk[3] = (kend - kstart + 1) * (lend - lstart + 1);

                            for (var k = kstart; k <= kend; k++)
                            {
                                // Vertical cblks
                                for (var l = lstart; l <= lend; l++)
                                {
                                    // Horiz cblks
                                    cb = new CBlkCoordInfo(k - k0, l - l0);
                                    ppinfo[t][c][r][nPrec].cblk[3][k - kstart][l - lstart] = cb;
                                } // Horizontal code-blocks
                            } // Vertical code-blocks
                        }
                    }
                } // Horizontal precincts
            } // Vertical precincts
        }

        /// <summary> Encodes a packet and returns the buffer containing the encoded packet
        /// header. The code-blocks appear in a 3D array of CBlkRateDistStats,
        /// 'cbs'. The first index is the tile index in lexicographical order, the
        /// second index is the subband index (as defined in the Subband class),
        /// and the third index is the code-block index (whithin the subband tile)
        /// in lexicographical order as well. The indexes of the new truncation
        /// points for each code-block are specified by the 3D array of int
        /// 'tIndx'. The indices of this array are the same as for cbs. The
        /// truncation point indices in 'tIndx' are the indices of the elements of
        /// the 'truncIdxs' array, of the CBlkRateDistStats class, that give the
        /// real truncation points. If a truncation point index is negative it
        /// means that the code-block has not been included in any layer yet. If
        /// the truncation point is less than or equal to the highest truncation
        /// point used in previous layers then the code-block is not included in
        /// the packet. Otherwise, if larger, the code-block is included in the
        /// packet. The body of the packet can be obtained with the
        /// getLastBodyBuf() and getLastBodyLen() methods.
        /// 
        /// Layers must be coded in increasing order, in consecutive manner, for
        /// each tile, component and resolution level (e.g., layer 1, then layer 2,
        /// etc.). For different tile, component and/or resolution level no
        /// particular order must be followed.
        /// 
        /// </summary>
        /// <param name="ly">The layer index (starts at 1).
        /// 
        /// </param>
        /// <param name="c">The component index.
        /// 
        /// </param>
        /// <param name="r">The resolution level
        /// 
        /// </param>
        /// <param name="t">Index of the current tile
        /// 
        /// </param>
        /// <param name="cbs">The 3D array of coded code-blocks.
        /// 
        /// </param>
        /// <param name="tIndx">The truncation point indices for each code-block.
        /// 
        /// </param>
        /// <param name="hbuf">The header buffer. If null a new BitOutputBuffer is created
        /// and returned. This buffer is reset before anything is written to it.
        /// 
        /// </param>
        /// <param name="bbuf">The body buffer. If null a new one is created. If not large
        /// enough a new one is created.
        /// 
        /// </param>
        /// <param name="pIdx">The precinct index.
        /// 
        /// </param>
        /// <returns> The buffer containing the packet header.
        /// 
        /// </returns>
        public virtual BitOutputBuffer encodePacket(int ly, int c, int r, int t, CBlkRateDistStats[][] cbs, int[][] tIndx, BitOutputBuffer? hbuf, byte[]? bbuf, int pIdx)
        {
            int b, i, maxi;
            int ncb;
            int thmax;
            int newtp;
            int cblen;
            int prednbits, nbits; // deltabits removed
            TagTreeEncoder cur_ttIncl, cur_ttMaxBP; // inclusion and bit-depth tag
                                                    // trees 
            int[] cur_prevtIdxs; // last encoded truncation points
            CBlkRateDistStats[] cur_cbs;
            int[] cur_tIndx; // truncation points to encode
            var minsb = (r == 0) ? 0 : 1;
            var maxsb = (r == 0) ? 1 : 4;
            Coord? cbCoord = null;
            var root = infoSrc.GetAnSubbandTree(t, c);
            SubbandAn sb;
            roiInPkt = false;
            roiLen = 0;
            int mend, nend;

            // Checks if a precinct with such an index exists in this resolution
            // level
            if (pIdx >= ppinfo[t][c][r].Length)
            {
                packetWritable = false;
                return hbuf;
            }
            var prec = ppinfo[t][c][r][pIdx];

            // First, we check if packet is empty (i.e precinct 'pIdx' has no
            // code-block in any of the subbands)
            var isPrecVoid = true;

            for (var s = minsb; s < maxsb; s++)
            {
                if (prec.nblk[s] == 0)
                {
                    // The precinct has no code-block in this subband.
                    continue;
                }
                else
                {
                    // The precinct is not empty in at least one subband ->
                    // stop
                    isPrecVoid = false;
                    break;
                }
            }

            if (isPrecVoid)
            {
                packetWritable = true;

                if (hbuf == null)
                {
                    hbuf = new BitOutputBuffer();
                }
                else
                {
                    hbuf!.reset();
                }
                if (bbuf == null)
                {
                    lbbuf = bbuf = Array.Empty<byte>();
                }
                hbuf!.writeBit(0);
                lblen = 0;

                return hbuf;
            }

            if (hbuf == null)
            {
                hbuf = new BitOutputBuffer();
            }
            else
            {
                hbuf!.reset();
            }

            // Invalidate last body buffer
            lbbuf = null;
            lblen = 0;

            // Signal that packet is present
            hbuf!.writeBit(1);

            for (var s = minsb; s < maxsb; s++)
            {
                // Loop on subbands
                sb = (SubbandAn)root.GetSubbandByIdx(r, s);

                // Go directly to next subband if the precinct has no code-block
                // in the current one.
                if (prec.nblk[s] == 0)
                {
                    continue;
                }

                cur_ttIncl = ttIncl[t][c][r][pIdx][s];
                cur_ttMaxBP = ttMaxBP[t][c][r][pIdx][s];
                cur_prevtIdxs = prevtIdxs[t][c][r][s];
                cur_cbs = cbs[s];
                cur_tIndx = tIndx[s];

                // Set tag tree values for code-blocks in this precinct
                mend = (prec.cblk[s] == null) ? 0 : prec.cblk[s].Length;
                for (var m = 0; m < mend; m++)
                {
                    nend = (prec.cblk[s][m] == null) ? 0 : prec.cblk[s][m].Length;
                    for (var n = 0; n < nend; n++)
                    {
                        cbCoord = prec.cblk[s][m][n].idx;
                        b = cbCoord.x + cbCoord.y * sb.numCb.x;

                        if (cur_tIndx[b] > cur_prevtIdxs[b] && cur_prevtIdxs[b] < 0)
                        {
                            // First inclusion
                            cur_ttIncl.SetValue(m, n, ly - 1);
                        }
                        if (ly == 1)
                        {
                            // First layer, need to set the skip of MSBP
                            cur_ttMaxBP.SetValue(m, n, cur_cbs[b].skipMSBP);
                        }
                    }
                }

                // Now encode the information
                for (var m = 0; m < prec.cblk[s].Length; m++)
                {
                    // Vertical code-blocks
                    for (var n = 0; n < prec.cblk[s][m].Length; n++)
                    {
                        // Horiz. cblks
                        cbCoord = prec.cblk[s][m][n].idx;
                        b = cbCoord.x + cbCoord.y * sb.numCb.x;

                        // 1) Inclusion information
                        if (cur_tIndx[b] > cur_prevtIdxs[b])
                        {
                            // Code-block included in this layer
                            if (cur_prevtIdxs[b] < 0)
                            {
                                // First inclusion
                                // Encode layer info
                                cur_ttIncl.encode(m, n, ly, hbuf);

                                // 2) Max bitdepth info. Encode value
                                thmax = cur_cbs[b].skipMSBP + 1;
                                for (i = 1; i <= thmax; i++)
                                {
                                    cur_ttMaxBP.encode(m, n, i, hbuf);
                                }

                                // Count body size for packet
                                lblen += cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_tIndx[b]]];
                            }
                            else
                            {
                                // Already in previous layer
                                // Send "1" bit
                                hbuf!.writeBit(1);
                                // Count body size for packet
                                lblen += cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_tIndx[b]]] - cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_prevtIdxs[b]]];
                            }

                            // 3) Truncation point information
                            if (cur_prevtIdxs[b] < 0)
                            {
                                newtp = cur_cbs[b].truncIdxs![cur_tIndx[b]];
                            }
                            else
                            {
                                newtp = cur_cbs[b].truncIdxs![cur_tIndx[b]] - cur_cbs[b].truncIdxs![cur_prevtIdxs[b]] - 1;
                            }

                            // Mix of switch and if is faster
                            switch (newtp)
                            {

                                case 0:
                                    hbuf!.writeBit(0); // Send one "0" bit
                                    break;

                                case 1:
                                    hbuf!.writeBits(2, 2); // Send one "1" and one "0"
                                    break;

                                case 2:
                                case 3:
                                case 4:
                                    // Send two "1" bits followed by 2 bits
                                    // representation of newtp-2
                                    hbuf!.writeBits((3 << 2) | (newtp - 2), 4);
                                    break;

                                default:
                                    if (newtp <= 35)
                                    {
                                        // Send four "1" bits followed by a five bits
                                        // representation of newtp-5
                                        hbuf!.writeBits((15 << 5) | (newtp - 5), 9);
                                    }
                                    else if (newtp <= 163)
                                    {
                                        // Send nine "1" bits followed by a seven bits
                                        // representation of newtp-36
                                        hbuf!.writeBits((511 << 7) | (newtp - 36), 16);
                                    }
                                    else
                                    {
                                        throw new ArithmeticException("Maximum number " + "of truncation " + "points exceeded");
                                    }
                                    break;

                            }
                        }
                        else
                        {
                            // Block not included in this layer
                            if (cur_prevtIdxs[b] >= 0)
                            {
                                // Already in previous layer. Send "0" bit
                                hbuf!.writeBit(0);
                            }
                            else
                            {
                                // Not in any previous layers
                                cur_ttIncl.encode(m, n, ly, hbuf);
                            }
                            // Go to the next one.
                            continue;
                        }

                        // Code-block length

                        // We need to compute the maximum number of bits needed to
                        // signal the length of each terminated segment and the
                        // final truncation point.
                        newtp = 1;
                        maxi = cur_cbs[b].truncIdxs![cur_tIndx[b]];
                        cblen = (cur_prevtIdxs[b] < 0) ? 0 : cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_prevtIdxs[b]]];

                        // Loop on truncation points
                        i = (cur_prevtIdxs[b] < 0) ? 0 : cur_cbs[b].truncIdxs![cur_prevtIdxs[b]] + 1;
                        var minbits = 0;
                        for (; i < maxi; i++, newtp++)
                        {
                            // If terminated truncation point calculate length
                            if (cur_cbs[b].isTermPass != null && cur_cbs[b].isTermPass![i])
                            {

                                // Calculate length
                                cblen = cur_cbs[b].truncRates![i] - cblen;

                                // Calculate number of needed bits
                                prednbits = lblock[t][c][r][s][b] + MathUtil.log2(newtp);
                                minbits = ((cblen > 0) ? MathUtil.log2(cblen) : 0) + 1;

                                // Update Lblock increment if needed
                                for (var j = prednbits; j < minbits; j++)
                                {
                                    lblock[t][c][r][s][b]++;
                                    hbuf!.writeBit(1);
                                }
                                // Initialize for next length
                                newtp = 0;
                                cblen = cur_cbs[b].truncRates![i];
                            }
                        }

                        // Last truncation point length always sent

                        // Calculate length
                        cblen = cur_cbs[b].truncRates![i] - cblen;

                        // Calculate number of bits
                        prednbits = lblock[t][c][r][s][b] + MathUtil.log2(newtp);
                        minbits = ((cblen > 0) ? MathUtil.log2(cblen) : 0) + 1;
                        // Update Lblock increment if needed
                        for (var j = prednbits; j < minbits; j++)
                        {
                            lblock[t][c][r][s][b]++;
                            hbuf!.writeBit(1);
                        }

                        // End of comma-code increment
                        hbuf!.writeBit(0);

                        // There can be terminated several segments, send length
                        // info for all terminated truncation points in addition
                        // to final one
                        newtp = 1;
                        maxi = cur_cbs[b].truncIdxs![cur_tIndx[b]];
                        cblen = (cur_prevtIdxs[b] < 0) ? 0 : cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_prevtIdxs[b]]];
                        // Loop on truncation points and count the groups
                        i = (cur_prevtIdxs[b] < 0) ? 0 : cur_cbs[b].truncIdxs![cur_prevtIdxs[b]] + 1;
                        for (; i < maxi; i++, newtp++)
                        {
                            // If terminated truncation point, send length
                            if (cur_cbs[b].isTermPass != null && cur_cbs[b].isTermPass![i])
                            {

                                cblen = cur_cbs[b].truncRates![i] - cblen;
                                nbits = MathUtil.log2(newtp) + lblock[t][c][r][s][b];
                                hbuf!.writeBits(cblen, nbits);

                                // Initialize for next length
                                newtp = 0;
                                cblen = cur_cbs[b].truncRates![i];
                            }
                        }
                        // Last truncation point length is always signalled
                        // First calculate number of bits needed to signal
                        // Calculate length
                        cblen = cur_cbs[b].truncRates![i] - cblen;
                        nbits = MathUtil.log2(newtp) + lblock[t][c][r][s][b];
                        hbuf!.writeBits(cblen, nbits);
                    } // End loop on horizontal code-blocks
                } // End loop on vertical code-blocks
            } // End loop on subband

            // -> Copy the data to the body buffer

            // Ensure size for body data
            if (bbuf == null || bbuf.Length < lblen)
            {
                if (lblen == 0)
                {
                    bbuf = Array.Empty<byte>();
                }
                else
                {
                    bbuf = new byte[lblen];
                }
            }
            lbbuf = bbuf;
            lblen = 0;

            for (var s = minsb; s < maxsb; s++)
            {
                // Loop on subbands
                sb = (SubbandAn)root.GetSubbandByIdx(r, s);

                cur_prevtIdxs = prevtIdxs[t][c][r][s];
                cur_cbs = cbs[s];
                cur_tIndx = tIndx[s];
                ncb = cur_prevtIdxs.Length;

                mend = (prec.cblk[s] == null) ? 0 : prec.cblk[s].Length;
                for (var m = 0; m < mend; m++)
                {
                    // Vertical code-blocks
                    nend = (prec.cblk[s][m] == null) ? 0 : prec.cblk[s][m].Length;
                    for (var n = 0; n < nend; n++)
                    {
                        // Horiz. cblks
                        cbCoord = prec.cblk[s][m][n].idx;
                        b = cbCoord.x + cbCoord.y * sb.numCb.x;

                        if (cur_tIndx[b] > cur_prevtIdxs[b])
                        {

                            // Block included in this precinct -> Copy data to
                            // body buffer and get code-size
                            if (cur_prevtIdxs[b] < 0)
                            {
                                cblen = cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_tIndx[b]]];
                                Array.Copy(cur_cbs[b].data!, 0, lbbuf, lblen, cblen);
                            }
                            else
                            {
                                cblen = cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_tIndx[b]]] - cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_prevtIdxs[b]]];
                                Array.Copy(cur_cbs[b].data!, cur_cbs[b].truncRates![cur_cbs[b].truncIdxs![cur_prevtIdxs[b]]], lbbuf, lblen, cblen);
                            }
                            lblen += cblen;

                            // Verifies if this code-block contains new ROI
                            // information
                            if (cur_cbs[b].nROIcoeff != 0 && (cur_prevtIdxs[b] == -1 || cur_cbs[b].truncIdxs![cur_prevtIdxs[b]] <= cur_cbs[b].nROIcp - 1))
                            {
                                roiInPkt = true;
                                roiLen = lblen;
                            }

                            // Update truncation point
                            cur_prevtIdxs[b] = cur_tIndx[b];
                        }
                    } // End loop on horizontal code-blocks
                } // End loop on vertical code-blocks
            } // End loop on subbands

            packetWritable = true;

            // Must never happen
            if (hbuf.Length == 0)
            {
                throw new InvalidOperationException("You have found a bug in PktEncoder, method:" + " encodePacket");
            }

            return hbuf;
        }

        /// <summary> Saves the current state of this object. The last saved state
        /// can be restored with the restore() method.
        /// 
        /// </summary>
        /// <seealso cref="restore" />
        public virtual void save()
        {
            int maxsbi, minsbi;

            // Have we done any save yet?
            if (bak_lblock == null)
            {
                // Allocate backup buffers
                bak_lblock = new int[ttIncl.Length][][][][];
                bak_prevtIdxs = new int[ttIncl.Length][][][][];
                for (var t = ttIncl.Length - 1; t >= 0; t--)
                {
                    bak_lblock[t] = new int[ttIncl[t].Length][][][];
                    bak_prevtIdxs[t] = new int[ttIncl[t].Length][][][];
                    for (var c = ttIncl[t].Length - 1; c >= 0; c--)
                    {
                        bak_lblock[t][c] = new int[lblock[t][c].Length][][];
                        bak_prevtIdxs[t][c] = new int[ttIncl[t][c].Length][][];
                        for (var r = lblock[t][c].Length - 1; r >= 0; r--)
                        {
                            bak_lblock[t][c][r] = new int[lblock[t][c][r].Length][];
                            bak_prevtIdxs[t][c][r] = new int[prevtIdxs[t][c][r].Length][];
                            minsbi = (r == 0) ? 0 : 1;
                            maxsbi = (r == 0) ? 1 : 4;
                            for (var s = minsbi; s < maxsbi; s++)
                            {
                                bak_lblock[t][c][r][s] = new int[lblock[t][c][r][s].Length];
                                bak_prevtIdxs[t][c][r][s] = new int[prevtIdxs[t][c][r][s].Length];
                            }
                        }
                    }
                }
            }

            //-- Save the data

            // Use reference caches to minimize array access overhead
            TagTreeEncoder[][][] ttIncl_t_c, ttMaxBP_t_c;
            TagTreeEncoder[][] ttIncl_t_c_r, ttMaxBP_t_c_r;
            int[][][] lblock_t_c, bak_lblock_t_c;
            int[][] prevtIdxs_t_c_r, bak_prevtIdxs_t_c_r;

            // Loop on tiles
            for (var t = ttIncl.Length - 1; t >= 0; t--)
            {
                // Loop on components
                for (var c = ttIncl[t].Length - 1; c >= 0; c--)
                {
                    // Initialize reference caches
                    lblock_t_c = lblock[t][c];
                    bak_lblock_t_c = bak_lblock[t][c];
                    ttIncl_t_c = ttIncl[t][c];
                    ttMaxBP_t_c = ttMaxBP[t][c];
                    // Loop on resolution levels
                    for (var r = lblock_t_c.Length - 1; r >= 0; r--)
                    {
                        // Initialize reference caches
                        ttIncl_t_c_r = ttIncl_t_c[r];
                        ttMaxBP_t_c_r = ttMaxBP_t_c[r];
                        prevtIdxs_t_c_r = prevtIdxs[t][c][r];
                        bak_prevtIdxs_t_c_r = bak_prevtIdxs[t][c][r];

                        // Loop on subbands
                        minsbi = (r == 0) ? 0 : 1;
                        maxsbi = (r == 0) ? 1 : 4;
                        for (var s = minsbi; s < maxsbi; s++)
                        {
                            // Save 'lblock'
                            Array.Copy(lblock_t_c[r][s], 0, bak_lblock_t_c[r][s], 0, lblock_t_c[r][s].Length);
                            // Save 'prevtIdxs'
                            Array.Copy(prevtIdxs_t_c_r[s], 0, bak_prevtIdxs_t_c_r[s], 0, prevtIdxs_t_c_r[s].Length);
                        } // End loop on subbands

                        // Loop on precincts
                        for (var p = ppinfo[t][c][r].Length - 1; p >= 0; p--)
                        {
                            if (p < ttIncl_t_c_r.Length)
                            {
                                // Loop on subbands
                                for (var s = minsbi; s < maxsbi; s++)
                                {
                                    ttIncl_t_c_r[p][s].save();
                                    ttMaxBP_t_c_r[p][s].save();
                                } // End loop on subbands
                            }
                        } // End loop on precincts
                    } // End loop on resolutions
                } // End loop on components
            } // End loop on tiles

            // Set the saved state
            saved = true;
        }

        /// <summary> Restores the last saved state of this object. An
        /// IllegalArgumentException is thrown if no state has been saved.
        /// 
        /// </summary>
        /// <seealso cref="save" />
        public virtual void restore()
        {
            int maxsbi, minsbi;

            if (!saved)
            {
                throw new ArgumentException();
            }

            // Invalidate last encoded body buffer
            lbbuf = null;

            //-- Restore tha data

            // Use reference caches to minimize array access overhead
            TagTreeEncoder[][][] ttIncl_t_c, ttMaxBP_t_c;
            TagTreeEncoder[][] ttIncl_t_c_r, ttMaxBP_t_c_r;
            int[][][] lblock_t_c, bak_lblock_t_c;
            int[][] prevtIdxs_t_c_r, bak_prevtIdxs_t_c_r;

            // Loop on tiles
            for (var t = ttIncl.Length - 1; t >= 0; t--)
            {
                // Loop on components
                for (var c = ttIncl[t].Length - 1; c >= 0; c--)
                {
                    // Initialize reference caches
                    lblock_t_c = lblock[t][c];
                    bak_lblock_t_c = bak_lblock[t][c];
                    ttIncl_t_c = ttIncl[t][c];
                    ttMaxBP_t_c = ttMaxBP[t][c];
                    // Loop on resolution levels
                    for (var r = lblock_t_c.Length - 1; r >= 0; r--)
                    {
                        // Initialize reference caches
                        ttIncl_t_c_r = ttIncl_t_c[r];
                        ttMaxBP_t_c_r = ttMaxBP_t_c[r];
                        prevtIdxs_t_c_r = prevtIdxs[t][c][r];
                        bak_prevtIdxs_t_c_r = bak_prevtIdxs[t][c][r];

                        // Loop on subbands
                        minsbi = (r == 0) ? 0 : 1;
                        maxsbi = (r == 0) ? 1 : 4;
                        for (var s = minsbi; s < maxsbi; s++)
                        {
                            // Restore 'lblock'
                            Array.Copy(bak_lblock_t_c[r][s], 0, lblock_t_c[r][s], 0, lblock_t_c[r][s].Length);
                            // Restore 'prevtIdxs'
                            Array.Copy(bak_prevtIdxs_t_c_r[s], 0, prevtIdxs_t_c_r[s], 0, prevtIdxs_t_c_r[s].Length);
                        } // End loop on subbands

                        // Loop on precincts
                        for (var p = ppinfo[t][c][r].Length - 1; p >= 0; p--)
                        {
                            if (p < ttIncl_t_c_r.Length)
                            {
                                // Loop on subbands
                                for (var s = minsbi; s < maxsbi; s++)
                                {
                                    ttIncl_t_c_r[p][s].restore();
                                    ttMaxBP_t_c_r[p][s].restore();
                                } // End loop on subbands
                            }
                        } // End loop on precincts
                    } // End loop on resolution levels
                } // End loop on components
            } // End loop on tiles
        }

        /// <summary> Resets the state of the object to the initial state, as if the object
        /// was just created.
        /// 
        /// </summary>
        public virtual void reset()
        {
            int maxsbi, minsbi;

            // Invalidate save
            saved = false;
            // Invalidate last encoded body buffer
            lbbuf = null;

            // Reinitialize each element in the arrays

            // Use reference caches to minimize array access overhead
            TagTreeEncoder[][][] ttIncl_t_c, ttMaxBP_t_c;
            TagTreeEncoder[][] ttIncl_t_c_r, ttMaxBP_t_c_r;
            int[][][] lblock_t_c;
            int[][] prevtIdxs_t_c_r;

            // Loop on tiles
            for (var t = ttIncl.Length - 1; t >= 0; t--)
            {
                // Loop on components
                for (var c = ttIncl[t].Length - 1; c >= 0; c--)
                {
                    // Initialize reference caches
                    lblock_t_c = lblock[t][c];
                    ttIncl_t_c = ttIncl[t][c];
                    ttMaxBP_t_c = ttMaxBP[t][c];
                    // Loop on resolution levels
                    for (var r = lblock_t_c.Length - 1; r >= 0; r--)
                    {
                        // Initialize reference caches
                        ttIncl_t_c_r = ttIncl_t_c[r];
                        ttMaxBP_t_c_r = ttMaxBP_t_c[r];
                        prevtIdxs_t_c_r = prevtIdxs[t][c][r];

                        // Loop on subbands
                        minsbi = (r == 0) ? 0 : 1;
                        maxsbi = (r == 0) ? 1 : 4;
                        for (var s = minsbi; s < maxsbi; s++)
                        {
                            // Reset 'prevtIdxs'
                            ArrayUtil.intArraySet(prevtIdxs_t_c_r[s], -1);
                            // Reset 'lblock'
                            ArrayUtil.intArraySet(lblock_t_c[r][s], INIT_LBLOCK);
                        } // End loop on subbands

                        // Loop on precincts
                        for (var p = ppinfo[t][c][r].Length - 1; p >= 0; p--)
                        {
                            if (p < ttIncl_t_c_r.Length)
                            {
                                // Loop on subbands
                                for (var s = minsbi; s < maxsbi; s++)
                                {
                                    ttIncl_t_c_r[p][s].reset();
                                    ttMaxBP_t_c_r[p][s].reset();
                                } // End loop on subbands
                            }
                        } // End loop on precincts
                    } // End loop on resolution levels
                } // End loop on components
            } // End loop on tiles
        }

        /// <summary> Returns information about a given precinct
        /// 
        /// </summary>
        /// <param name="t">Tile index.
        /// 
        /// </param>
        /// <param name="c">Component index.
        /// 
        /// </param>
        /// <param name="r">Resolution level index.
        /// 
        /// </param>
        /// <param name="p">Precinct index
        /// 
        /// </param>
        public virtual PrecInfo GetPrecInfo(int t, int c, int r, int p)
        {
            return ppinfo[t][c][r][p];
        }
    }
}