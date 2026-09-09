/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using System.Text;

namespace CoreJ2K.Icc.Tags
{

    /// <summary> An ICC profile contains a 128-byte header followed by a variable
    /// number of tags contained in a tag table. Each tag is a structured 
    /// block of ints. The tags share a common format on disk starting with 
    /// a signature, an offset to the tag data, and a length of the tag data.
    /// The tag data itself is found at the given offset in the file and 
    /// consists of a tag type int, followed by a reserved int, followed by
    /// a data block, the structure of which is unique to the tag type.
    /// 
    /// This class is the abstract super class of all tags. It models that 
    /// part of the structure which is common among tags of all types.
    /// It also contains the definitions of the various tag types.
    /// 
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.tags.ICCTagTable" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public abstract class ICCTag
    {

        // Tag Signature Strings
        private const string sdwCprtSignature = "cprt";
        private const string sdwDescSignature = "desc";
        private const string sdwWtPtSignature = "wtpt";
        private const string sdwBkPtSignature = "bkpt";
        private const string sdwRXYZSignature = "rXYZ";
        private const string sdwGXYZSignature = "gXYZ";
        private const string sdwBXYZSignature = "bXYZ";
        private const string sdwKXYZSignature = "kXYZ";
        private const string sdwRTRCSignature = "rTRC";
        private const string sdwGTRCSignature = "gTRC";
        private const string sdwBTRCSignature = "bTRC";
        private const string sdwKTRCSignature = "kTRC";
        private const string sdwDmndSignature = "dmnd";
        private const string sdwDmddSignature = "dmdd";

        // Tag Signatures
        private static readonly int kdwCprtSignature;
        private static readonly int kdwDescSignature;
        private static readonly int kdwWtPtSignature;
        private static readonly int kdwBkPtSignature;
        private static readonly int kdwRXYZSignature;
        private static readonly int kdwGXYZSignature;
        private static readonly int kdwBXYZSignature;
        private static readonly int kdwKXYZSignature;
        private static readonly int kdwRTRCSignature;
        private static readonly int kdwGTRCSignature;
        private static readonly int kdwBTRCSignature;
        private static readonly int kdwKTRCSignature;
        private static readonly int kdwDmndSignature;
        private static readonly int kdwDmddSignature;

        // Tag Type Strings
        private const string sdwTextDescType = "desc";
        private const string sdwTextType = "text";
        private const string sdwCurveType = "curv";
        private const string sdwCurveTypeReverse = "vruc";
        private const string sdwXYZType = "XYZ ";
        private const string sdwXYZTypeReverse = " ZYX";
        private const string sdwMeasurementType = "meas";
        private const string sdwSignatureType = "sig ";
        private const string sdwViewType = "view";
        private const string sdwDataType = "data";

        // Tag Types
        private static readonly int kdwTextDescType;
        private static readonly int kdwTextType;
        private static readonly int kdwCurveType;
        private static readonly int kdwCurveTypeReverse;
        private static readonly int kdwXYZType;
        private static readonly int kdwXYZTypeReverse;
        private static readonly int kdwMeasurementType;
        private static readonly int kdwSignatureType;
        private static readonly int kdwViewType;
        private static readonly int kdwDataType;

        /// <summary>Tag id                            </summary>
        public int signature;
        /// <summary>Tag type                          </summary>
        // Tag signature
        public int type;
        /// <summary>Tag data                          </summary>
        public byte[] data;
        /// <summary>offset to tag data in the array   </summary>
        // Tag type
        public int offset;
        /// <summary>size of the tag data in the array </summary>
        public int count;

        /// <summary> Create a string representation of the tag type</summary>
        /// <param name="type">input
        /// </param>
        /// <returns> String representation of the type
        /// </returns>
        public static string typeString(int type)
        {
            if (type == kdwTextDescType)
                return sdwTextDescType;
            else if (type == kdwTextType)
                return sdwTextDescType;
            else if (type == kdwCurveType)
                return sdwCurveType;
            else if (type == kdwCurveTypeReverse)
                return sdwCurveTypeReverse;
            else if (type == kdwXYZType)
                return sdwXYZType;
            else if (type == kdwXYZTypeReverse)
                return sdwXYZTypeReverse;
            else if (type == kdwMeasurementType)
                return sdwMeasurementType;
            else if (type == kdwSignatureType)
                return sdwSignatureType;
            else if (type == kdwViewType)
                return sdwViewType;
            else if (type == kdwDataType)
                return sdwDataType;
            else
                return "bad tag type";
        }


        /// <summary> Create a string representation of the signature</summary>
        /// <param name="signature">input
        /// </param>
        /// <returns> String representation of the signature
        /// </returns>
        public static string signatureString(int signature)
        {

            if (signature == kdwCprtSignature)
                return sdwCprtSignature;
            else if (signature == kdwDescSignature)
                return sdwDescSignature;
            else if (signature == kdwWtPtSignature)
                return sdwWtPtSignature;
            else if (signature == kdwBkPtSignature)
                return sdwBkPtSignature;
            else if (signature == kdwRXYZSignature)
                return sdwRXYZSignature;
            else if (signature == kdwGXYZSignature)
                return sdwGXYZSignature;
            else if (signature == kdwBXYZSignature)
                return sdwBXYZSignature;
            else if (signature == kdwRTRCSignature)
                return sdwRTRCSignature;
            else if (signature == kdwGTRCSignature)
                return sdwGTRCSignature;
            else if (signature == kdwBTRCSignature)
                return sdwBTRCSignature;
            else if (signature == kdwKTRCSignature)
                return sdwKTRCSignature;
            else if (signature == kdwDmndSignature)
                return sdwDmndSignature;
            else if (signature == kdwDmddSignature)
                return sdwDmddSignature;
            else
                return "bad tag signature";
        }


        /// <summary> Factory method for creating a tag of a specific type.</summary>
        /// <param name="signature">tag to create
        /// </param>
        /// <param name="data">byte array containg embedded tag data
        /// </param>
        /// <param name="offset">to tag data in the array
        /// </param>
        /// <param name="count">size of tag data in bytes
        /// </param>
        /// <returns> specified ICCTag
        /// </returns>
        public static ICCTag createInstance(int signature, byte[] data, int offset, int count)
        {

            var type = ICCProfile.GetInt(data, offset);

            if (type == kdwTextDescType)
                return new ICCTextDescriptionType(signature, data, offset, count);
            else if (type == kdwTextType)
                return new ICCTextType(signature, data, offset, count);
            else if (type == kdwXYZType)
                return new ICCXYZType(signature, data, offset, count);
            else if (type == kdwXYZTypeReverse)
                return new ICCXYZTypeReverse(signature, data, offset, count);
            else if (type == kdwCurveType)
                return new ICCCurveType(signature, data, offset, count);
            else if (type == kdwCurveTypeReverse)
                return new ICCCurveTypeReverse(signature, data, offset, count);
            else if (type == kdwMeasurementType)
                return new ICCMeasurementType(signature, data, offset, count);
            else if (type == kdwSignatureType)
                return new ICCSignatureType(signature, data, offset, count);
            else if (type == kdwViewType)
                return new ICCViewType(signature, data, offset, count);
            else if (type == kdwDataType)
                return new ICCDataType(signature, data, offset, count);
            else
            {
                var bytes = BitConverter.GetBytes(type);
                throw new ArgumentException($"bad tag type: {Encoding.UTF8.GetString(bytes, 0, bytes.Length)}({type})");
            }
        }


        /// <summary> Ued by subclass initialization to store the state common to all tags</summary>
        /// <param name="signature">tag being created
        /// </param>
        /// <param name="data">byte array containg embedded tag data
        /// </param>
        /// <param name="offset">to tag data in the array
        /// </param>
        /// <param name="count">size of tag data in bytes
        /// </param>
        protected internal ICCTag(int signature, byte[] data, int offset, int count)
        {
            this.signature = signature;
            this.data = data;
            this.offset = offset;
            this.count = count;
            type = ICCProfile.GetInt(data, offset);
        }

        public override string ToString()
        {
            return $"{signatureString(signature)}:{typeString(type)}";
        }
        static ICCTag()
        {
            kdwCprtSignature = GetTagInt(sdwCprtSignature);
            kdwDescSignature = GetTagInt(sdwDescSignature);
            kdwWtPtSignature = GetTagInt(sdwWtPtSignature);
            kdwBkPtSignature = GetTagInt(sdwBkPtSignature);
            kdwRXYZSignature = GetTagInt(sdwRXYZSignature);
            kdwGXYZSignature = GetTagInt(sdwGXYZSignature);
            kdwBXYZSignature = GetTagInt(sdwBXYZSignature);
            kdwKXYZSignature = GetTagInt(sdwKXYZSignature);
            kdwRTRCSignature = GetTagInt(sdwRTRCSignature);
            kdwGTRCSignature = GetTagInt(sdwGTRCSignature);
            kdwBTRCSignature = GetTagInt(sdwBTRCSignature);
            kdwKTRCSignature = GetTagInt(sdwKTRCSignature);
            kdwDmndSignature = GetTagInt(sdwDmndSignature);
            kdwDmddSignature = GetTagInt(sdwDmddSignature);
            kdwTextDescType = GetTagInt(sdwTextDescType);
            kdwTextType = GetTagInt(sdwTextType);
            kdwCurveType = GetTagInt(sdwCurveType);
            kdwCurveTypeReverse = GetTagInt(sdwCurveTypeReverse);
            kdwXYZType = GetTagInt(sdwXYZType);
            kdwXYZTypeReverse = GetTagInt(sdwXYZTypeReverse);
            kdwMeasurementType = GetTagInt(sdwMeasurementType);
            kdwSignatureType = GetTagInt(sdwSignatureType);
            kdwViewType = GetTagInt(sdwViewType);
            kdwDataType = GetTagInt(sdwDataType);
        }

        private static int GetTagInt(string tag)
        {
            var tagBytes = Encoding.UTF8.GetBytes(tag);
            Array.Reverse(tagBytes);
            return BitConverter.ToInt32(tagBytes, 0);
        }
    }
}