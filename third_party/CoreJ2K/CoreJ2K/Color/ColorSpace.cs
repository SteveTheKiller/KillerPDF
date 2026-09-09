/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using FileFormatBoxes = CoreJ2K.j2k.fileformat.FileFormatBoxes;
using io_RandomAccessIO = CoreJ2K.j2k.io.RandomAccessIO;
using reader_HeaderDecoder = CoreJ2K.j2k.codestream.reader.HeaderDecoder;
using util_ParameterList = CoreJ2K.j2k.util.ParameterList;

namespace CoreJ2K.Color
{
    using ChannelDefinitionBox = Boxes.ChannelDefinitionBox;
    using ColorSpecificationBox = Boxes.ColorSpecificationBox;
    using ComponentMappingBox = Boxes.ComponentMappingBox;
    using ImageHeaderBox = Boxes.ImageHeaderBox;
    using PaletteBox = Boxes.PaletteBox;

    /// <summary> This class analyzes the image to provide colorspace
    /// information for the decoding chain.  It does this by
    /// examining the box structure of the JP2 image.
    /// It also provides access to the parameter list information,
    /// which is stored as a public final field.
    /// 
    /// </summary>
    /// <seealso cref="j2k.icc.ICCProfile" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class ColorSpace
    {
        /// <summary> Retrieve the ICC profile from the images as
        /// a byte array.
        /// </summary>
        /// <returns> the ICC Profile as a byte [].
        /// </returns>
        public virtual byte[] ICCProfile => csbox.ICCProfile;

        /// <summary>Return the colorspace method (Profiled, enumerated, or palettized). </summary>
        public virtual MethodEnum Method => csbox.Method;

        /// <summary>Return number of channels in the palette. </summary>
        public virtual PaletteBox PaletteBox => pbox;

        /// <summary>Return number of channels in the palette. </summary>
        public virtual int PaletteChannels => pbox?.NumColumns ?? 0;

        /// <summary>Is palettized predicate. </summary>
        public virtual bool Palettized => pbox != null;

        // Renamed for convenience:
        internal const int GRAY = 0;
        internal const int RED = 1;
        internal const int GREEN = 2;
        internal const int BLUE = 3;

        /// <summary>Parameter Specs </summary>
        public util_ParameterList pl;

        /// <summary>Parameter Specs </summary>
        public reader_HeaderDecoder hd;

        /* Image box structure as pertains to colorspacees. */
        private PaletteBox? pbox = null;
        private ComponentMappingBox? cmbox = null;
        private ColorSpecificationBox? csbox = null;
        private ChannelDefinitionBox? cdbox = null;
        private ImageHeaderBox? ihbox = null;

        /// <summary>Input image </summary>
        private readonly io_RandomAccessIO? inStream = null;

        /// <summary>Indent a String that contains newlines. </summary>
        public static string indent(string ident, System.Text.StringBuilder instr)
        {
            return indent(ident, instr.ToString());
        }

        /// <summary>Indent a String that contains newlines. </summary>
        public static string indent(string ident, string instr)
        {
            var tgt = new System.Text.StringBuilder(instr);
            var eolChar = Environment.NewLine[0];
            var i = tgt.Length;
            while (--i > 0)
            {
                if (tgt[i] == eolChar)
                    tgt.Insert(i + 1, ident);
            }
            return ident + tgt;
        }

        /// <summary> public constructor which takes in the image, parameterlist and the
        /// image header decoder as args.
        /// </summary>
        /// <param name="in">input RandomAccess image file.
        /// </param>
        /// <param name="hd">provides information about the image header.
        /// </param>
        /// <param name="pl">provides parameters from the default and commandline lists. 
        /// </param>
        /// <exception cref="IOException,">ColorSpaceException
        /// </exception>
        public ColorSpace(io_RandomAccessIO inStream, reader_HeaderDecoder hd, util_ParameterList pl)
        {
            this.pl = pl;
            this.inStream = inStream;
            this.hd = hd;
            GetBoxes();
        }

        /// <summary> Retrieve the various boxes from the JP2 file.</summary>
        /// <exception cref="ColorSpaceException,">IOException
        /// </exception>
        protected internal void GetBoxes()
        {
            //byte[] data;
            int type;
            long len = 0;
            var boxStart = 0;
            var boxHeader = new byte[16];
            var i = 0;

            // Search the toplevel boxes for the header box
            while (true)
            {
                inStream.seek(boxStart);
                inStream.readFully(boxHeader, 0, 16);
                // CONVERSION PROBLEM?

                len = Icc.ICCProfile.GetInt(boxHeader, 0);
                if (len == 1)
                    len = Icc.ICCProfile.GetLong(boxHeader, 8); // Extended
                                                                // length
                type = Icc.ICCProfile.GetInt(boxHeader, 4);

                // Verify the contents of the file so far.
                if (i == 0 && type != FileFormatBoxes.JP2_SIGNATURE_BOX)
                {
                    throw new ColorSpaceException("first box in image not " + "signature");
                }
                else if (i == 1 && type != FileFormatBoxes.FILE_TYPE_BOX)
                {
                    throw new ColorSpaceException("second box in image not file");
                }
                else if (type == FileFormatBoxes.CONTIGUOUS_CODESTREAM_BOX)
                {
                    throw new ColorSpaceException("header box not found in image");
                }
                else if (type == FileFormatBoxes.JP2_HEADER_BOX)
                {
                    break;
                }

                // Progress to the next box.
                ++i;
                boxStart = (int)(boxStart + len);
            }

            // boxStart indexes the start of the JP2_HEADER_BOX,
            // make headerBoxEnd index the end of the box.
            var headerBoxEnd = boxStart + len;

            if (len == 1)
                boxStart += 8; // Extended length header

            for (boxStart += 8; boxStart < headerBoxEnd; boxStart = (int)(boxStart + len))
            {
                inStream.seek(boxStart);
                inStream.readFully(boxHeader, 0, 16);
                len = Icc.ICCProfile.GetInt(boxHeader, 0);
                if (len == 1)
                    throw new ColorSpaceException("Extended length boxes " + "not supported");
                type = Icc.ICCProfile.GetInt(boxHeader, 4);

                switch (type)
                {

                    case FileFormatBoxes.IMAGE_HEADER_BOX:
                        ihbox = new ImageHeaderBox(inStream, boxStart);
                        break;

                    case FileFormatBoxes.COLOUR_SPECIFICATION_BOX:
                        csbox = new ColorSpecificationBox(inStream, boxStart);
                        break;

                    case FileFormatBoxes.CHANNEL_DEFINITION_BOX:
                        cdbox = new ChannelDefinitionBox(inStream, boxStart);
                        break;

                    case FileFormatBoxes.COMPONENT_MAPPING_BOX:
                        cmbox = new ComponentMappingBox(inStream, boxStart);
                        break;

                    case FileFormatBoxes.PALETTE_BOX:
                        pbox = new PaletteBox(inStream, boxStart);
                        break;

                    default:
                        break;

                }
            }

            if (ihbox == null)
                throw new ColorSpaceException("image header box not found");

            if ((pbox == null && cmbox != null) || (pbox != null && cmbox == null))
                throw new ColorSpaceException("palette box and component " + "mapping box inconsistency");
        }


        /// <summary>Return the channel definition of the input component. </summary>
        public virtual int GetChannelDefinition(int c)
        {
            return cdbox?.GetCn(c + 1) ?? c;
        }

        /// <summary>Return the colorspace (sYCC, sRGB, sGreyScale). </summary>
        public virtual CSEnum GetColorSpace()
        {
            return csbox.ColorSpace;
        }

        /// <summary>Return bitdepth of the palette entries. </summary>
        public virtual int GetPaletteChannelBits(int c)
        {
            return pbox?.GetBitDepth(c) ?? 0;
        }

        /// <summary> Return a palettized sample</summary>
        /// <param name="channel">requested 
        /// </param>
        /// <param name="index">of entry
        /// </param>
        /// <returns> palettized sample
        /// </returns>
        public virtual int GetPalettizedSample(int channel, int index)
        {
            return pbox?.GetEntry(index, channel) ?? 0;
        }

        /// <summary>Signed output predicate. </summary>
        public virtual bool IsOutputSigned(int channel)
        {
            return pbox?.IsSigned(channel) ?? hd.IsOriginalSigned(channel);
        }

        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ColorSpace is ").Append(csbox.MethodString).Append(Palettized ? "  and palettized " : " ").Append(Method == MethodEnum.ENUMERATED ? csbox.ColorSpaceString : "");
            if (ihbox != null)
            {
                rep.Append(Environment.NewLine).Append(indent("    ", ihbox.ToString()));
            }
            if (cdbox != null)
            {
                rep.Append(Environment.NewLine).Append(indent("    ", cdbox.ToString()));
            }
            if (csbox != null)
            {
                rep.Append(Environment.NewLine).Append(indent("    ", csbox.ToString()));
            }
            if (pbox != null)
            {
                rep.Append(Environment.NewLine).Append(indent("    ", pbox.ToString()));
            }
            if (cmbox != null)
            {
                rep.Append(Environment.NewLine).Append(indent("    ", cmbox.ToString()));
            }
            return rep.Append("]").ToString();
        }

