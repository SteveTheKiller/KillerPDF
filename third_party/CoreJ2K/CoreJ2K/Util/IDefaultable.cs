// Copyright (c) 2012-2016 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

namespace CoreJ2K.Util
{
    /// <summary>
    /// Interface for default classification of manager types.
    /// </summary>
    public interface IDefaultable
    {
        /// <summary>
        /// Gets whether or not this type is classified as a default manager.
        /// </summary>
        bool IsDefault { get; }
    }
}