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
using CoreJ2K.j2k.util;
using System;

namespace CoreJ2K.j2k.codestream.writer
{

    /// <summary> This class implements the tag tree encoder. A tag tree codes a 2D matrix of
    /// integer elements in an efficient way. The encoding procedure 'encode()'
    /// codes information about a value of the matrix, given a threshold. The
    /// procedure encodes the sufficient information to identify whether or not the
    /// value is greater than or equal to the threshold.
    /// 
    /// The tag tree saves encoded information to a BitOutputBuffer.
    /// 
    /// A particular and useful property of tag trees is that it is possible to
    /// change a value of the matrix, provided both new and old values of the
    /// element are both greater than or equal to the largest threshold which has
    /// yet been supplied to the coding procedure 'encode()'. This property can be
    /// exploited through the 'SetValue()' method.
    /// 
    /// This class allows saving the state of the tree at any point and
    /// restoring it at a later time, by calling save() and restore().
    /// 
    /// A tag tree can also be reused, or restarted, if one of the reset()
    /// methods is called.
    /// 
    /// The TagTreeDecoder class implements the tag tree decoder.
    /// 
    /// Tag trees that have one dimension, or both, as 0 are allowed for
    /// convenience. Of course no values can be set or coded in such cases.
    /// 
    /// </summary>
    /// <seealso cref="BitOutputBuffer" />
    /// <seealso cref="j2k.codestream.reader.TagTreeDecoder" />
    public class TagTreeEncoder
    {
        /// <summary> Returns the number of leafs along the horizontal direction.
        /// 
        /// </summary>
        /// <returns> The number of leafs along the horizontal direction.
        /// 
        /// </returns>
        public virtual int Width => w;

        /// <summary> Returns the number of leafs along the vertical direction.
        /// 
        /// </summary>
        /// <returns> The number of leafs along the vertical direction.
        /// 
        /// </returns>
        public virtual int Height => h;

        /// <summary> Sets the values of the leafs to the new set of values and updates the
        /// tag tree accordingly. No leaf can change its value if either the new or
        /// old value is smaller than largest threshold which has yet been supplied
        /// to 'encode()'. However such a leaf can keep its old value (i.e. new and
        /// old value must be identical.
        /// 
        /// This method is more efficient than the SetValue() method if a large
        /// proportion of the leafs change their value. Note that for leafs which
        /// don't have their value defined yet the value should be
        /// Integer.MAX_VALUE (which is the default initialization value).
        /// 
        /// </summary>
        /// <param name="val">The new values for the leafs, in lexicographical order.
        /// 
        /// </param>
        /// <seealso cref="SetValue" />
        public virtual int[] Values
        {
            set
            {
                int i, maxt;
                if (lvls == 0)
                {
                    // Can't set values on empty tree
                    throw new ArgumentException();
                }
                // Check the values
                maxt = treeS[lvls - 1][0];
                for (i = w * h - 1; i >= 0; i--)
                {
                    if ((treeV[0][i] < maxt || value[i] < maxt) && treeV[0][i] != value[i])
                    {
                        throw new ArgumentException();
                    }
                    // Update leaf value
                    treeV[0][i] = value[i];
                }
                // Recalculate tree at other levels
                recalcTreeV();
            }

        }

        /// <summary>The horizontal dimension of the base level </summary>
        protected internal int w;

        /// <summary>The vertical dimensions of the base level </summary>
        protected internal int h;

        /// <summary>The number of levels in the tag tree </summary>
        protected internal int lvls;

        /// <summary>The tag tree values. The first index is the level, starting at level 0
        /// (leafs). The second index is the element within the level, in
        /// lexicographical order. 
        /// </summary>
        protected internal int[][] treeV;

        /// <summary>The tag tree state. The first index is the level, starting at level 0
        /// (leafs). The second index is the element within the level, in
        /// lexicographical order. 
        /// </summary>
        protected internal int[][] treeS;

        /// <summary>The saved tag tree values. The first index is the level, starting at
        /// level 0 (leafs). The second index is the element within the level, in
        /// lexicographical order. 
        /// </summary>
        protected internal int[][] treeVbak;

