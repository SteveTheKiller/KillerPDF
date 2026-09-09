// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Collections;

namespace CoreJ2K.j2k.roi.encoder
{
    /// <summary>
    /// Memory-efficient bit-packed ROI mask representation.
    /// Uses 1 bit per coefficient instead of 32 bits (int array).
    /// Provides 32x memory reduction for binary masks.
    /// </summary>
    public class BitPackedROIMask
    {
        private readonly BitArray _bits;
        private readonly int _width;
        private readonly int _height;
        
        /// <summary>
        /// Gets the width of the mask.
        /// </summary>
        public int Width => _width;
        
        /// <summary>
        /// Gets the height of the mask.
        /// </summary>
        public int Height => _height;
        
        /// <summary>
        /// Gets the total number of coefficients in the mask.
        /// </summary>
        public int Length => _width * _height;
        
        /// <summary>
        /// Gets the approximate memory usage in bytes.
        /// </summary>
        public int MemoryUsage => (_bits.Length + 7) / 8 + 16; // bits + overhead
        
        /// <summary>
        /// Creates a new bit-packed ROI mask.
        /// </summary>
        /// <param name="width">Width of the mask</param>
        /// <param name="height">Height of the mask</param>
        public BitPackedROIMask(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Width and height must be positive");
                
            _width = width;
            _height = height;
            _bits = new BitArray(width * height, false);
        }
        
        /// <summary>
        /// Creates a bit-packed mask from an existing int array mask.
        /// </summary>
        /// <param name="mask">The mask data (0 = background, non-zero = ROI)</param>
        /// <param name="width">Width of the mask</param>
        /// <param name="height">Height of the mask</param>
        public BitPackedROIMask(int[] mask, int width, int height) : this(width, height)
        {
            if (mask == null)
                throw new ArgumentNullException(nameof(mask));
            if (mask.Length != width * height)
                throw new ArgumentException("Mask array size must match width * height");
                
            for (int i = 0; i < mask.Length; i++)
            {
                _bits[i] = mask[i] != 0;
            }
        }
        
        /// <summary>
        /// Gets or sets the mask value at the specified linear index.
        /// </summary>
        /// <param name="index">Linear index (y * width + x)</param>
        public bool this[int index]
        {
            get => _bits[index];
            set => _bits[index] = value;
        }
        
        /// <summary>
        /// Gets or sets the mask value at the specified coordinates.
        /// </summary>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        public bool this[int x, int y]
        {
            get => _bits[y * _width + x];
            set => _bits[y * _width + x] = value;
        }
        
        /// <summary>
        /// Converts the bit-packed mask back to an int array.
        /// </summary>
        /// <param name="roiValue">Value to use for ROI coefficients (typically the maxshift value)</param>
        /// <returns>Int array mask</returns>
        public int[] ToIntArray(int roiValue = 1)
        {
            var result = new int[Length];
            for (int i = 0; i < Length; i++)
            {
                result[i] = _bits[i] ? roiValue : 0;
            }
            return result;
        }
        
        /// <summary>
        /// Sets all bits in the mask.
        /// </summary>
        /// <param name="value">Value to set (true = ROI, false = background)</param>
        public void SetAll(bool value)
        {
            _bits.SetAll(value);
        }
        
        /// <summary>
        /// Performs bitwise AND operation with another mask.
        /// </summary>
        public void And(BitPackedROIMask other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            if (other.Width != _width || other.Height != _height)
                throw new ArgumentException("Masks must have same dimensions");
                
            _bits.And(other._bits);
        }
        
        /// <summary>
        /// Performs bitwise OR operation with another mask.
        /// </summary>
        public void Or(BitPackedROIMask other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            if (other.Width != _width || other.Height != _height)
                throw new ArgumentException("Masks must have same dimensions");
                
            _bits.Or(other._bits);
        }
        
        /// <summary>
        /// Performs bitwise NOT operation on the mask.
        /// </summary>
        public void Not()
        {
            _bits.Not();
        }
        
        /// <summary>
        /// Counts the number of ROI coefficients (set bits).
        /// </summary>
        public int CountROICoefficients()
        {
            int count = 0;
            for (int i = 0; i < _bits.Length; i++)
            {
                if (_bits[i]) count++;
            }
            return count;
        }
        
        /// <summary>
        /// Checks if the mask contains any ROI coefficients.
        /// </summary>
        public bool HasAnyROI()
        {
            for (int i = 0; i < _bits.Length; i++)
            {
                if (_bits[i]) return true;
            }
            return false;
        }
        
