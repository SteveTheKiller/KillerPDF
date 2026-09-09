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
namespace CoreJ2K.j2k.util
{

    /// <summary> This class contains a collection of utility methods fro mathematical
    /// operations. All methods are static.
    /// 
    /// </summary>
    public class MathUtil
    {

        /// <summary> Method that calculates the floor of the log, base 2, of 'x'. The
        /// calculation is performed in integer arithmetic, therefore, it is exact.
        /// 
        /// </summary>
        /// <param name="x">The value to calculate log2 on.
        /// 
        /// </param>
        /// <returns> floor(log(x)/log(2)), calculated in an exact way.
        /// 
        /// </returns>
        public static int log2(int x)
        {
            int y, v;
            // No log of 0 or negative
            if (x <= 0)
            {
                throw new ArgumentException($"Cannot calculate log2: {nameof(x)} <= 0");
            }
            // Calculate log2 (it's actually floor log2)
            v = x;
            y = -1;
            while (v > 0)
            {
                v >>= 1;
                y++;
            }
            return y;
        }

        /// <summary> Method that calculates the Least Common Multiple (LCM) of two strictly
        /// positive integer numbers.
        /// 
        /// </summary>
        /// <param name="x1">First number
        /// 
        /// </param>
        /// <param name="x2">Second number
        /// 
        /// </param>
        public static int lcm(int x1, int x2)
        {
            if (x1 <= 0 || x2 <= 0)
            {
                throw new ArgumentException("Cannot compute the least common multiple of two numbers if one, at least, is negative.");
            }
            int max, min;
            if (x1 > x2)
            {
                max = x1;
                min = x2;
            }
            else
            {
                max = x2;
                min = x1;
            }
            for (var i = 1; i <= min; i++)
            {
                if ((max * i) % min == 0)
                {
                    return i * max;
                }
            }
            throw new InvalidOperationException($"Cannot find the least common multiple of numbers {x1} and {x2}");
        }

        /// <summary> Method that calculates the Least Common Multiple (LCM) of several
        /// positive integer numbers.
        /// 
        /// </summary>
        /// <param name="x">Array containing the numbers.
        /// 
        /// </param>
        public static int lcm(int[] x)
        {
            if (x.Length < 2)
            {
                throw new InvalidOperationException("Do not use this method if there are less than two numbers.");
            }
            var tmp = lcm(x[x.Length - 1], x[x.Length - 2]);
            for (var i = x.Length - 3; i >= 0; i--)
            {
                if (x[i] <= 0)
                {
                    throw new ArgumentException("Cannot compute the least common multiple of several numbers where one, at least, is negative.");
                }
                tmp = lcm(tmp, x[i]);
            }
            return tmp;
        }

        /// <summary> Method that calculates the Greatest Common Divisor (GCD) of two
        /// positive integer numbers.
        /// 
        /// </summary>
        public static int gcd(int x1, int x2)
        {
            if (x1 < 0 || x2 < 0)
            {
                throw new ArgumentException("Cannot compute the GCD if one integer is negative.");
            }
            int a, b, g, z;

            if (x1 > x2)
            {
                a = x1;
                b = x2;
            }
            else
            {
                a = x2;
                b = x1;
            }

            if (b == 0)
                return 0;

            g = b;

            while (g != 0)
            {
                z = a % g;
                a = g;
                g = z;
            }
            return a;
        }

        /// <summary> Method that calculates the Greatest Common Divisor (GCD) of several
        /// positive integer numbers.
        /// 
        /// </summary>
        /// <param name="x">Array containing the numbers.
        /// 
        /// </param>
        public static int gcd(int[] x)
        {
            if (x.Length < 2)
            {
                throw new InvalidOperationException("Do not use this method if there are less than two numbers.");
            }
            var tmp = gcd(x[x.Length - 1], x[x.Length - 2]);
            for (var i = x.Length - 3; i >= 0; i--)
            {
                if (x[i] < 0)
                {
                    throw new ArgumentException("Cannot compute the least common multiple of several numbers where one, at least, is negative.");
                }
                tmp = gcd(tmp, x[i]);
            }
            return tmp;
        }
    }
}