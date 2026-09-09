// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;
using System.Text;

namespace CoreJ2K.j2k.fileformat.metadata
{

    /// <summary>
    /// Represents component registration (CRG marker) data per ISO/IEC 15444-1 Annex A.11.3.
    /// CRG markers specify the horizontal and vertical offset of each component
    /// relative to the reference grid origin, allowing precise spatial registration
    /// of components with sub-pixel accuracy.
    /// </summary>
    public class ComponentRegistrationData
    {
        /// <summary>
        /// Gets or sets the horizontal offsets for each component (Xcrg values).
        /// Values are in units of 1/65536 of the horizontal separation of sample points.
        /// Array length must equal the number of components.
        /// </summary>
        public int[]? HorizontalOffsets { get; set; }

        /// <summary>
        /// Gets or sets the vertical offsets for each component (Ycrg values).
        /// Values are in units of 1/65536 of the vertical separation of sample points.
        /// Array length must equal the number of components.
        /// </summary>
        public int[]? VerticalOffsets { get; set; }

        /// <summary>
        /// Gets the number of registered components.
        /// </summary>
        public int NumComponents => HorizontalOffsets?.Length ?? 0;

        /// <summary>
        /// Gets the horizontal offset for a specific component.
        /// </summary>
        /// <param name="componentIndex">The component index (0-based).</param>
        /// <returns>The horizontal offset in units of 1/65536 of sample separation.</returns>
        public int GetHorizontalOffset(int componentIndex)
        {
            if (HorizontalOffsets == null || componentIndex < 0 || componentIndex >= HorizontalOffsets.Length)
                return 0;
            return HorizontalOffsets[componentIndex];
        }

        /// <summary>
        /// Gets the vertical offset for a specific component.
        /// </summary>
        /// <param name="componentIndex">The component index (0-based).</param>
        /// <returns>The vertical offset in units of 1/65536 of sample separation.</returns>
        public int GetVerticalOffset(int componentIndex)
        {
            if (VerticalOffsets == null || componentIndex < 0 || componentIndex >= VerticalOffsets.Length)
                return 0;
            return VerticalOffsets[componentIndex];
        }

        /// <summary>
        /// Sets the offset for a specific component.
        /// </summary>
        /// <param name="componentIndex">The component index (0-based).</param>
        /// <param name="horizontalOffset">Horizontal offset in units of 1/65536 of sample separation.</param>
        /// <param name="verticalOffset">Vertical offset in units of 1/65536 of sample separation.</param>
        public void SetOffset(int componentIndex, int horizontalOffset, int verticalOffset)
        {
            if (HorizontalOffsets == null || VerticalOffsets == null)
                throw new InvalidOperationException("Offset arrays must be initialized before setting values");

            if (componentIndex < 0 || componentIndex >= HorizontalOffsets.Length)
                throw new ArgumentOutOfRangeException(nameof(componentIndex));

            HorizontalOffsets[componentIndex] = horizontalOffset;
            VerticalOffsets[componentIndex] = verticalOffset;
        }

        /// <summary>
        /// Converts an offset value in fractional pixels to the CRG format.
        /// </summary>
        /// <param name="fractionalPixels">Offset in fractional pixels (e.g., 0.5 for half a pixel).</param>
        /// <returns>Offset in units of 1/65536 of sample separation.</returns>
        public static int FromFractionalPixels(double fractionalPixels)
        {
            return (int)(fractionalPixels * 65536);
        }

        /// <summary>
        /// Converts a CRG offset value to fractional pixels.
        /// </summary>
        /// <param name="crgValue">Offset in units of 1/65536 of sample separation.</param>
        /// <returns>Offset in fractional pixels.</returns>
        public static double ToFractionalPixels(int crgValue)
        {
            return crgValue / 65536.0;
        }

        /// <summary>
        /// Creates a ComponentRegistrationData instance with specified offsets.
        /// </summary>
        /// <param name="numComponents">Number of components.</param>
        /// <param name="horizontalOffsets">Horizontal offsets for each component (optional, defaults to 0).</param>
        /// <param name="verticalOffsets">Vertical offsets for each component (optional, defaults to 0).</param>
        public static ComponentRegistrationData Create(int numComponents, int[]? horizontalOffsets = null, int[]? verticalOffsets = null)
        {
            var data = new ComponentRegistrationData
            {
                HorizontalOffsets = horizontalOffsets ?? new int[numComponents],
                VerticalOffsets = verticalOffsets ?? new int[numComponents]
            };

            if (data.HorizontalOffsets.Length != numComponents || data.VerticalOffsets.Length != numComponents)
                throw new ArgumentException("Offset arrays must match the number of components");

            return data;
        }

        /// <summary>
        /// Creates a ComponentRegistrationData instance with standard chroma positioning.
        /// </summary>
        /// <param name="numComponents">Number of components.</param>
        /// <param name="chromaPosition">Chroma positioning type: 0=centered, 1=co-sited.</param>
        public static ComponentRegistrationData CreateWithChromaPosition(int numComponents, int chromaPosition)
        {
            var data = Create(numComponents);

            // For YCbCr-style color spaces, apply standard chroma positioning
            if (numComponents >= 3 && chromaPosition == 1)
            {
                // Co-sited: chroma samples co-located with luma samples (top-left)
                // No offset needed (all zeros)
            }
            else if (numComponents >= 3 && chromaPosition == 0)
            {
                // Centered: chroma samples centered between luma samples
                // Offset chroma by 0.5 pixels
                int offset = FromFractionalPixels(0.5);
                for (int i = 1; i < numComponents; i++)
                {
                    data.HorizontalOffsets![i] = offset;
                    data.VerticalOffsets![i] = offset;
                }
            }

            return data;
        }

        public override string ToString()
        {
            if (HorizontalOffsets == null || VerticalOffsets == null)
                return "Component Registration Data: No offsets defined";

            var sb = new StringBuilder($"Component Registration Data: {NumComponents} components\n");
            for (int i = 0; i < NumComponents; i++)
            {
                var hOff = ToFractionalPixels(HorizontalOffsets[i]);
                var vOff = ToFractionalPixels(VerticalOffsets[i]);
                sb.AppendLine($"  Component {i}: X={hOff:F4} pixels, Y={vOff:F4} pixels");
            }
            return sb.ToString();
        }
    }
}
