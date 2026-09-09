// Copyright (c) 2025 Sjofn LLC.
// Licensed under the BSD 3-Clause License.

using System;

namespace CoreJ2K.Color.ICC
{
    /// <summary>
    /// Represents ICC profile data that can be embedded in or extracted from JPEG2000 files.
    /// Provides basic validation and access to ICC profile bytes.
    /// </summary>
    public class ICCProfileData
    {
        /// <summary>
        /// Gets the raw ICC profile bytes.
        /// </summary>
        public byte[]? ProfileBytes { get; }

        /// <summary>
        /// Gets the ICC profile version (major.minor).
        /// </summary>
        public Version? ProfileVersion { get; }

        /// <summary>
        /// Gets the ICC profile size in bytes.
        /// </summary>
        public int ProfileSize => ProfileBytes?.Length ?? 0;

        /// <summary>
        /// Gets the color space type from the profile header.
        /// </summary>
        public string? ColorSpaceType { get; }

        /// <summary>
        /// Gets the profile/device class from the profile header.
        /// </summary>
        public string? ProfileClass { get; }

        /// <summary>
        /// Gets whether this is a valid ICC profile.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Creates an ICC profile data instance from raw bytes.
        /// </summary>
        /// <param name="profileBytes">The ICC profile bytes.</param>
        public ICCProfileData(byte[] profileBytes)
        {
            if (profileBytes == null || profileBytes.Length < 128)
            {
                IsValid = false;
                ProfileBytes = profileBytes;
                return;
            }

            ProfileBytes = (byte[])profileBytes.Clone();
            
            // Validate and parse ICC profile header
            try
            {
                // ICC profile size is at offset 0-3 (big-endian)
                var declaredSize = ReadBigEndianInt32(profileBytes, 0);
                if (declaredSize != profileBytes.Length)
                {
                    IsValid = false;
                    return;
                }

                // Profile version at offset 8-9
                var versionMajor = profileBytes[8];
                var versionMinor = profileBytes[9] >> 4;
                ProfileVersion = new Version(versionMajor, versionMinor);

                // Profile/Device class at offset 12-15 (4 ASCII chars)
                ProfileClass = ReadAsciiString(profileBytes, 12, 4);

                // Color space at offset 16-19 (4 ASCII chars)
                ColorSpaceType = ReadAsciiString(profileBytes, 16, 4);

                IsValid = true;
            }
            catch
            {
                IsValid = false;
            }
        }

        /// <summary>
        /// Reads a big-endian 32-bit integer from byte array.
        /// </summary>
        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | 
                   (data[offset + 2] << 8) | data[offset + 3];
        }

        /// <summary>
        /// Reads an ASCII string from byte array.
        /// </summary>
        private static string ReadAsciiString(byte[] data, int offset, int length)
        {
            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = (char)data[offset + i];
            }
            return new string(chars).Trim('\0');
        }

        /// <summary>
        /// Returns a string representation of the ICC profile.
        /// </summary>
        public override string ToString()
        {
            if (!IsValid)
                return "Invalid ICC Profile";

            return $"ICC Profile v{ProfileVersion} [{ProfileClass}] {ColorSpaceType} ({ProfileSize} bytes)";
        }

        /// <summary>
        /// Common ICC profile color space signatures.
        /// </summary>
        public static class ColorSpaces
        {
            public const string XYZ = "XYZ ";
            public const string Lab = "Lab ";
            public const string Luv = "Luv ";
            public const string YCbCr = "YCbr";
            public const string Yxy = "Yxy ";
            public const string RGB = "RGB ";
            public const string Gray = "GRAY";
            public const string HSV = "HSV ";
            public const string HLS = "HLS ";
            public const string CMYK = "CMYK";
            public const string CMY = "CMY ";
        }

        /// <summary>
        /// Common ICC profile class signatures.
        /// </summary>
        public static class ProfileClasses
        {
            public const string Input = "scnr";
            public const string Display = "mntr";
            public const string Output = "prtr";
            public const string Link = "link";
            public const string ColorSpace = "spac";
            public const string Abstract = "abst";
            public const string NamedColor = "nmcl";
        }
    }
}
