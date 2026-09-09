// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;

namespace CoreJ2K.j2k.fileformat.metadata
{
    /// <summary>
    /// Represents resolution metadata for JPEG2000 images (DPI/PPI information).
    /// Supports both capture resolution and display resolution as defined in ISO/IEC 15444-1.
    /// </summary>
    public class ResolutionData
    {
        /// <summary>
        /// Gets or sets the horizontal capture resolution in pixels per meter.
        /// Null if not specified.
        /// </summary>
        public double? HorizontalCaptureResolution { get; set; }

        /// <summary>
        /// Gets or sets the vertical capture resolution in pixels per meter.
        /// Null if not specified.
        /// </summary>
        public double? VerticalCaptureResolution { get; set; }

        /// <summary>
        /// Gets or sets the horizontal display resolution in pixels per meter.
        /// Null if not specified.
        /// </summary>
        public double? HorizontalDisplayResolution { get; set; }

        /// <summary>
        /// Gets or sets the vertical display resolution in pixels per meter.
        /// Null if not specified.
        /// </summary>
        public double? VerticalDisplayResolution { get; set; }

        /// <summary>
        /// Gets the horizontal capture resolution in DPI (dots per inch).
        /// </summary>
        public double? HorizontalCaptureDpi => HorizontalCaptureResolution.HasValue
            ? HorizontalCaptureResolution.Value / 39.3701 // meters to inches
            : (double?)null;

        /// <summary>
        /// Gets the vertical capture resolution in DPI (dots per inch).
        /// </summary>
        public double? VerticalCaptureDpi => VerticalCaptureResolution.HasValue
            ? VerticalCaptureResolution.Value / 39.3701
            : (double?)null;

        /// <summary>
        /// Gets the horizontal display resolution in DPI (dots per inch).
        /// </summary>
        public double? HorizontalDisplayDpi => HorizontalDisplayResolution.HasValue
            ? HorizontalDisplayResolution.Value / 39.3701
            : (double?)null;

        /// <summary>
        /// Gets the vertical display resolution in DPI (dots per inch).
        /// </summary>
        public double? VerticalDisplayDpi => VerticalDisplayResolution.HasValue
            ? VerticalDisplayResolution.Value / 39.3701
            : (double?)null;

        /// <summary>
        /// Sets capture resolution from DPI values.
        /// </summary>
        /// <param name="horizontalDpi">Horizontal resolution in DPI.</param>
        /// <param name="verticalDpi">Vertical resolution in DPI.</param>
        public void SetCaptureDpi(double horizontalDpi, double verticalDpi)
        {
            HorizontalCaptureResolution = horizontalDpi * 39.3701; // Convert to pixels per meter
            VerticalCaptureResolution = verticalDpi * 39.3701;
        }

        /// <summary>
        /// Sets display resolution from DPI values.
        /// </summary>
        /// <param name="horizontalDpi">Horizontal resolution in DPI.</param>
        /// <param name="verticalDpi">Vertical resolution in DPI.</param>
        public void SetDisplayDpi(double horizontalDpi, double verticalDpi)
        {
            HorizontalDisplayResolution = horizontalDpi * 39.3701;
            VerticalDisplayResolution = verticalDpi * 39.3701;
        }

        /// <summary>
        /// Sets capture resolution from pixels per meter values.
        /// </summary>
        /// <param name="horizontalPpm">Horizontal resolution in pixels per meter.</param>
        /// <param name="verticalPpm">Vertical resolution in pixels per meter.</param>
        public void SetCaptureResolution(double horizontalPpm, double verticalPpm)
        {
            HorizontalCaptureResolution = horizontalPpm;
            VerticalCaptureResolution = verticalPpm;
        }

        /// <summary>
        /// Sets display resolution from pixels per meter values.
        /// </summary>
        /// <param name="horizontalPpm">Horizontal resolution in pixels per meter.</param>
        /// <param name="verticalPpm">Vertical resolution in pixels per meter.</param>
        public void SetDisplayResolution(double horizontalPpm, double verticalPpm)
        {
            HorizontalDisplayResolution = horizontalPpm;
            VerticalDisplayResolution = verticalPpm;
        }

        /// <summary>
        /// Returns true if any resolution data is present.
        /// </summary>
        public bool HasResolution => HorizontalCaptureResolution.HasValue ||
                                      VerticalCaptureResolution.HasValue ||
                                      HorizontalDisplayResolution.HasValue ||
                                      VerticalDisplayResolution.HasValue;

        /// <summary>
        /// Returns true if capture resolution data is present.
        /// </summary>
        public bool HasCaptureResolution => HorizontalCaptureResolution.HasValue &&
                                             VerticalCaptureResolution.HasValue;

        /// <summary>
        /// Returns true if display resolution data is present.
        /// </summary>
        public bool HasDisplayResolution => HorizontalDisplayResolution.HasValue &&
                                             VerticalDisplayResolution.HasValue;

        /// <summary>
        /// Returns a string representation of the resolution data.
        /// </summary>
        public override string ToString()
        {
            var parts = new System.Collections.Generic.List<string>();

            if (HasCaptureResolution)
            {
                parts.Add($"Capture: {HorizontalCaptureDpi:F2}x{VerticalCaptureDpi:F2} DPI");
            }

            if (HasDisplayResolution)
            {
                parts.Add($"Display: {HorizontalDisplayDpi:F2}x{VerticalDisplayDpi:F2} DPI");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "No resolution data";
        }

        /// <summary>
        /// Common DPI values for convenience.
        /// </summary>
        public static class CommonDpi
        {
            /// <summary>72 DPI - Screen resolution (old Mac standard)</summary>
            public const double Screen72 = 72.0;

            /// <summary>96 DPI - Windows default screen resolution</summary>
            public const double Screen96 = 96.0;

            /// <summary>150 DPI - Low quality print</summary>
            public const double Print150 = 150.0;

            /// <summary>300 DPI - Standard print quality</summary>
            public const double Print300 = 300.0;

            /// <summary>600 DPI - High quality print</summary>
            public const double Print600 = 600.0;

            /// <summary>1200 DPI - Very high quality print</summary>
            public const double Print1200 = 1200.0;
        }
    }
}
