// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;

    internal static class Jbig2Bitmaps
    {
        /// <summary>
        /// Returns the specified rectangle area of the bitmap.
        /// </summary>
        /// <param name="roi">A <see cref="Jbig2Rectangle"/> that specifies the requested image section.</param>
        /// <param name="src">src the given bitmap</param>
        /// <returns>A <see cref="Jbig2Bitmap"/> that represents the requested image section.</returns>
        public static Jbig2Bitmap Extract(Jbig2Rectangle roi, Jbig2Bitmap src)
        {
            var dst = new Jbig2Bitmap(roi.Width, roi.Height);

            int upShift = roi.X & 0x07;
            int downShift = 8 - upShift;
            int dstLineStartIdx = 0;

            int padding = 8 - dst.Width & 0x07;
            int srcLineStartIdx = src.GetByteIndex(roi.X, roi.Y);
            int srcLineEndIdx = src.GetByteIndex(roi.X + roi.Width - 1, roi.Y);
            bool usePadding = dst.RowStride == srcLineEndIdx + 1 - srcLineStartIdx;

            int maxY = roi.GetMaxY();
            for (int y = roi.Y; y < maxY; y++)
            {
                int srcIdx = srcLineStartIdx;
                int dstIdx = dstLineStartIdx;

                if (srcLineStartIdx == srcLineEndIdx)
                {
                    var pixels = (byte)(src.GetByte(srcIdx) << upShift);
                    dst.SetByte(dstIdx, Unpad(padding, pixels));
                }
                else if (upShift == 0)
                {
                    for (int x = srcLineStartIdx; x <= srcLineEndIdx; x++)
                    {
                        byte value = src.GetByte(srcIdx++);

                        if (x == srcLineEndIdx && usePadding)
                        {
                            value = Unpad(padding, value);
                        }

                        dst.SetByte(dstIdx++, value);
                    }
                }
                else
                {
                    CopyLine(src, dst, upShift, downShift, padding, srcLineStartIdx, srcLineEndIdx,
                            usePadding, srcIdx, dstIdx);
                }

                srcLineStartIdx += src.RowStride;
                srcLineEndIdx += src.RowStride;
                dstLineStartIdx += dst.RowStride;
            }

            return dst;
        }

        /// <summary>
        /// The method combines two given bytes with a logical operator.
        /// The JBIG2 Standard specifies 5 possible combinations of bytes.
        /// Hint: Please take a look at ISO/IEC 14492:2001 (E) for detailed definition
        /// and description of the operators.
        /// </summary>
        /// <param name="value1">The value that should be combined with value2.</param>
        /// <param name="value2">The value that should be combined with value1.</param>
        /// <param name="op">The specified combination operator.</param>
        /// <returns>The combination result.</returns>
        public static byte CombineBytes(byte value1, byte value2, CombinationOperator op)
        {
            switch (op)
            {
                case CombinationOperator.OR:
                    return (byte)(value2 | value1);

                case CombinationOperator.AND:
                    return (byte)(value2 & value1);

                case CombinationOperator.XOR:
                    return (byte)(value2 ^ value1);

                case CombinationOperator.XNOR:
                    return (byte)~(value1 ^ value2);

                case CombinationOperator.REPLACE:
                default:
                    // Old value is replaced by new value.
                    return value2;
            }
        }

        /// <summary>
        /// This method combines a given bitmap with the current instance.
        /// Parts of the bitmap to blit that are outside of the target bitmap will be ignored.
        /// </summary>
        /// <param name="src">The bitmap that should be combined with the one of the current instance.</param>
        /// <param name="dst">The destination bitmap.</param>
        /// <param name="x">The x coordinate where the upper left corner of the bitmap to blit should be positioned.</param>
        /// <param name="y">The y coordinate where the upper left corner of the bitmap to blit should be positioned.</param>
        /// <param name="combinationOperator">The combination operator for combining two pixels.</param>
        public static void Blit(Jbig2Bitmap src, Jbig2Bitmap dst, int x, int y, CombinationOperator combinationOperator)
        {
            int startLine = 0;
            int srcStartIdx = 0;
            int srcEndIdx = src.RowStride - 1;

            // Ignore those parts of the source bitmap which would be placed outside the target bitmap.
            if (x < 0)
            {
                srcStartIdx = -x;
                x = 0;
            }
            else if (x + src.Width > dst.Width)
            {
                srcEndIdx -= src.Width + x - dst.Width;
            }

            if (y < 0)
            {
                startLine = -y;
                y = 0;
                srcStartIdx += src.RowStride;
                srcEndIdx += src.RowStride;
            }
            else if (y + src.Height > dst.Height)
            {
                startLine = src.Height + y - dst.Height;
            }

            int shiftVal1 = x & 0x07;
            int shiftVal2 = 8 - shiftVal1;

            int padding = src.Width & 0x07;
            int toShift = shiftVal2 - padding;

            bool useShift = (shiftVal2 & 0x07) != 0;
            bool specialCase = src.Width <= (srcEndIdx - srcStartIdx << 3) + shiftVal2;

            int dstStartIdx = dst.GetByteIndex(x, y);

            int lastLine = Math.Min(src.Height, startLine + dst.Height);

            if (!useShift)
            {
                BlitUnshifted(src, dst, startLine, lastLine, dstStartIdx, srcStartIdx, srcEndIdx,
                        combinationOperator);
            }
            else if (specialCase)
            {
                BlitSpecialShifted(src, dst, startLine, lastLine, dstStartIdx, srcStartIdx, srcEndIdx,
                        toShift, shiftVal1, shiftVal2, combinationOperator);
            }
            else
            {
                BlitShifted(src, dst, startLine, lastLine, dstStartIdx, srcStartIdx, srcEndIdx, toShift,
                        shiftVal1, shiftVal2, combinationOperator, padding);
            }
        }

        private static void CopyLine(Jbig2Bitmap src, Jbig2Bitmap dst, int sourceUpShift, int sourceDownShift,
                int padding, int firstSourceByteOfLine, int lastSourceByteOfLine, bool usePadding,
                int sourceOffset, int targetOffset)
        {
            for (int x = firstSourceByteOfLine; x < lastSourceByteOfLine; x++)
            {
                if (sourceOffset + 1 < src.ByteArray.Length)
                {
                    bool isLastByte = x + 1 == lastSourceByteOfLine;
                    var value = (byte)(src.GetByte(sourceOffset++) << sourceUpShift
                            | (int)(uint)(src.GetByte(sourceOffset) & 0xff) >> sourceDownShift);

                    if (isLastByte && !usePadding)
                    {
                        value = Unpad(padding, value);
                    }

                    dst.SetByte(targetOffset++, value);

                    if (isLastByte && usePadding)
                    {
                        value = Unpad(padding, (byte)((src.GetByte(sourceOffset) & 0xff) << sourceUpShift));
                        dst.SetByte(targetOffset, value);
                    }
                }
                else
                {
                    var value = (byte)(src.GetByte(sourceOffset++) << sourceUpShift & 0xff);
                    dst.SetByte(targetOffset++, value);
                }
            }
        }

        /// <summary>
        /// Removes unnecessary bits from a byte.
        /// </summary>
        /// <param name="padding">The amount of unnecessary bits.</param>
        /// <param name="value">The byte that should be cleaned up.</param>
        /// <returns>A cleaned byte.</returns>
        private static byte Unpad(int padding, byte value)
        {
            return (byte)(value >> padding << padding);
        }

        private static void BlitUnshifted(Jbig2Bitmap src, Jbig2Bitmap dst, int startLine, int lastLine,
                int dstStartIdx, int srcStartIdx, int srcEndIdx, CombinationOperator op)
        {
            for (int dstLine = startLine; dstLine < lastLine; dstLine++, dstStartIdx += dst
                    .RowStride, srcStartIdx += src.RowStride, srcEndIdx += src.RowStride)
            {
                int dstIdx = dstStartIdx;

                // Go through the bytes in a line of the Symbol
                for (int srcIdx = srcStartIdx; srcIdx <= srcEndIdx; srcIdx++)
                {
                    byte oldByte = dst.GetByte(dstIdx);
                    byte newByte = src.GetByte(srcIdx);
                    dst.SetByte(dstIdx++, CombineBytes(oldByte, newByte, op));
                }
            }
        }

        private static void BlitSpecialShifted(Jbig2Bitmap src, Jbig2Bitmap dst, int startLine, int lastLine,
                int dstStartIdx, int srcStartIdx, int srcEndIdx, int toShift, int shiftVal1,
                int shiftVal2, CombinationOperator op)
        {
            for (int dstLine = startLine; dstLine < lastLine; dstLine++, dstStartIdx += dst.RowStride,
                 srcStartIdx += src.RowStride, srcEndIdx += src.RowStride)
            {
                short register = 0;
                int dstIdx = dstStartIdx;

                // Go through the bytes in a line of the Symbol
                for (int srcIdx = srcStartIdx; srcIdx <= srcEndIdx; srcIdx++)
                {
                    byte oldByte = dst.GetByte(dstIdx);
                    register = (short)(((int)register | src.GetByteAsInteger(srcIdx)) << shiftVal2);
                    byte newByte = (byte)(register >> 8);

                    if (srcIdx == srcEndIdx)
                    {
                        newByte = Unpad(toShift, newByte);
                    }

                    dst.SetByte(dstIdx++, CombineBytes(oldByte, newByte, op));
                    register <<= shiftVal1;
                }
            }
        }

        private static void BlitShifted(Jbig2Bitmap src, Jbig2Bitmap dst, int startLine, int lastLine,
                int dstStartIdx, int srcStartIdx, int srcEndIdx, int toShift, int shiftVal1,
                int shiftVal2, CombinationOperator op, int padding)
        {
            for (int dstLine = startLine; dstLine < lastLine; dstLine++, dstStartIdx += dst.RowStride,
                 srcStartIdx += src.RowStride, srcEndIdx += src.RowStride)
            {
                short register = 0;
                int dstIdx = dstStartIdx;

                // Go through the bytes in a line of the symbol
                for (int srcIdx = srcStartIdx; srcIdx <= srcEndIdx; srcIdx++)
                {
                    byte oldByte = dst.GetByte(dstIdx);
                    register = (short)(((int)register | src.GetByteAsInteger(srcIdx)) << shiftVal2);

                    byte newByte = (byte)(register >> 8);
                    dst.SetByte(dstIdx++, CombineBytes(oldByte, newByte, op));

                    register <<= shiftVal1;

                    if (srcIdx == srcEndIdx)
                    {
                        newByte = (byte)(register >> 8 - shiftVal2);

                        if (padding != 0)
                        {
                            newByte = Unpad(8 + toShift, newByte);
                        }

                        oldByte = dst.GetByte(dstIdx);
                        dst.SetByte(dstIdx, CombineBytes(oldByte, newByte, op));
                    }
                }
            }
        }
    }
}