        /// <summary>The saved tag tree state. The first index is the level, starting at
        /// level 0 (leafs). The second index is the element within the level, in
        /// lexicographical order. 
        /// </summary>
        protected internal int[][] treeSbak;

        /// <summary>The saved state. If true the values and states of the tree have been
        /// saved since the creation or last reset. 
        /// </summary>
        protected internal bool saved;

        /// <summary> Creates a tag tree encoder with 'w' elements along the horizontal
        /// dimension and 'h' elements along the vertical direction. The total
        /// number of elements is thus 'vdim' x 'hdim'.
        /// 
        /// The values of all elements are initialized to Integer.MAX_VALUE.
        /// 
        /// </summary>
        /// <param name="h">The number of elements along the horizontal direction.
        /// 
        /// </param>
        /// <param name="w">The number of elements along the vertical direction.
        /// 
        /// </param>
        public TagTreeEncoder(int h, int w)
        {
            int k;
            // Check arguments
            if (w < 0 || h < 0)
            {
                throw new ArgumentException();
            }
            // Initialize elements
            init(w, h);
            // Set values to max
            for (k = treeV.Length - 1; k >= 0; k--)
            {
                ArrayUtil.intArraySet(treeV[k], int.MaxValue);
            }
        }

        /// <summary> Creates a tag tree encoder with 'w' elements along the horizontal
        /// dimension and 'h' elements along the vertical direction. The total
        /// number of elements is thus 'vdim' x 'hdim'. The values of the leafs in
        /// the tag tree are initialized to the values of the 'val' array.
        /// 
        /// The values in the 'val' array are supposed to appear in
        /// lexicographical order, starting at index 0.
        /// 
        /// </summary>
        /// <param name="h">The number of elements along the horizontal direction.
        /// 
        /// </param>
        /// <param name="w">The number of elements along the vertical direction.
        /// 
        /// </param>
        /// <param name="val">The values with which initialize the leafs of the tag tree.
        /// 
        /// </param>
        public TagTreeEncoder(int h, int w, int[] val)
        {
            int k;
            // Check arguments
            if (w < 0 || h < 0 || val.Length < w * h)
            {
                throw new ArgumentException();
            }
            // Initialize elements
            init(w, h);
            // Update leaf values
            for (k = w * h - 1; k >= 0; k--)
            {
                treeV[0][k] = val[k];
            }
            // Calculate values at other levels
            recalcTreeV();
        }

        /// <summary> Initializes the variables of this class, given the dimensions at the
        /// base level (leaf level). All the state ('treeS' array) and values
        /// ('treeV' array) are intialized to 0. This method is called by the
        /// constructors.
        /// 
        /// </summary>
        /// <param name="w">The number of elements along the vertical direction.
        /// 
        /// </param>
        /// <param name="h">The number of elements along the horizontal direction.
        /// 
        /// </param>
        private void init(int w, int h)
        {
            int i;
            // Initialize dimensions
            this.w = w;
            this.h = h;
            // Calculate the number of levels
            if (w == 0 || h == 0)
            {
                lvls = 0;
            }
            else
            {
                lvls = 1;
                while (h != 1 || w != 1)
                {
                    // Loop until we reach root
                    w = (w + 1) >> 1;
                    h = (h + 1) >> 1;
                    lvls++;
                }
            }
            // Allocate tree values and states (no need to initialize to 0 since
            // it's the default)
            treeV = new int[lvls][];
            treeS = new int[lvls][];
            w = this.w;
            h = this.h;
            for (i = 0; i < lvls; i++)
            {
                treeV[i] = new int[h * w];
                treeS[i] = new int[h * w];
                w = (w + 1) >> 1;
                h = (h + 1) >> 1;
            }
        }

