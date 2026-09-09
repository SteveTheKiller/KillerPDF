using CoreJ2K.j2k.io;
using CoreJ2K.j2k.util;
/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;

namespace CoreJ2K.Color.Boxes
{

    /// <summary> This class models the Color Specification Box in a JP2 image.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public sealed class ColorSpecificationBox : JP2Box
    {

        public ColorSpace.MethodEnum Method { get; private set; }

        public ColorSpace.CSEnum ColorSpace { get; private set; }

        public string ColorSpaceString =>
            // Return a String representation of the colorspace. 
            ColorSpace.ToString();

        public string MethodString =>
            // Return a String representation of the colorspace method. 
            Method.ToString();

        public byte[]? ICCProfile { get; private set; } = null;

        /// <summary> Construct a ColorSpecificationBox from an input image.</summary>
        /// <param name="in">RandomAccessIO jp2 image
        /// </param>
        /// <param name="boxStart">offset to the start of the box in the image
        /// </param>
        /// <exception cref="IOException,">ColorSpaceException 
        /// 
        /// </exception>
        public ColorSpecificationBox(RandomAccessIO inStream, int boxStart) : base(inStream, boxStart)
        {
            readBox();
        }

        /// <summary>Analyze the box content. </summary>
        private void readBox()
        {
            var boxHeader = new byte[256];
            inStream.seek(dataStart);
            inStream.readFully(boxHeader, 0, 11);
            switch (boxHeader[0])
            {

                case 1:
                    Method = Color.ColorSpace.MethodEnum.ENUMERATED;
                    var cs = Icc.ICCProfile.GetInt(boxHeader, 3);
                    switch (cs)
                    {
                        case 16:
                            ColorSpace = Color.ColorSpace.CSEnum.sRGB;
                            break; // from switch (cs)...

                        case 17:
                            ColorSpace = Color.ColorSpace.CSEnum.GreyScale;
                            break; // from switch (cs)...

                        case 18:
                            ColorSpace = Color.ColorSpace.CSEnum.sYCC;
                            break; // from switch (cs)...
                        case 20:
                            ColorSpace = Color.ColorSpace.CSEnum.esRGB;
                            break;

                        #region Known but unsupported colorspaces
                        case 3:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace YCbCr(2) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 4:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace YCbCr(3) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 9:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace PhotoYCC in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 11:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace CMY in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 12:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace CMYK in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 13:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace YCCK in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 14:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace CIELab in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 15:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace Bi-Level(2) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 19:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace CIEJab in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 21:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace ROMM-RGB in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 22:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace YPbPr(1125/60) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 23:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace YPbPr(1250/50) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        case 24:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING, "Unsupported enumerated colorspace e-sYCC in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;
                        #endregion

                        default:
                            FacilityManager.GetMsgLogger().printmsg(MsgLogger_Fields.WARNING,
                                $"Unknown enumerated colorspace ({cs}) in color specification box");
                            ColorSpace = Color.ColorSpace.CSEnum.Unknown;
                            break;

                    }
                    break; // from switch (boxHeader[0])...

                case 2:
                    Method = Color.ColorSpace.MethodEnum.ICC_PROFILED;
                    var size = Icc.ICCProfile.GetInt(boxHeader, 3);
                    ICCProfile = new byte[size];
                    inStream.seek(dataStart + 3);
                    inStream.readFully(ICCProfile, 0, size);
                    break; // from switch (boxHeader[0])...

                default:
                    throw new ColorSpaceException($"Bad specification method ({boxHeader[0]}) in {this}");

            }
        }

        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ColorSpecificationBox ");
            rep.Append("method= ").Append(Convert.ToString(Method)).Append(", ");
            rep.Append("colorspace= ").Append(Convert.ToString(ColorSpace)).Append("]");
            return rep.ToString();
        }
        static ColorSpecificationBox()
        {
            {
                type = 0x636f6c72;
            }
        }
    }
}