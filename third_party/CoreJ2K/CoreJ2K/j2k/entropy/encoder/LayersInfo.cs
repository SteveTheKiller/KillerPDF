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
using System;
namespace CoreJ2K.j2k.entropy.encoder
{

    /// <summary> This class stores the specification of a layer distribution in the bit
    /// stream. The specification is made of optimization points and a number of
    /// extra layers to add between the optimization points. Each optimization
    /// point creates a layer which is optimized by the rate allocator to the
    /// specified target bitrate. The extra layers are added by the rate allocator
    /// between the optimized layers, with the difference that they are not
    /// optimized (i.e. they have no precise target bitrate).
    /// 
    /// The overall target bitrate for the bit stream is always added as the
    /// last optimization point without any extra layers after it. If there are
    /// some optimization points whose target bitrate is larger than the overall
    /// target bitrate, the overall target bitrate will still appear as the last
    /// optimization point, even though it does not follow the increasing target
    /// bitrate order of the other optimization points. The rate allocator is
    /// responsible for eliminating layers that have target bitrates larger than
    /// the overall target bitrate.
    /// 
    /// Optimization points can be added with the addOptPoint() method. It takes
    /// the target bitrate for the optimized layer and the number of extra layers
    /// to add after it.
    /// 
    /// Information about the total number of layers, total number of
    /// optimization points, target bitrates, etc. can be obtained with the other
    /// methods.
    /// 
    /// </summary>
    public class LayersInfo
    {
        /// <summary> Returns the overall target bitrate for the entire bit stream.
        /// 
        /// </summary>
        /// <returns> The overall target bitrate
        /// 
        /// </returns>
        public virtual float TotBitrate => totbrate;

        /// <summary> Returns the total number of layers, according to the layer
        /// specification of this object and the overall target bitrate.
        /// 
        /// </summary>
        /// <returns> The total number of layers, according to the layer spec.
        /// 
        /// </returns>
        public virtual int TotNumLayers => totlyrs;

        /// <summary> Returns the number of layers to optimize, or optimization points, as
        /// specified by this object.
        /// 
        /// </summary>
        /// <returns> The number of optimization points
        /// 
        /// </returns>
        public virtual int NOptPoints =>
            // overall target bitrate is counted as extra
            nopt + 1;

        /// <summary>The initial size for the arrays: 10 </summary>
        private const int SZ_INIT = 10;

        /// <summary>The size increment for the arrays </summary>
        private const int SZ_INCR = 5;

        /// <summary>The total number of layers </summary>
        // Starts at 1: overall target bitrate is always an extra optimized layer
        internal int totlyrs = 1;

        /// <summary>The overall target bitrate, for the whole bit stream </summary>
        internal float totbrate;

        /// <summary>The number of optimized layers, or optimization points, without
        /// counting the extra one coming from the overall target bitrate 
        /// </summary>
        internal int nopt;

        /// <summary>The target bitrate to which specified layers should be optimized. </summary>
        internal float[] optbrate = new float[SZ_INIT];

        /// <summary>The number of extra layers to be added after an optimized layer. After
        /// the layer that is optimized to optbrate[i], extralyrs[i] extra layers
        /// should be added. These layers are allocated between the bitrate
        /// optbrate[i] and the next optimized bitrate optbrate[i+1] or, if it does
        /// not exist, the overall target bitrate. 
        /// </summary>
        internal int[] extralyrs = new int[SZ_INIT];

        /// <summary> Creates a new LayersInfo object. The overall target bitrate 'brate' is
        /// always an extra optimization point, with no extra layers are after
        /// it. Note that any optimization points that are added with addOptPoint()
        /// are always added before the overall target bitrate.
        /// 
        /// </summary>
        /// <param name="brate">The overall target bitrate for the bit stream
        /// 
        /// </param>
        public LayersInfo(float brate)
        {
            if (brate <= 0)
            {
                throw new ArgumentException("Overall target bitrate must " + "be a positive number");
            }
            totbrate = brate;
        }

        /// <summary> Returns the target bitrate of the optmimization point 'n'.
        /// 
        /// </summary>
        /// <param name="n">The optimization point index (starts at 0).
        /// 
        /// </param>
        /// <returns> The target bitrate (in bpp) for the optimization point 'n'.
        /// 
        /// </returns>
        public virtual float GetTargetBitrate(int n)
        {
            // overall target bitrate is counted as extra
            return (n < nopt) ? optbrate[n] : totbrate;
        }

        /// <summary> Returns the number of extra layers to add after the optimization point
        /// 'n', but before optimization point 'n+1'. If there is no optimization
        /// point 'n+1' then they should be added before the overall target
        /// bitrate.
        /// 
        /// </summary>
        /// <param name="n">The optimization point index (starts at 0).
        /// 
        /// </param>
        /// <returns> The number of extra (unoptimized) layers to add after the
        /// optimization point 'n'
        /// 
        /// </returns>
        public virtual int GetExtraLayers(int n)
        {
            // overall target bitrate is counted as extra
            return (n < nopt) ? extralyrs[n] : 0;
        }

        /// <summary> Adds a new optimization point, with target bitrate 'brate' and with
        /// 'elyrs' (unoptimized) extra layers after it. The target bitrate 'brate'
        /// must be larger than the previous optimization point. The arguments are
        /// checked and IllegalArgumentException is thrown if they are not correct.
        /// 
        /// </summary>
        /// <param name="brate">The target bitrate for the optimized layer.
        /// 
        /// </param>
        /// <param name="elyrs">The number of extra (unoptimized) layers to add after the
        /// optimized layer.
        /// 
        /// </param>
        public virtual void addOptPoint(float brate, int elyrs)
        {
            // Check validity of arguments
            if (brate <= 0)
            {
                throw new ArgumentException("Target bitrate must be positive");
            }
            if (elyrs < 0)
            {
                throw new ArgumentException("The number of extra layers " + "must be 0 or more");
            }
            if (nopt > 0 && optbrate[nopt - 1] >= brate)
            {
                throw new ArgumentException("New optimization point must have " + "a target bitrate higher than the " + "preceding one");
            }
            // Check room for new optimization point
            if (optbrate.Length == nopt)
            {
                // Need more room
                var tbr = optbrate;
                var tel = extralyrs;
                // both arrays always have same size
                optbrate = new float[optbrate.Length + SZ_INCR];
                extralyrs = new int[extralyrs.Length + SZ_INCR];
                Array.Copy(tbr, 0, optbrate, 0, nopt);
                Array.Copy(tel, 0, extralyrs, 0, nopt);
            }
            // Add new optimization point
            optbrate[nopt] = brate;
            extralyrs[nopt] = elyrs;
            nopt++;
            // Update total number of layers
            totlyrs += 1 + elyrs;
        }
    }
}