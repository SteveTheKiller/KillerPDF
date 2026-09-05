// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents a bi-level image that is organized like a bitmap.
    /// </summary>
    internal sealed class Jbig2Bitmap
    {
        /// <summary>
        /// The height of the bitmap in pixels.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// The width of the bitmap in pixels.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// The amount of bytes used per row.
        /// </summary>
        public int RowStride { get; }

        /// <summary>
        /// Creates an instance of a blank image.
        /// The image data is stored in a byte array. Each pixel is stored as one bit, so that each byte contains 8 pixel.
        /// A pixel has by default the value 0 for white and 1 for black.
        /// Row stride means the amount of bytes per line. It is computed automatically and fills the pad bits with 0.
        /// </summary>
        public Jbig2Bitmap(int width, int height)
        {
            Height = height;
            Width = width;
            RowStride = width + 7 >> 3;

            ByteArray = new byte[Height * RowStride];
        }

        /// <summary>
        /// Returns the value of a pixel specified by the given coordinates.
        /// By default, the value is 0 for a white pixel and 1 for a black pixel.
        /// The value is placed in the rightmost bit in the byte.
        /// </summary>
        /// <param name="x">The x-coordinate of the pixel.</param>
        /// <param name="y">The y-coordinate of the pixel.</param>
        /// <returns>The value of a pixel.</returns>
        public byte GetPixel(int x, int y)
        {
            int byteIndex = GetByteIndex(x, y);
            int bitOffset = GetBitOffset(x);

            int toShift = 7 - bitOffset;
            return (byte)(GetByte(byteIndex) >> toShift & 0x01);
        }

        public void SetPixel(int x, int y, byte pixelValue)
        {
            int byteIndex = GetByteIndex(x, y);
            int bitOffset = GetBitOffset(x);

            int shift = 7 - bitOffset;

            ref byte src = ref ByteArray[byteIndex];

            src = (byte)(src | pixelValue << shift);
        }

        /// <summary>
        /// Returns the index of the byte that contains the pixel, specified by the
        /// pixel's x and y coordinates.
        /// </summary>
        /// <param name="x">The x-coordinate of the pixel.</param>
        /// <param name="y">The y-coordinate of the pixel.</param>
        /// <returns>The index of the byte that contains the specified pixel.</returns>
        public int GetByteIndex(int x, int y)
        {
            return y * RowStride + (x >> 3);
        }

        /// <summary>
        /// Returns the bitmap as a byte array.
        /// <para>8 pixels per byte, 0 for white, 1 for black.</para>
        /// </summary>
        public byte[] ByteArray { get; }

        /// <summary>
        /// Returns a byte from the bitmap byte array.
        /// </summary>
        /// <param name="index">The array index that specifies the position of the wanted byte.</param>
        /// <returns>The byte at the requested index.</returns>
        public byte GetByte(int index)
        {
            return ByteArray[index];
        }

        /// <summary>
        /// Sets the given value at the given array index position.
        /// </summary>
        /// <param name="index">The array index that specifies the position of a byte.</param>
        /// <param name="value">The byte that should be set.</param>
        public void SetByte(int index, byte value)
        {
            ByteArray[index] = value;
        }

        /// <summary>
        /// Converts the byte at specified index into an integer and returns the value.
        /// if the given index is out of bound.
        /// </summary>
        /// <param name="index">The array index that specifies the position of the wanted byte.</param>
        /// <returns>The converted byte at the requested index as an integer.</returns>
        public int GetByteAsInteger(int index)
        {
            return ByteArray[index] & 0xff;
        }

        /// <summary>
        /// Computes the offset of the given x coordinate in its byte. The method uses optimized modulo
        /// operation for a better performance.
        /// </summary>
        /// <param name="x">The x coordinate of a pixel.</param>
        /// <returns>The bit offset of a pixel in its byte.</returns>
        public static int GetBitOffset(int x)
        {
            // The same as x % 8.
            // The rightmost three bits are 1. The value masks all bits upon the value "7".
            return x & 0x07;
        }
    }
}
