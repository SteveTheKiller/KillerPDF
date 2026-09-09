/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using ICCProfile = CoreJ2K.Icc.ICCProfile;
using io_RandomAccessIO = CoreJ2K.j2k.io.RandomAccessIO;

namespace CoreJ2K.Color.Boxes
{

    /// <summary> This class models the Image Header box contained in a JP2
    /// image.  It is a stub class here since for colormapping the
    /// knowlege of the existance of the box in the image is sufficient.
    /// 
    /// </summary>
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public sealed class ImageHeaderBox : JP2Box
    {

        internal long height;
        internal long width;
        internal int nc;
        internal short bpc;
        internal short c;
        internal bool unk;
        internal bool ipr;


        /// <summary> Construct an ImageHeaderBox from an input image.</summary>
        /// <param name="in">RandomAccessIO jp2 image
        /// </param>
        /// <param name="boxStart">offset to the start of the box in the image
        /// </param>
        /// <exception cref="IOException,">ColorSpaceException
        /// </exception>
        public ImageHeaderBox(io_RandomAccessIO inStream, int boxStart) : base(inStream, boxStart)
        {
            readBox();
        }

        /// <summary>Return a suitable String representation of the class instance. </summary>
        public override string ToString()
        {
            var rep = new System.Text.StringBuilder("[ImageHeaderBox ").Append(Environment.NewLine).Append("  ");
            rep.Append("height= ").Append(Convert.ToString(height)).Append(", ");
            rep.Append("width= ").Append(Convert.ToString(width)).Append(Environment.NewLine).Append("  ");

            rep.Append("nc= ").Append(Convert.ToString(nc)).Append(", ");
            rep.Append("bpc= ").Append(Convert.ToString(bpc)).Append(", ");
            rep.Append("c= ").Append(Convert.ToString(c)).Append(Environment.NewLine).Append("  ");

            rep.Append("image colorspace is ").Append(new System.Text.StringBuilder(unk ? "known" : "unknown"));
            rep.Append(", the image ").Append(new System.Text.StringBuilder(ipr ? "contains " : "does not contain ")).Append("intellectual property").Append("]");

            return rep.ToString();
        }

        /// <summary>Analyze the box content. </summary>
        internal void readBox()
        {
            var bfr = new byte[14];
            inStream.seek(dataStart);
            inStream.readFully(bfr, 0, 14);

            height = ICCProfile.GetInt(bfr, 0);
            width = ICCProfile.GetInt(bfr, 4);
            nc = ICCProfile.GetShort(bfr, 8);
            bpc = (short)(bfr[10] & 0x00ff);
            c = (short)(bfr[11] & 0x00ff);
            unk = bfr[12] == 0;
            ipr = bfr[13] == 1;
        }
        static ImageHeaderBox()
        {
            {
                type = 69686472;
            }
        }
    }
}