        /// <summary> Are profiling diagnostics turned on</summary>
        /// <returns> yes or no
        /// </returns>
        public virtual bool debugging()
        {
            return pl.TryGetValue("colorspace_debug", out var tmp) && string.Equals(tmp, "ON", StringComparison.OrdinalIgnoreCase);
        }


        public enum MethodEnum
        {
            ICC_PROFILED,
            ENUMERATED
        }
        public enum CSEnum
        {
            sRGB,
            GreyScale,
            sYCC,
            esRGB,
            Illegal,
            Unknown
        }
        /* Enumeration Class */
        /*
		/// <summary>method enumeration </summary>
		public const MethodEnum ICC_PROFILED = new MethodEnum("profiled");
		/// <summary>method enumeration </summary>
		public const MethodEnum ENUMERATED = new MethodEnum("enumerated");
		
		/// <summary>colorspace enumeration </summary>
		public const CSEnum sRGB = new CSEnum("sRGB");
		/// <summary>colorspace enumeration </summary>
		public const CSEnum GreyScale = new CSEnum("GreyScale");
		/// <summary>colorspace enumeration </summary>
		public const CSEnum sYCC = new CSEnum("sYCC");
		/// <summary>colorspace enumeration </summary>
		public const CSEnum Illegal = new CSEnum("Illegal");
		/// <summary>colorspace enumeration </summary>
		public const CSEnum Unknown = new CSEnum("Unknown");
		
		/// <summary> Typesafe enumeration class</summary>
		/// <version> 	1.0
		/// </version>
		/// <author> 	Bruce A Kern
		/// </author>
		public class Enumeration
		{
			public System.String value;
			public Enumeration(System.String value)
			{
				this.value = value;
			}
			public override System.String ToString()
			{
				return value;
			}
		}
		
        		
		/// <summary> Method enumeration class</summary>
		/// <version> 	1.0
		/// </version>
		/// <author> 	Bruce A Kern
		/// </author>
		public class MethodEnum:Enumeration
		{
			public MethodEnum(System.String value):base(value)
			{
			}
		}
		
		/// <summary> Colorspace enumeration class</summary>
		/// <version> 	1.0
		/// </version>
		/// <author> 	Bruce A Kern
		/// </author>
		public class CSEnum:Enumeration
		{
			public CSEnum(System.String value):base(value)
			{
			}
		}
		*/
    }
}