        /// <summary>
        /// Creates a copy of this mask.
        /// </summary>
        public BitPackedROIMask Clone()
        {
            var clone = new BitPackedROIMask(_width, _height);
            for (int i = 0; i < _bits.Length; i++)
            {
                clone._bits[i] = _bits[i];
            }
            return clone;
        }
        
        /// <summary>
        /// Compares memory usage of bit-packed vs int array representation.
        /// </summary>
        /// <returns>Memory savings ratio (e.g., 32.0 means 32x less memory)</returns>
        public double GetMemorySavingsRatio()
        {
            var intArraySize = Length * sizeof(int);
            return (double)intArraySize / MemoryUsage;
        }
    }
    
    /// <summary>
    /// Sparse representation of ROI mask that only stores non-zero coefficients.
    /// Efficient when ROI covers a small portion of the image.
    /// </summary>
    public class SparseROIMask
    {
        private readonly int[] _roiIndices;
        private readonly int[] _roiValues;
        private readonly int _width;
        private readonly int _height;
        
        /// <summary>Gets the width of the mask region.</summary>
        public int Width => _width;
        
        /// <summary>Gets the height of the mask region.</summary>
        public int Height => _height;
        
        /// <summary>Gets the number of non-zero (ROI) coefficients.</summary>
        public int ROICount => _roiIndices.Length;
        
        /// <summary>Gets the sparsity ratio (0.0 to 1.0, lower is more sparse).</summary>
        public double SparsityRatio => (double)ROICount / (_width * _height);
        
        /// <summary>
        /// Gets the approximate memory usage in bytes.
        /// </summary>
        public int MemoryUsage => (_roiIndices.Length + _roiValues.Length) * sizeof(int) + 24;
        
        /// <summary>
        /// Creates a sparse ROI mask from a dense int array mask.
        /// </summary>
        /// <param name="mask">Dense mask array (0 = background, non-zero = ROI)</param>
        /// <param name="width">Width of the mask</param>
        /// <param name="height">Height of the mask</param>
        public SparseROIMask(int[] mask, int width, int height)
        {
            if (mask == null)
                throw new ArgumentNullException(nameof(mask));
            if (mask.Length != width * height)
                throw new ArgumentException("Mask array size must match width * height");
                
            _width = width;
            _height = height;
            
            // First pass: count non-zero elements
            int roiCount = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0) roiCount++;
            }
            
            // Second pass: store indices and values
            _roiIndices = new int[roiCount];
            _roiValues = new int[roiCount];
            int idx = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                {
                    _roiIndices[idx] = i;
                    _roiValues[idx] = mask[i];
                    idx++;
                }
            }
        }
        
        /// <summary>
        /// Converts the sparse mask back to a dense int array.
        /// </summary>
        public int[] ToDenseArray()
        {
            var result = new int[_width * _height];
            for (int i = 0; i < _roiIndices.Length; i++)
            {
                result[_roiIndices[i]] = _roiValues[i];
            }
            return result;
        }
        
        /// <summary>
        /// Gets the mask value at a specific index.
        /// </summary>
        /// <param name="index">Linear index (y * width + x)</param>
        /// <returns>Mask value (0 if not in ROI)</returns>
        public int GetValue(int index)
        {
            // Binary search for the index
            int idx = Array.BinarySearch(_roiIndices, index);
            return idx >= 0 ? _roiValues[idx] : 0;
        }
        
        /// <summary>
        /// Gets the mask value at specific coordinates.
        /// </summary>
        public int GetValue(int x, int y)
        {
            return GetValue(y * _width + x);
        }
        
        /// <summary>
        /// Checks if a coefficient is part of the ROI.
        /// </summary>
        public bool IsROI(int index)
        {
            return Array.BinarySearch(_roiIndices, index) >= 0;
        }
        
        /// <summary>
        /// Checks if a coefficient at specific coordinates is part of the ROI.
        /// </summary>
        public bool IsROI(int x, int y)
        {
            return IsROI(y * _width + x);
        }
        
        /// <summary>
        /// Determines if sparse representation is beneficial compared to dense.
        /// </summary>
        /// <returns>True if sparse representation uses less memory</returns>
        public bool IsMemoryEfficient()
        {
            var denseSize = _width * _height * sizeof(int);
            return MemoryUsage < denseSize;
        }
        
        /// <summary>
        /// Gets the memory savings ratio compared to dense representation.
        /// </summary>
        public double GetMemorySavingsRatio()
        {
            var denseSize = _width * _height * sizeof(int);
            return (double)denseSize / MemoryUsage;
        }
    }
}
