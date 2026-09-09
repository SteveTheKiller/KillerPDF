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
* 
* 
* 
*/
using System;
namespace CoreJ2K.j2k.util
{

    /// <summary> This class contains a colleaction of utility static methods for arrays.
    /// 
    /// </summary>
    public class ArrayUtil
    {

        /// <summary>The maximum array size to do element by element copying, larger
        /// arrays are copyied in a n optimized way. 
        /// </summary>
        public const int MAX_EL_COPYING = 8;

        /// <summary>The number of elements to copy initially in an optimized array copy </summary>
        public const int INIT_EL_COPYING = 4;

        /// <summary> Reinitializes an int array to the given value in an optimized way. If
        /// the length of the array is less than MAX_EL_COPYING, then the array
        /// is set element by element in the normal way, otherwise the first
        /// INIT_EL_COPYING elements are set element by element and then
        /// System.arraycopy is used to set the other parts of the array.
        /// 
        /// </summary>
        /// <param name="arr">The array to set.
        /// 
        /// </param>
        /// <param name="val">The value to set the array to.
        /// 
        /// 
        /// 
        /// </param>
        public static void intArraySet(int[] arr, int val)
        {
            int i, len, len2;

            len = arr.Length;
            // Set array to 'val' in an optimized way
            if (len < MAX_EL_COPYING)
            {
                // Not worth doing optimized way
                for (i = len - 1; i >= 0; i--)
                {
                    // Set elements
                    arr[i] = val;
                }
            }
            else
            {
                // Do in optimized way
                len2 = len >> 1;
                for (i = 0; i < INIT_EL_COPYING; i++)
                {
                    // Set first elements
                    arr[i] = val;
                }
                for (; i <= len2; i <<= 1)
                {
                    // Copy values doubling size each time
                    Array.Copy(arr, 0, arr, i, i);
                }
                if (i < len)
                {
                    // Copy values to end
                    Array.Copy(arr, 0, arr, i, len - i);
                }
            }
        }

        /// <summary> Reinitializes a byte array to the given value in an optimized way. If
        /// the length of the array is less than MAX_EL_COPYING, then the array
        /// is set element by element in the normal way, otherwise the first
        /// INIT_EL_COPYING elements are set element by element and then
        /// System.arraycopy is used to set the other parts of the array.
        /// 
        /// </summary>
        /// <param name="arr">The array to set.
        /// 
        /// </param>
        /// <param name="val">The value to set the array to.
        /// 
        /// 
        /// 
        /// </param>
        public static void byteArraySet(byte[] arr, byte val)
        {
            int i, len, len2;

            len = arr.Length;
            // Set array to 'val' in an optimized way
            if (len < MAX_EL_COPYING)
            {
                // Not worth doing optimized way
                for (i = len - 1; i >= 0; i--)
                {
                    // Set elements
                    arr[i] = val;
                }
            }
            else
            {
                // Do in optimized way
                len2 = len >> 1;
                for (i = 0; i < INIT_EL_COPYING; i++)
                {
                    // Set first elements
                    arr[i] = val;
                }
                for (; i <= len2; i <<= 1)
                {
                    // Copy values doubling size each time
                    Array.Copy(arr, 0, arr, i, i);
                }
                if (i < len)
                {
                    // Copy values to end
                    Array.Copy(arr, 0, arr, i, len - i);
                }
            }
        }
    }
}