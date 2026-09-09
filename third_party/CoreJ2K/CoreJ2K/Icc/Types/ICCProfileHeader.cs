/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Icc.Types
{


    /// <summary> An ICC profile contains a 128-byte header followed by a variable
    /// number of tags contained in a tag table. This class models the header
    /// portion of the profile.  Most fields in the header are ints.  Some, such
    /// as data and version are aggregations of ints. This class provides an api to
    /// those fields as well as the definition of standard constants which are used 
    /// in the header.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>

    public class ICCProfileHeader
    {
        /// <summary>ICCProfile header byte array. </summary>
        //private byte[] header = null;


        /* Define the set of standard signature and type values. Only
		* those codes required for Restricted ICC use are defined here.
		*/
        /// <summary>Profile header signature </summary>
        private static int kdwProfileSignature = ICCProfile.GetInt(System.Text.Encoding.UTF8.GetBytes("acsp"), 0);

        /// <summary>Profile header signature </summary>
        public static int kdwProfileSigReverse = ICCProfile.GetInt(System.Text.Encoding.UTF8.GetBytes("psca"), 0);

        private const string kdwInputProfile = "scnr";
        private const string kdwDisplayProfile = "mntr";
        private const string kdwRGBData = "RGB ";
        private const string kdwGrayData = "GRAY";
        private const string kdwXYZData = "XYZ ";
        private const string kdwGrayTRCTag = "kTRC";
        private const string kdwRedColorantTag = "rXYZ";
        private const string kdwGreenColorantTag = "gXYZ";
        private const string kdwBlueColorantTag = "bXYZ";
        private const string kdwRedTRCTag = "rTRC";
        private const string kdwGreenTRCTag = "gTRC";
        private const string kdwBlueTRCTag = "bTRC";

        /* Offsets into ICCProfile header byte array. */

        private static readonly int offProfileSize = 0;
        private static readonly int offCMMTypeSignature = offProfileSize + ICCProfile.int_size;
        private static readonly int offProfileVersion = offCMMTypeSignature + ICCProfile.int_size;
        private static readonly int offProfileClass = offProfileVersion + ICCProfileVersion.size;
        private static readonly int offColorSpaceType = offProfileClass + ICCProfile.int_size;
        private static readonly int offPCSType = offColorSpaceType + ICCProfile.int_size;
        private static readonly int offDateTime = offPCSType + ICCProfile.int_size;
        private static readonly int offProfileSignature = offDateTime + ICCDateTime.size;
        private static readonly int offPlatformSignature = offProfileSignature + ICCProfile.int_size;
        private static readonly int offCMMFlags = offPlatformSignature + ICCProfile.int_size;
        private static readonly int offDeviceManufacturer = offCMMFlags + ICCProfile.int_size;
        private static readonly int offDeviceModel = offDeviceManufacturer + ICCProfile.int_size;
        private static readonly int offDeviceAttributes1 = offDeviceModel + ICCProfile.int_size;
        private static readonly int offDeviceAttributesReserved = offDeviceAttributes1 + ICCProfile.int_size;
        private static readonly int offRenderingIntent = offDeviceAttributesReserved + ICCProfile.int_size;
        private static readonly int offPCSIlluminant = offRenderingIntent + ICCProfile.int_size;
        private static readonly int offCreatorSig = offPCSIlluminant + XYZNumber.size;
        private static readonly int offReserved = offCreatorSig + ICCProfile.int_size;
        /// <summary>Size of the header </summary>
        public static int size = offReserved + 44 * ICCProfile.byte_size;

        /// <summary>Header field </summary>
        /* Header fields mapped to primitive types. */
        public int dwProfileSize;
        /// <summary>Header field </summary>
        // Size of the entire profile in bytes	
        public int dwCMMTypeSignature;
        /// <summary>Header field </summary>
        // The preferred CMM for this profile
        public int dwProfileClass;
        /// <summary>Header field </summary>
        // Profile/Device class signature
        public int dwColorSpaceType;
        /// <summary>Header field </summary>
        // Colorspace signature
        public int dwPCSType;
        /// <summary>Header field </summary>
        // PCS type signature
        public int dwProfileSignature;
        /// <summary>Header field </summary>
        // Must be 'acsp' (0x61637370)
        public int dwPlatformSignature;
        /// <summary>Header field </summary>
        // Primary platform for which this profile was created
        public int dwCMMFlags;
        /// <summary>Header field </summary>
        // Flags to indicate various hints for the CMM
        public int dwDeviceManufacturer;
        /// <summary>Header field </summary>
        // Signature of device manufacturer
        public int dwDeviceModel;
        /// <summary>Header field </summary>
        // Signature of device model
        public int dwDeviceAttributes1;
        /// <summary>Header field </summary>
        // Attributes of the device
        public int dwDeviceAttributesReserved;
        /// <summary>Header field </summary>
        public int dwRenderingIntent;
        /// <summary>Header field </summary>
        // Desired rendering intent for this profile
        public int dwCreatorSig;
        /// <summary>Header field </summary>
        // Profile creator signature

        public byte[] reserved = new byte[44]; // 

        /// <summary>Header field </summary>
        /* Header fields mapped to ggregate types. */
        public ICCProfileVersion? profileVersion;
        /// <summary>Header field </summary>
        // Version of the profile format on which
        public ICCDateTime? dateTime;
        /// <summary>Header field </summary>
        // Date and time of profile creation// this profile is based
        public XYZNumber? PCSIlluminant; // Illuminant used for this profile


        /// <summary>Construct and empty header </summary>
        public ICCProfileHeader()
        {
        }

        /// <summary> Construct a header from a complete ICCProfile</summary>
        /// <param name="byte">[] -- holds ICCProfile contents
        /// </param>
        public ICCProfileHeader(byte[] data)
        {

            dwProfileSize = ICCProfile.GetInt(data, offProfileSize);
            dwCMMTypeSignature = ICCProfile.GetInt(data, offCMMTypeSignature);
            dwProfileClass = ICCProfile.GetInt(data, offProfileClass);
            dwColorSpaceType = ICCProfile.GetInt(data, offColorSpaceType);
            dwPCSType = ICCProfile.GetInt(data, offPCSType);
            dwProfileSignature = ICCProfile.GetInt(data, offProfileSignature);
            dwPlatformSignature = ICCProfile.GetInt(data, offPlatformSignature);
            dwCMMFlags = ICCProfile.GetInt(data, offCMMFlags);
            dwDeviceManufacturer = ICCProfile.GetInt(data, offDeviceManufacturer);
            dwDeviceModel = ICCProfile.GetInt(data, offDeviceModel);
            dwDeviceAttributes1 = ICCProfile.GetInt(data, offDeviceAttributesReserved);
            dwDeviceAttributesReserved = ICCProfile.GetInt(data, offDeviceAttributesReserved);
            dwRenderingIntent = ICCProfile.GetInt(data, offRenderingIntent);
            dwCreatorSig = ICCProfile.GetInt(data, offCreatorSig);
            profileVersion = ICCProfile.GetICCProfileVersion(data, offProfileVersion);
            dateTime = ICCProfile.GetICCDateTime(data, offDateTime);
            PCSIlluminant = ICCProfile.GetXYZNumber(data, offPCSIlluminant);

            for (var i = 0; i < reserved.Length; ++i)
                reserved[i] = data[offReserved + i];
        }

        /// <summary> Write out this ICCProfile header to a RandomAccessFile</summary>
        /// <param name="raf">sink for data
        /// </param>
        /// <exception cref="IOException">
        /// </exception>
        public virtual void write(System.IO.Stream raf)
        {

            raf.Seek(offProfileSize, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwProfileSize);
            raf.Seek(offCMMTypeSignature, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwCMMTypeSignature);
            raf.Seek(offProfileVersion, System.IO.SeekOrigin.Begin); profileVersion!.write(raf);
            raf.Seek(offProfileClass, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwProfileClass);
            raf.Seek(offColorSpaceType, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwColorSpaceType);
            raf.Seek(offPCSType, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwPCSType);
            raf.Seek(offDateTime, System.IO.SeekOrigin.Begin); dateTime!.write(raf);
            raf.Seek(offProfileSignature, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwProfileSignature);
            raf.Seek(offPlatformSignature, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwPlatformSignature);
            raf.Seek(offCMMFlags, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwCMMFlags);
            raf.Seek(offDeviceManufacturer, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwDeviceManufacturer);
            raf.Seek(offDeviceModel, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwDeviceModel);
            raf.Seek(offDeviceAttributes1, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwDeviceAttributes1);
            raf.Seek(offDeviceAttributesReserved, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwDeviceAttributesReserved);
            raf.Seek(offRenderingIntent, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwRenderingIntent);
            raf.Seek(offPCSIlluminant, System.IO.SeekOrigin.Begin); PCSIlluminant!.write(raf);
            raf.Seek(offCreatorSig, System.IO.SeekOrigin.Begin); raf.WriteByte((byte)dwCreatorSig);
            raf.Seek(offReserved, System.IO.SeekOrigin.Begin);
            raf.Write(reserved, 0, reserved.Length);
            //WriteRandomFile(reserved, raf);
        }


        /// <summary>String representation of class </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ICCProfileHeader: ");

            rep.Append($"{Environment.NewLine}         ProfileSize: {Convert.ToString(dwProfileSize, 16)}");
            rep.Append($"{Environment.NewLine}    CMMTypeSignature: {Convert.ToString(dwCMMTypeSignature, 16)}");
            rep.Append($"{Environment.NewLine}        ProfileClass: {Convert.ToString(dwProfileClass, 16)}");
            rep.Append($"{Environment.NewLine}      ColorSpaceType: {Convert.ToString(dwColorSpaceType, 16)}");
            rep.Append($"{Environment.NewLine}           dwPCSType: {Convert.ToString(dwPCSType, 16)}");
            rep.Append($"{Environment.NewLine}  dwProfileSignature: {Convert.ToString(dwProfileSignature, 16)}");
            rep.Append($"{Environment.NewLine} dwPlatformSignature: {Convert.ToString(dwPlatformSignature, 16)}");
            rep.Append($"{Environment.NewLine}          dwCMMFlags: {Convert.ToString(dwCMMFlags, 16)}");
            rep.Append($"{Environment.NewLine}dwDeviceManufacturer: {Convert.ToString(dwDeviceManufacturer, 16)}");
            rep.Append($"{Environment.NewLine}       dwDeviceModel: {Convert.ToString(dwDeviceModel, 16)}");
            rep.Append($"{Environment.NewLine} dwDeviceAttributes1: {Convert.ToString(dwDeviceAttributes1, 16)}");
            rep.Append($"{Environment.NewLine}   dwRenderingIntent: {Convert.ToString(dwRenderingIntent, 16)}");
            rep.Append($"{Environment.NewLine}        dwCreatorSig: {Convert.ToString(dwCreatorSig, 16)}");
            rep.Append($"{Environment.NewLine}      profileVersion: {profileVersion}");
            rep.Append($"{Environment.NewLine}            dateTime: {dateTime}");
            rep.Append($"{Environment.NewLine}       PCSIlluminant: {PCSIlluminant}");
            return rep.Append("]").ToString();
        }
    }
}