        /// <summary> Recalculates the values of the elements in the tag tree, in levels 1
        /// and up, based on the values of the leafs (level 0).
        /// 
        /// </summary>
        private void recalcTreeV()
        {
            int m, n, bi, lw, tm1, tm2, lh, k;
            // Loop on all other levels, updating minimum
            for (k = 0; k < lvls - 1; k++)
            {
                // Visit all elements in level
                lw = (w + (1 << k) - 1) >> k;
                lh = (h + (1 << k) - 1) >> k;
                for (m = ((lh >> 1) << 1) - 2; m >= 0; m -= 2)
                {
                    // All quads with 2 lines
                    for (n = ((lw >> 1) << 1) - 2; n >= 0; n -= 2)
                    {
                        // All quads with 2 columns
                        // Take minimum of 4 elements and put it in higher 
                        // level
                        bi = m * lw + n;
                        tm1 = (treeV[k][bi] < treeV[k][bi + 1]) ? treeV[k][bi] : treeV[k][bi + 1];
                        tm2 = (treeV[k][bi + lw] < treeV[k][bi + lw + 1]) ? treeV[k][bi + lw] : treeV[k][bi + lw + 1];
                        treeV[k + 1][(m >> 1) * ((lw + 1) >> 1) + (n >> 1)] = tm1 < tm2 ? tm1 : tm2;
                    }
                    // Now we may have quad with 1 column, 2 lines
                    if (lw % 2 != 0)
                    {
                        n = ((lw >> 1) << 1);
                        // Take minimum of 2 elements and put it in higher 
                        // level
                        bi = m * lw + n;
                        treeV[k + 1][(m >> 1) * ((lw + 1) >> 1) + (n >> 1)] = (treeV[k][bi] < treeV[k][bi + lw]) ? treeV[k][bi] : treeV[k][bi + lw];
                    }
                }
                // Now we may have quads with 1 line, 2 or 1 columns
                if (lh % 2 != 0)
                {
                    m = ((lh >> 1) << 1);
                    for (n = ((lw >> 1) << 1) - 2; n >= 0; n -= 2)
                    {
                        // All quads with 2 columns
                        // Take minimum of 2 elements and put it in higher 
                        // level
                        bi = m * lw + n;
                        treeV[k + 1][(m >> 1) * ((lw + 1) >> 1) + (n >> 1)] = (treeV[k][bi] < treeV[k][bi + 1]) ? treeV[k][bi] : treeV[k][bi + 1];
                    }
                    // Now we may have quad with 1 column, 1 line
                    if (lw % 2 != 0)
                    {
                        // Just copy the value
                        n = ((lw >> 1) << 1);
                        treeV[k + 1][(m >> 1) * ((lw + 1) >> 1) + (n >> 1)] = treeV[k][m * lw + n];
                    }
                }
            }
        }

        /// <summary> Changes the value of a leaf in the tag tree. The new and old values of
        /// the element must be not smaller than the largest threshold which has
        /// yet been supplied to 'encode()'.
        /// 
        /// </summary>
        /// <param name="m">The vertical index of the element.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the element.
        /// 
        /// </param>
        /// <param name="v">The new value of the element.
        /// 
        /// </param>
        public virtual void SetValue(int m, int n, int v)
        {
            int k, idx;
            // Check arguments
            if (lvls == 0 || n < 0 || n >= w || v < treeS[lvls - 1][0] || treeV[0][m * w + n] < treeS[lvls - 1][0])
            {
                throw new ArgumentException();
            }
            // Update the leaf value
            treeV[0][m * w + n] = v;
            // Update all parents
            for (k = 1; k < lvls; k++)
            {
                idx = (m >> k) * ((w + (1 << k) - 1) >> k) + (n >> k);
                if (v < treeV[k][idx])
                {
                    // We need to update minimum and continue checking
                    // in higher levels
                    treeV[k][idx] = v;
                }
                else
                {
                    // We are done: v is equal or less to minimum
                    // in this level, no other minimums to update.
                    break;
                }
            }
        }

