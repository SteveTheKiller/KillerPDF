// Copyright (c) 2007-2016 CSJ2K contributors.
// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CoreJ2K.Util
{
    /// <summary>
    /// Represents an in-memory image with interleaved integer samples.
    /// Provides conversion to other image backends via <see cref="ImageFactory"/>.
    /// </summary>
    /// <remarks>
    /// The underlying sample buffer is rented from <see cref="ArrayPool{T}.Shared"/> to
    /// avoid Large Object Heap allocations on every decode. Callers should
    /// <see cref="Dispose"/> the instance when finished so the buffer is returned to the
    /// pool. A finalizer is provided as a safety net for callers that forget.
    /// </remarks>
    [DebuggerDisplay("InterleavedImage {Width}x{Height}x{NumberOfComponents}")]
    public sealed class InterleavedImage : IImage, ICloneable, IEquatable<InterleavedImage>, IDisposable
    {
        #region FIELDS

        private readonly double[] byteScaling;
        private readonly int[] bitDepths;
        private readonly int dataLength;
        private int[] data;
        private int disposed;

        #endregion

        #region CONSTRUCTORS

        /// <summary>
        /// Initializes a new instance of the <see cref="InterleavedImage"/> class.
        /// </summary>
        /// <param name="width">Image width in pixels. Must be &gt; 0.</param>
        /// <param name="height">Image height in pixels. Must be &gt; 0.</param>
        /// <param name="numberOfComponents">Number of components per pixel. Must be &gt; 0.</param>
        /// <param name="bitsUsed">Sequence of bit depths (one per component). Each value must be between 1 and 31.</param>
        internal InterleavedImage(int width, int height, int numberOfComponents, IEnumerable<int> bitsUsed)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (numberOfComponents <= 0) throw new ArgumentOutOfRangeException(nameof(numberOfComponents));
            if (bitsUsed == null) throw new ArgumentNullException(nameof(bitsUsed));

            Width = width;
            Height = height;
            NumberOfComponents = numberOfComponents;

            var bused = bitsUsed as int[] ?? bitsUsed.ToArray();
            if (bused.Length != numberOfComponents) throw new ArgumentException("bitsUsed length must equal numberOfComponents", nameof(bitsUsed));

            // Validate pixel count and total samples to avoid integer overflow when allocating
            var pixels = (long)width * height;
            var totalSamples = pixels * (long)numberOfComponents;
            if (totalSamples <= 0 || totalSamples > int.MaxValue)
            {
                throw new ArgumentException("Image is too large");
            }

            // Store bit depths and use (2^bits - 1) as maximum sample value for scaling
            bitDepths = new int[numberOfComponents];
            byteScaling = new double[numberOfComponents];
            for (var i = 0; i < numberOfComponents; ++i)
            {
                var bits = bused[i];
                if (bits < 1 || bits > 31) throw new ArgumentOutOfRangeException(nameof(bitsUsed), "bitsUsed values must be between 1 and 31");
                bitDepths[i] = bits;
                var maxVal = (double)(((1UL << bits) - 1UL));
                // Scale sample range [0,maxVal] to [0,255]
                byteScaling[i] = 255.0 / maxVal;
            }

            dataLength = (int)totalSamples;
            data = ArrayPool<int>.Shared.Rent(dataLength);
            // Pool buffers are not guaranteed to be zeroed; clear the logical region.
            Array.Clear(data, 0, dataLength);
        }

        // Internal constructor used for cloning to allow assigning readonly fields
        internal InterleavedImage(int width, int height, int numberOfComponents, double[] byteScaling, int[] sourceData, int sourceLength)
        {
            if (byteScaling == null) throw new ArgumentNullException(nameof(byteScaling));
            if (sourceData == null) throw new ArgumentNullException(nameof(sourceData));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (numberOfComponents <= 0) throw new ArgumentOutOfRangeException(nameof(numberOfComponents));

            var expected = (long)width * height * numberOfComponents;
            if (expected > int.MaxValue || expected != sourceLength) throw new ArgumentException("data length does not match dimensions", nameof(sourceData));
            if (sourceLength > sourceData.Length) throw new ArgumentException("sourceLength exceeds sourceData", nameof(sourceLength));

            Width = width;
            Height = height;
            NumberOfComponents = numberOfComponents;

            // Make defensive copies so the clone is independent
            this.byteScaling = (double[])byteScaling.Clone();
            dataLength = sourceLength;
            data = ArrayPool<int>.Shared.Rent(dataLength);
            Array.Copy(sourceData, 0, data, 0, dataLength);

            // Reconstruct bit depths from byte scaling (approximate)
            bitDepths = new int[numberOfComponents];
            for (var i = 0; i < numberOfComponents; i++)
            {
                // Reverse calculate: byteScaling[i] = 255.0 / ((1 << bits) - 1)
                // So: (1 << bits) - 1 = 255.0 / byteScaling[i]
                var maxVal = Math.Round(255.0 / byteScaling[i]);
                bitDepths[i] = (int)Math.Ceiling(Math.Log(maxVal + 1, 2));
            }
        }

        /// <summary>
        /// Finalizer returns the rented buffer to the pool if <see cref="Dispose"/> was not called.
        /// </summary>
        ~InterleavedImage()
        {
            ReturnBuffer();
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets the image width in pixels.
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// Gets the image height in pixels.
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// Gets the number of components per pixel.
        /// </summary>
        public int NumberOfComponents { get; }

        /// <summary>
        /// Gets the bit depth for each component.
        /// </summary>
        public IReadOnlyList<int> BitDepths => bitDepths;

        internal int[] Data => data;

        /// <summary>
        /// Gets the logical number of samples held in the buffer
        /// (Width * Height * NumberOfComponents). The backing array returned by the pool
        /// may be larger than this value; only indices &lt; <see cref="DataLength"/> are valid.
        /// </summary>
        internal int DataLength => dataLength;

        #endregion

        #region METHODS

        /// <summary>
        /// Gets the bit depth for a specific component.
        /// </summary>
        /// <param name="component">Component index (0-based).</param>
        /// <returns>The bit depth for the specified component.</returns>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="component"/> is out of range.</exception>
        public int GetBitDepth(int component)
        {
            if (component < 0 || component >= NumberOfComponents)
                throw new ArgumentOutOfRangeException(nameof(component));
            return bitDepths[component];
        }

        /// <summary>
        /// Converts this image to the requested backend image type using <see cref="ImageFactory"/>.
        /// </summary>
        /// <typeparam name="T">Target image backend type returned by <see cref="ImageFactory.New{T}"/>.</typeparam>
        /// <returns>An instance of <typeparamref name="T"/> representing the converted image.</returns>
        public T As<T>()
        {
            // Rent a byte buffer to avoid a second LOH allocation for the scaled-byte image.
            // All registered IImageCreator implementations read the byte array synchronously
            // inside GetImageObject(), so it is safe to return the buffer immediately after.
            var byteCount = dataLength; // Width * Height * NumberOfComponents
            var byteBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                ToBytes(Width, Height, NumberOfComponents, byteScaling, data, byteBuffer);
                var image = ImageFactory.New<T>(Width, Height, NumberOfComponents, byteBuffer);
                if (image == null)
                    throw new InvalidOperationException(
                        $"No image creator registered for target type {typeof(T).FullName}. " +
                        $"Registered creators: {ImageFactory.DescribeRegistered()}.");
                return image.As<T>();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(byteBuffer, clearArray: false);
            }
        }

        /// <summary>
        /// Retrieves all samples for a single component as a contiguous array.
        /// </summary>
        /// <param name="number">Component index (0-based).</param>
        /// <returns>An array of length <c>Width*Height</c> containing the requested component samples.</returns>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="number"/> is out of range.</exception>
        public int[] GetComponent(int number)
        {
            if (number < 0 || number >= NumberOfComponents)
                throw new ArgumentOutOfRangeException(nameof(number));

            var component = new int[Width * Height];
            CopyComponentTo(number, component);
            return component;
        }

        /// <summary>
        /// Copies all samples for a single component into <paramref name="destination"/> without allocating.
        /// Use this overload with a pooled or stack-allocated buffer to avoid heap allocation for large images.
        /// </summary>
        /// <param name="number">Component index (0-based).</param>
        /// <param name="destination">Span to receive the de-interleaved samples. Must have length &gt;= Width*Height.</param>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="number"/> is out of range.</exception>
        /// <exception cref="ArgumentException">If <paramref name="destination"/> is too small.</exception>
        public void CopyComponentTo(int number, Span<int> destination)
        {
            if (number < 0 || number >= NumberOfComponents)
                throw new ArgumentOutOfRangeException(nameof(number));
            var length = Width * Height;
            if (destination.Length < length)
                throw new ArgumentException("Destination span is too small", nameof(destination));

            var srcIndex = number;
            var step = NumberOfComponents;
            for (var k = 0; k < length; ++k)
            {
                destination[k] = data[srcIndex];
                srcIndex += step;
            }
        }

        /// <summary>
        /// Replaces the samples for the specified component with the provided array.
        /// </summary>
        /// <param name="number">Component index (0-based).</param>
        /// <param name="samples">Array of samples of length Width*Height.</param>
        public void SetComponent(int number, int[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            SetComponent(number, (ReadOnlySpan<int>)samples);
        }

        /// <summary>
        /// Replaces the samples for the specified component from a span, without allocating.
        /// Use this overload with a pooled or stack-allocated source buffer.
        /// </summary>
        /// <param name="number">Component index (0-based).</param>
        /// <param name="samples">Span of samples of length Width*Height.</param>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="number"/> is out of range.</exception>
        /// <exception cref="ArgumentException">If <paramref name="samples"/> length does not equal Width*Height.</exception>
        public void SetComponent(int number, ReadOnlySpan<int> samples)
        {
            if (number < 0 || number >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(number));
            var length = Width * Height;
            if (samples.Length != length) throw new ArgumentException("samples length must equal Width*Height", nameof(samples));

            var dst = number;
            var step = NumberOfComponents;
            for (var i = 0; i < length; ++i)
            {
                data[dst] = samples[i];
                dst += step;
            }
        }

        /// <summary>
        /// Swaps two components in-place.
        /// </summary>
        public void SwapComponents(int a, int b)
        {
            if (a < 0 || a >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(a));
            if (b < 0 || b >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(b));
            if (a == b) return;

            var pixels = Width * Height;
            var step = NumberOfComponents;
            var idxA = a;
            var idxB = b;
            for (var p = 0; p < pixels; ++p)
            {
                var tmp = Data[idxA];
                Data[idxA] = Data[idxB];
                Data[idxB] = tmp;
                idxA += step;
                idxB += step;
            }
        }

        /// <summary>
        /// Copies samples from source component to destination component (overwrites destination).
        /// </summary>
        public void CopyComponent(int source, int destination)
        {
            if (source < 0 || source >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(source));
            if (destination < 0 || destination >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(destination));
            if (source == destination) return;

            var pixels = Width * Height;
            var step = NumberOfComponents;
            var srcIdx = source;
            var dstIdx = destination;
            for (var p = 0; p < pixels; ++p)
            {
                Data[dstIdx] = Data[srcIdx];
                srcIdx += step;
                dstIdx += step;
            }
        }

        /// <summary>
        /// Applies a transformation to each sample of the specified component in-place.
        /// </summary>
        public void ApplyToComponent(int component, Func<int,int> transform)
        {
            if (component < 0 || component >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(component));
            if (transform == null) throw new ArgumentNullException(nameof(transform));

            var pixels = Width * Height;
            var step = NumberOfComponents;
            var idx = component;
            for (var p = 0; p < pixels; ++p)
            {
                Data[idx] = transform(Data[idx]);
                idx += step;
            }
        }

        /// <summary>
        /// Fills an entire component with a constant value.
        /// </summary>
        public void FillComponent(int component, int value)
        {
            if (component < 0 || component >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(component));

            var pixels = Width * Height;
            var step = NumberOfComponents;
            var idx = component;
            for (var p = 0; p < pixels; ++p)
            {
                Data[idx] = value;
                idx += step;
            }
        }

        /// <summary>
        /// Returns the requested component converted to bytes using per-component scaling.
        /// </summary>
        public byte[] GetComponentBytes(int component)
        {
            var pixels = Width * Height;
            var outBytes = new byte[pixels];
            ToComponentBytes(component, outBytes);
            return outBytes;
        }

        /// <summary>
        /// Writes the specified component converted to bytes into destination span.
        /// Destination length must be at least Width*Height.
        /// </summary>
        public void ToComponentBytes(int component, Span<byte> destination)
        {
            if (component < 0 || component >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(component));
            var pixels = Width * Height;
            if (destination.Length < pixels) throw new ArgumentException("Destination span is too small", nameof(destination));

            var scale = byteScaling[component];
            var idx = component;
            var step = NumberOfComponents;
            for (var p = 0; p < pixels; ++p)
            {
                var scaled = scale * Data[idx];
                var v = (int)Math.Round(scaled);
                if (v < 0) v = 0; else if (v > 255) v = 255;
                destination[p] = (byte)v;
                idx += step;
            }
        }

        /// <summary>
        /// Returns a copy of the raw interleaved sample buffer as a new array.
        /// The returned array can be modified without affecting this instance.
        /// </summary>
        public int[] GetDataCopy()
        {
            var copy = new int[dataLength];
            CopyDataTo(copy);
            return copy;
        }

        /// <summary>
        /// Copies the raw interleaved sample buffer into <paramref name="destination"/> without allocating.
        /// Use this overload with a pooled or pre-allocated buffer to avoid heap allocation for large images.
        /// </summary>
        /// <param name="destination">Span to receive all samples. Must have length &gt;= Width*Height*NumberOfComponents.</param>
        /// <exception cref="ArgumentException">If <paramref name="destination"/> is too small.</exception>
        public void CopyDataTo(Span<int> destination)
        {
            if (destination.Length < dataLength)
                throw new ArgumentException("Destination span is too small", nameof(destination));
            data.AsSpan(0, dataLength).CopyTo(destination);
        }

        /// <summary>
        /// Gets the sample value for component <paramref name="c"/> at pixel (<paramref name="x"/>,<paramref name="y"/>).
        /// </summary>
        /// <param name="x">X coordinate (0-based).</param>
        /// <param name="y">Y coordinate (0-based).</param>
        /// <param name="c">Component index (0-based).</param>
        /// <returns>The sample value for the requested location and component.</returns>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates or component index are out of range.</exception>
        public int GetSample(int x, int y, int c)
        {
            if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
            if (c < 0 || c >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(c));

            var idx = (y * Width + x) * NumberOfComponents + c;
            return Data[idx];
        }

        /// <summary>
        /// Sets the sample value for component <paramref name="c"/> at pixel (<paramref name="x"/>,<paramref name="y"/>).
        /// </summary>
        /// <param name="x">X coordinate (0-based).</param>
        /// <param name="y">Y coordinate (0-based).</param>
        /// <param name="c">Component index (0-based).</param>
        /// <param name="value">Sample value to set.</param>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates or component index are out of range.</exception>
        public void SetSample(int x, int y, int c, int value)
        {
            if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
            if (c < 0 || c >= NumberOfComponents) throw new ArgumentOutOfRangeException(nameof(c));

            var idx = (y * Width + x) * NumberOfComponents + c;
            Data[idx] = value;
        }

        internal void FillRow(int rowIndex, int lineIndex, int rowWidth, int[] rowValues)
        {
            if (rowValues == null) throw new ArgumentNullException(nameof(rowValues));
            if (rowWidth <= 0) throw new ArgumentOutOfRangeException(nameof(rowWidth));
            if (lineIndex < 0 || lineIndex >= Height) throw new ArgumentOutOfRangeException(nameof(lineIndex));
            if (NumberOfComponents <= 0) throw new InvalidOperationException("Invalid NumberOfComponents");

            // rowValues holds interleaved samples for a tile row; its length must be a multiple of components
            if (rowValues.Length % NumberOfComponents != 0)
                throw new ArgumentException("rowValues length must be a multiple of NumberOfComponents", nameof(rowValues));

            var srcTotalPixels = rowValues.Length / NumberOfComponents;

            // Determine destination X and source X start (handle tiles that start left of image)
            var dstX = rowIndex;
            var srcX = 0;
            if (dstX < 0)
            {
                // tile starts before image left edge; skip the leftmost src pixels
                srcX = -dstX;
                dstX = 0;
            }

            // remaining width available in target row (pixels)
            var remainingDstPixels = rowWidth - dstX;
            if (remainingDstPixels <= 0) return;

            // remaining source pixels available
            var remainingSrcPixels = srcTotalPixels - srcX;
            if (remainingSrcPixels <= 0) return;

            // number of pixels to copy (pixels)
            var copyPixels = Math.Min(remainingDstPixels, remainingSrcPixels);
            if (copyPixels <= 0) return;

            var copyLength = copyPixels * NumberOfComponents;
            var srcOffset = srcX * NumberOfComponents;
            var dstOffset = NumberOfComponents * (dstX + lineIndex * Width);

            // Bounds-check destination region to avoid ArgumentException from Array.Copy
            if (dstOffset < 0 || dstOffset + copyLength > dataLength)
                throw new ArgumentException("Destination region does not fit within image buffer", nameof(rowValues));

            Array.Copy(rowValues, srcOffset, data, dstOffset, copyLength);
        }

        /// <summary>
        /// Creates a deep clone of this <see cref="InterleavedImage"/> instance.
        /// </summary>
        /// <returns>A deep copy of this instance (as <see cref="object"/> to satisfy <see cref="ICloneable"/>).</returns>
        public object Clone()
        {
            return CloneInterleavedImage();
        }

        /// <summary>
        /// Creates a strongly-typed deep clone of this instance.
        /// </summary>
        /// <returns>A new <see cref="InterleavedImage"/> with independent internal buffers.</returns>
        public InterleavedImage CloneInterleavedImage()
        {
            // Create a new instance with deep-copied arrays so the clone is independent
            return new InterleavedImage(Width, Height, NumberOfComponents, byteScaling, data, dataLength);
        }

        private static void ToBytes(int width, int height, int numberOfComponents,
            IReadOnlyList<double> byteScaling, IReadOnlyList<int> data, Span<byte> destination)
        {
            var pixels = width * height;
            var nc = numberOfComponents;
            var count = nc * pixels;

            if (destination.Length < count)
                throw new ArgumentException("Destination span is too small", nameof(destination));

            // Convert interleaved int samples to bytes with per-component scaling and clamping
            for (var p = 0; p < pixels; ++p)
            {
                var baseIdx = p * nc;
                for (var c = 0; c < nc; ++c)
                {
                    // Scale and clamp to [0,255]
                    var scaled = byteScaling[c] * data[baseIdx + c];
                    var v = (int)Math.Round(scaled);
                    if (v < 0) v = 0;
                    else if (v > 255) v = 255;
                    destination[baseIdx + c] = (byte)v;
                }
            }
        }

        /// <summary>
        /// Gets all component values for a pixel.
        /// </summary>
        /// <param name="x">X coordinate (0-based).</param>
        /// <param name="y">Y coordinate (0-based).</param>
        /// <param name="components">Destination span to receive component values. Must have length >= NumberOfComponents.</param>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates are out of range.</exception>
        /// <exception cref="ArgumentException">If destination span is too small.</exception>
        public void GetPixel(int x, int y, Span<int> components)
        {
            if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
            if (components.Length < NumberOfComponents)
                throw new ArgumentException("Destination span is too small", nameof(components));

            var idx = (y * Width + x) * NumberOfComponents;
            for (var c = 0; c < NumberOfComponents; c++)
            {
                components[c] = Data[idx + c];
            }
        }

        /// <summary>
        /// Gets all component values for a pixel.
        /// </summary>
        /// <param name="x">X coordinate (0-based).</param>
        /// <param name="y">Y coordinate (0-based).</param>
        /// <returns>Array containing all component values for the pixel.</returns>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates are out of range.</exception>
        public int[] GetPixel(int x, int y)
        {
            var components = new int[NumberOfComponents];
            GetPixel(x, y, components);
            return components;
        }

        /// <summary>
        /// Sets all component values for a pixel.
        /// </summary>
        /// <param name="x">X coordinate (0-based).</param>
        /// <param name="y">Y coordinate (0-based).</param>
        /// <param name="components">Component values to set. Must have length >= NumberOfComponents.</param>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates are out of range.</exception>
        /// <exception cref="ArgumentException">If components array is too small.</exception>
        public void SetPixel(int x, int y, ReadOnlySpan<int> components)
        {
            if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
            if (y < 0 || y >= Height) throw new ArgumentOutOfRangeException(nameof(y));
            if (components.Length < NumberOfComponents)
                throw new ArgumentException("Not enough components", nameof(components));

            var idx = (y * Width + x) * NumberOfComponents;
            for (var c = 0; c < NumberOfComponents; c++)
            {
                Data[idx + c] = components[c];
            }
        }

        /// <summary>
        /// Copies a rectangular region to another InterleavedImage.
        /// </summary>
        /// <param name="srcX">Source X coordinate (0-based).</param>
        /// <param name="srcY">Source Y coordinate (0-based).</param>
        /// <param name="width">Width of the region to copy.</param>
        /// <param name="height">Height of the region to copy.</param>
        /// <param name="destination">Destination image.</param>
        /// <param name="dstX">Destination X coordinate (0-based).</param>
        /// <param name="dstY">Destination Y coordinate (0-based).</param>
        /// <exception cref="ArgumentNullException">If destination is null.</exception>
        /// <exception cref="ArgumentException">If component counts don't match.</exception>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates or dimensions are invalid.</exception>
        public void CopyRegion(int srcX, int srcY, int width, int height,
            InterleavedImage? destination, int dstX, int dstY)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (NumberOfComponents != destination.NumberOfComponents)
                throw new ArgumentException("Component count mismatch", nameof(destination));
            if (srcX < 0 || srcY < 0 || width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException();
            if (srcX + width > Width || srcY + height > Height)
                throw new ArgumentOutOfRangeException("Source region exceeds image bounds");
            if (dstX < 0 || dstY < 0)
                throw new ArgumentOutOfRangeException();
            if (dstX + width > destination.Width || dstY + height > destination.Height)
                throw new ArgumentOutOfRangeException("Destination region exceeds image bounds");

            // Copy row by row
            for (var y = 0; y < height; y++)
            {
                var srcIdx = ((srcY + y) * Width + srcX) * NumberOfComponents;
                var dstIdx = ((dstY + y) * destination.Width + dstX) * NumberOfComponents;
                var copyLength = width * NumberOfComponents;

                Array.Copy(Data, srcIdx, destination.Data, dstIdx, copyLength);
            }
        }

        /// <summary>
        /// Creates a new InterleavedImage from a rectangular region.
        /// </summary>
        /// <param name="x">X coordinate of the region (0-based).</param>
        /// <param name="y">Y coordinate of the region (0-based).</param>
        /// <param name="width">Width of the region.</param>
        /// <param name="height">Height of the region.</param>
        /// <returns>A new InterleavedImage containing the cropped region.</returns>
        /// <exception cref="ArgumentOutOfRangeException">If coordinates or dimensions are invalid.</exception>
        public InterleavedImage Crop(int x, int y, int width, int height)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException();
            if (x + width > Width || y + height > Height)
                throw new ArgumentOutOfRangeException("Crop region exceeds image bounds");

            var result = new InterleavedImage(width, height, NumberOfComponents, bitDepths);
            CopyRegion(x, y, width, height, result, 0, 0);
            return result;
        }

        /// <summary>
        /// Determines whether this image is equal to another image.
        /// </summary>
        /// <param name="other">The image to compare with.</param>
        /// <returns>true if the images have the same dimensions, components, and data; otherwise, false.</returns>
        public bool Equals(InterleavedImage? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            if (Width != other.Width || Height != other.Height || NumberOfComponents != other.NumberOfComponents)
                return false;

            // Compare data arrays (only logical region; pool buffers may be larger)
            var dataSpan = data.AsSpan(0, dataLength);
            var otherDataSpan = other.data.AsSpan(0, other.dataLength);
            return dataSpan.SequenceEqual(otherDataSpan);
        }

        /// <summary>
        /// Returns the rented sample buffer to <see cref="ArrayPool{T}.Shared"/>.
        /// After disposal the image must not be used.
        /// </summary>
        public void Dispose()
        {
            ReturnBuffer();
            GC.SuppressFinalize(this);
        }

        private void ReturnBuffer()
        {
            // Ensure the buffer is returned only once even under racing finalizer/Dispose.
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
            var buffer = data;
            data = Array.Empty<int>();
            if (buffer != null && buffer.Length > 0)
            {
                ArrayPool<int>.Shared.Return(buffer, clearArray: false);
            }
        }

        /// <summary>
        /// Determines whether this image is equal to another object.
        /// </summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns>true if obj is an InterleavedImage equal to this instance; otherwise, false.</returns>
        public override bool Equals(object? obj)
        {
            return Equals(obj as InterleavedImage);
        }

        /// <summary>
        /// Returns a hash code for this image based on its dimensions and component count.
        /// </summary>
        /// <returns>A hash code for the current image.</returns>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 23 + Width.GetHashCode();
                hash = hash * 23 + Height.GetHashCode();
                hash = hash * 23 + NumberOfComponents.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Returns a string representation of this image.
        /// </summary>
        /// <returns>A string describing the image dimensions and components.</returns>
        public override string ToString()
        {
            return $"InterleavedImage[{Width}x{Height}, {NumberOfComponents} components]";
        }

        /// <summary>
        /// Determines whether two images are equal.
        /// </summary>
        public static bool operator ==(InterleavedImage left, InterleavedImage right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two images are not equal.
        /// </summary>
        public static bool operator !=(InterleavedImage left, InterleavedImage right)
        {
            return !(left == right);
        }

        #endregion
    }
}