        /// <summary> Encodes information for the specified element of the tree, given the
        /// threshold and sends it to the 'out' stream. The information that is
        /// coded is whether or not the value of the element is greater than or
        /// equal to the value of the threshold.
        /// 
        /// </summary>
        /// <param name="m">The vertical index of the element.
        /// 
        /// </param>
        /// <param name="n">The horizontal index of the element.
        /// 
        /// </param>
        /// <param name="t">The threshold to use for encoding. It must be non-negative.
        /// 
        /// </param>
        /// <param name="out">The stream where to write the coded information.
        /// 
        /// </param>
        public virtual void encode(int m, int n, int t, BitOutputBuffer outStream)
        {
            int k, ts, idx, tmin;

            // Check arguments
            if (m >= h || n >= w || t < 0)
            {
                throw new ArgumentException();
            }

            // Initialize
            k = lvls - 1;
            tmin = treeS[k][0];

            // Loop on levels
            while (true)
            {
                // Index of element in level 'k'
                idx = (m >> k) * ((w + (1 << k) - 1) >> k) + (n >> k);
                // Cache state
                ts = treeS[k][idx];
                if (ts < tmin)
                {
                    ts = tmin;
                }
                while (t > ts)
                {
                    if (treeV[k][idx] > ts)
                    {
                        outStream.writeBit(0); // Send '0' bit
                    }
                    else if (treeV[k][idx] == ts)
                    {
                        outStream.writeBit(1); // Send '1' bit
                    }
                    else
                    {
                        // we are done: set ts and get out of this while
                        ts = t;
                        break;
                    }
                    // Increment of treeS[k][idx]
                    ts++;
                }
                // Update state
                treeS[k][idx] = ts;
                // Update tmin or terminate
                if (k > 0)
                {
                    tmin = ts < treeV[k][idx] ? ts : treeV[k][idx];
                    k--;
                }
                else
                {
                    // Terminate
                    return;
                }
            }
        }

        /// <summary> Saves the current values and state of the tree. Calling restore()
        /// restores the tag tree the saved state.
        /// 
        /// </summary>
        /// <seealso cref="restore" />
        public virtual void save()
        {
            int k; // i removed

            if (treeVbak == null)
            {
                // Nothing saved yet
                // Allocate saved arrays
                // treeV and treeS have the same dimensions
                treeVbak = new int[lvls][];
                treeSbak = new int[lvls][];
                for (k = lvls - 1; k >= 0; k--)
                {
                    treeVbak[k] = new int[treeV[k].Length];
                    treeSbak[k] = new int[treeV[k].Length];
                }
            }

            // Copy the arrays
            for (k = treeV.Length - 1; k >= 0; k--)
            {
                Array.Copy(treeV[k], 0, treeVbak[k], 0, treeV[k].Length);
                Array.Copy(treeS[k], 0, treeSbak[k], 0, treeS[k].Length);
            }

            // Set saved state
            saved = true;
        }

        /// <summary> Restores the saved values and state of the tree. An
        /// IllegalArgumentException is thrown if the tree values and state have
        /// not been saved yet.
        /// 
        /// </summary>
        /// <seealso cref="save" />
        public virtual void restore()
        {
            int k; // i removed

            if (!saved)
            {
                // Nothing saved yet
                throw new ArgumentException();
            }

            // Copy the arrays
            for (k = lvls - 1; k >= 0; k--)
            {
                Array.Copy(treeVbak[k], 0, treeV[k], 0, treeV[k].Length);
                Array.Copy(treeSbak[k], 0, treeS[k], 0, treeS[k].Length);
            }
        }

        /// <summary> Resets the tree values and state. All the values are set to
        /// Integer.MAX_VALUE and the states to 0.
        /// 
        /// </summary>
        public virtual void reset()
        {
            int k;
            // Set all values to Integer.MAX_VALUE
            // and states to 0
            for (k = lvls - 1; k >= 0; k--)
            {
                ArrayUtil.intArraySet(treeV[k], int.MaxValue);
                ArrayUtil.intArraySet(treeS[k], 0);
            }
            // Invalidate saved tree
            saved = false;
        }

        /// <summary> Resets the tree values and state. The values are set to the values in
        /// 'val'. The states are all set to 0.
        /// 
        /// </summary>
        /// <param name="val">The new values for the leafs, in lexicographical order.
        /// 
        /// </param>
        public virtual void reset(int[] val)
        {
            int k;
            // Set values for leaf level
            for (k = w * h - 1; k >= 0; k--)
            {
                treeV[0][k] = val[k];
            }
            // Calculate values at other levels
            recalcTreeV();
            // Set all states to 0
            for (k = lvls - 1; k >= 0; k--)
            {
                ArrayUtil.intArraySet(treeS[k], 0);
            }
            // Invalidate saved tree
            saved = false;
        }
    }
}