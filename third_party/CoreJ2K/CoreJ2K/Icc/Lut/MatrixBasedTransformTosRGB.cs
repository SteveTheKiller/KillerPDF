/// <summary>**************************************************************************
/// 
/// 
/// Copyright Eastman Kodak Company, 343 State Street, Rochester, NY 14650
/// $Date $
/// ***************************************************************************
/// </summary>
using System;
using System.Globalization;
using ColorSpace = CoreJ2K.Color.ColorSpace;
using ICCXYZType = CoreJ2K.Icc.Tags.ICCXYZType;
using image_DataBlkFloat = CoreJ2K.j2k.image.DataBlkFloat;
using image_DataBlkInt = CoreJ2K.j2k.image.DataBlkInt;

namespace CoreJ2K.Icc.Lut
{

    /// <summary> Transform for applying ICCProfiling to an input DataBlk
    /// 
    /// </summary>
    /// <seealso cref="j2k.image.DataBlkInt" />
    /// <seealso cref="j2k.image.DataBlkFloat" />
    /// <version> 	1.0
    /// </version>
    /// <author> 	Bruce A. Kern
    /// </author>
    public class MatrixBasedTransformTosRGB
    {
        // Start of constant definitions:

        private static readonly int RED;
        private static readonly int GREEN;
        private static readonly int BLUE;

        private const double SRGB00 = 3.1337;
        private const double SRGB01 = -1.6173;
        private const double SRGB02 = -0.4907;
        private const double SRGB10 = -0.9785;
        private const double SRGB11 = 1.9162;
        private const double SRGB12 = 0.0334;
        private const double SRGB20 = 0.0720;
        private const double SRGB21 = -0.2290;
        private const double SRGB22 = 1.4056;

        // Define constants representing the indices into the matrix array
        private const int M00 = 0;
        private const int M01 = 1;
        private const int M02 = 2;
        private const int M10 = 3;
        private const int M11 = 4;
        private const int M12 = 5;
        private const int M20 = 6;
        private const int M21 = 7;
        private const int M22 = 8;

        private const double ksRGBExponent = (1.0 / 2.4);
        private const double ksRGBScaleAfterExp = 1.055;
        private const double ksRGBReduceAfterExp = 0.055;
        private const double ksRGBShadowCutoff = 0.0031308;
        private const double ksRGBShadowSlope = 12.92;

        // End of contant definitions:

        private readonly double[] matrix; // Matrix coefficients 

        private readonly LookUpTableFP[] fLut = new LookUpTableFP[3];
        private readonly LookUpTable32LinearSRGBtoSRGB lut; // Linear sRGB to sRGB LUT

        private readonly int[] dwMaxValue;
        private readonly int[] dwShiftValue;

        //private int dwMaxCols = 0; // Maximum number of columns that can be processed
        //private int dwMaxRows = 0; // Maximum number of rows that can be processed

        private float[][]? fBuf = null;

        /// <summary> String representation of class</summary>
        /// <returns> suitable representation for class 
        /// </returns>
        public override string ToString()
        {
            int i, j;

            var rep = new System.Text.StringBuilder("[MatrixBasedTransformTosRGB: ");

            var body = new System.Text.StringBuilder("  ");
            body.Append(Environment.NewLine).Append("ksRGBExponent= ").Append(Convert.ToString(ksRGBExponent, CultureInfo.InvariantCulture));
            body.Append(Environment.NewLine).Append("ksRGBScaleAfterExp= ").Append(Convert.ToString(ksRGBScaleAfterExp, CultureInfo.InvariantCulture));
            body.Append(Environment.NewLine).Append("ksRGBReduceAfterExp= ").Append(Convert.ToString(ksRGBReduceAfterExp, CultureInfo.InvariantCulture));


            body.Append(Environment.NewLine).Append("dwMaxValues= ").Append(Convert.ToString(dwMaxValue[0])).Append(", ").Append(Convert.ToString(dwMaxValue[1])).Append(", ").Append(Convert.ToString(dwMaxValue[2]));

            body.Append(Environment.NewLine).Append("dwShiftValues= ").Append(Convert.ToString(dwShiftValue[0])).Append(", ").Append(Convert.ToString(dwShiftValue[1])).Append(", ").Append(Convert.ToString(dwShiftValue[2]));

            body.Append(Environment.NewLine).Append(Environment.NewLine).Append("fLut= ").Append(Environment.NewLine).Append(ColorSpace.indent("  ",
                $"fLut[RED]=  {fLut[0]}")).Append(Environment.NewLine).Append(ColorSpace.indent("  ", $"fLut[GRN]=  {fLut[1]}")).Append(Environment.NewLine).Append(ColorSpace.indent("  ",
                $"fLut[BLU]=  {fLut[2]}"));

            // Print the matrix
            body.Append(Environment.NewLine).Append(Environment.NewLine).Append("[matrix ");
            for (i = 0; i < 3; ++i)
            {
                body.Append(Environment.NewLine).Append("  ");
                for (j = 0; j < 3; ++j)
                {
                    body.Append($"{matrix[3 * i + j]}   ");
                }
            }
            body.Append("]");


            // Print the LinearSRGBtoSRGB lut.
            body.Append(Environment.NewLine).Append(Environment.NewLine).Append(lut);

            rep.Append(ColorSpace.indent("  ", body)).Append("]");
            return rep.Append("]").ToString();
        }


        /// <summary> Construct a 3 component transform based on an input RestricedICCProfile
        /// This transform will pass the input throught a floating point lut (LookUpTableFP),
        /// apply a matrix to the output and finally pass the intermediate buffer through
        /// a 8-bit lut (LookUpTable8).  This operation will be designated (LFP*M*L8) * Data
        /// The operators (LFP*M*L8) are constructed here.  Although the data for
        /// only one component is returned, the transformation must be done for all
        /// components, because the matrix application involves a linear combination of
        /// component input to produce the output.
        /// </summary>
        /// <param name="ricc">input profile
        /// </param>
        /// <param name="dwMaxValue">clipping value for output.
        /// </param>
        /// <param name="dwMaxCols">number of columns to transform
        /// </param>
        /// <param name="dwMaxRows">number of rows to transform
        /// </param>
        public MatrixBasedTransformTosRGB(RestrictedICCProfile ricc, int[] dwMaxValue, int[] dwShiftValue)
        {

            // Assure the proper type profile for this xform.
            if (ricc.Type != RestrictedICCProfile.kThreeCompInput)
                throw new ArgumentException("MatrixBasedTransformTosRGB: wrong type ICCProfile supplied");

            int c; // component index.
            this.dwMaxValue = dwMaxValue;
            this.dwShiftValue = dwShiftValue;

            // Create the LUTFP from the input profile.
            for (c = 0; c < 3; ++c)
            {
                fLut[c] = LookUpTableFP.createInstance(ricc.trc[c], dwMaxValue[c] + 1);
            }

            // Create the Input linear to PCS matrix
            matrix = createMatrix(ricc, dwMaxValue); // Create and matrix from the ICC profile.

            // Create the final LUT32
            lut = LookUpTable32LinearSRGBtoSRGB.createInstance(dwMaxValue[0], dwMaxValue[0], ksRGBShadowCutoff, ksRGBShadowSlope, ksRGBScaleAfterExp, ksRGBExponent, ksRGBReduceAfterExp);
        }


        private double[] createMatrix(RestrictedICCProfile ricc, int[] maxValues)
        {

            // Coefficients from the input linear to PCS matrix
            var dfPCS00 = ICCXYZType.XYZToDouble(ricc.colorant[RED].x);
            var dfPCS01 = ICCXYZType.XYZToDouble(ricc.colorant[GREEN].x);
            var dfPCS02 = ICCXYZType.XYZToDouble(ricc.colorant[BLUE].x);
            var dfPCS10 = ICCXYZType.XYZToDouble(ricc.colorant[RED].y);
            var dfPCS11 = ICCXYZType.XYZToDouble(ricc.colorant[GREEN].y);
            var dfPCS12 = ICCXYZType.XYZToDouble(ricc.colorant[BLUE].y);
            var dfPCS20 = ICCXYZType.XYZToDouble(ricc.colorant[RED].z);
            var dfPCS21 = ICCXYZType.XYZToDouble(ricc.colorant[GREEN].z);
            var dfPCS22 = ICCXYZType.XYZToDouble(ricc.colorant[BLUE].z);

            var matrix = new double[9];
            matrix[M00] = maxValues[0] * (SRGB00 * dfPCS00 + SRGB01 * dfPCS10 + SRGB02 * dfPCS20);
            matrix[M01] = maxValues[0] * (SRGB00 * dfPCS01 + SRGB01 * dfPCS11 + SRGB02 * dfPCS21);
            matrix[M02] = maxValues[0] * (SRGB00 * dfPCS02 + SRGB01 * dfPCS12 + SRGB02 * dfPCS22);
            matrix[M10] = maxValues[1] * (SRGB10 * dfPCS00 + SRGB11 * dfPCS10 + SRGB12 * dfPCS20);
            matrix[M11] = maxValues[1] * (SRGB10 * dfPCS01 + SRGB11 * dfPCS11 + SRGB12 * dfPCS21);
            matrix[M12] = maxValues[1] * (SRGB10 * dfPCS02 + SRGB11 * dfPCS12 + SRGB12 * dfPCS22);
            matrix[M20] = maxValues[2] * (SRGB20 * dfPCS00 + SRGB21 * dfPCS10 + SRGB22 * dfPCS20);
            matrix[M21] = maxValues[2] * (SRGB20 * dfPCS01 + SRGB21 * dfPCS11 + SRGB22 * dfPCS21);
            matrix[M22] = maxValues[2] * (SRGB20 * dfPCS02 + SRGB21 * dfPCS12 + SRGB22 * dfPCS22);

            return matrix;
        }


        /// <summary> Performs the transform.  Pass the input throught the LookUpTableFP, apply the
        /// matrix to the output and finally pass the intermediate buffer through the
        /// LookUpTable8.  This operation is designated (LFP*M*L8) * Data are already 
        /// constructed.  Although the data for only one component is returned, the
        /// transformation must be done for all components, because the matrix application
        /// involves a linear combination of component input to produce the output.
        /// </summary>
        /// <param name="ncols">number of columns in the input
        /// </param>
        /// <param name="nrows">number of rows in the input
        /// </param>
        /// <param name="inb">input data block
        /// </param>
        /// <param name="outb">output data block
        /// </param>
        /// <exception cref="MatrixBasedTransformException">
        /// </exception>
        public virtual void apply(image_DataBlkInt[] inb, image_DataBlkInt[] outb)
        {
            int[][] inputData = new int[3][], output = new int[3][]; // data references.

            int nrows = inb[0].h, ncols = inb[0].w;

            if ((fBuf == null) || (fBuf[0].Length < ncols * nrows))
            {
                var tmpArray = new float[3][];
                for (var i = 0; i < 3; i++)
                {
                    tmpArray[i] = new float[ncols * nrows];
                }
                fBuf = tmpArray;
            }

            // for each component (rgb)
            for (var c = 0; c < 3; ++c)
            {

                // Reference the input and output samples.
                inputData[c] = (int[])inb[c].Data;
                output[c] = (int[])outb[c].Data;

                // Assure a properly sized output buffer.
                if (output[c] == null || output[c].Length < inputData[c].Length)
                {
                    output[c] = new int[inputData[c].Length];
                    outb[c].Data = output[c];
                }

                // The first thing to do is to process the input into a standard form
                // and through the first input LUT, producing floating point output values
                standardizeMatrixLineThroughLut(inb[c], fBuf[c], dwMaxValue[c], fLut[c]);
            }

            // For each row and column
            var ra = fBuf[RED];
            var ga = fBuf[GREEN];
            var ba = fBuf[BLUE];

            var ro = output[RED];
            var go = output[GREEN];
            var bo = output[BLUE];
            var lut32 = lut.lut;

            double r, g, b;
            int val, index = 0;
            for (var y = 0; y < inb[0].h; ++y)
            {
                var end = index + inb[0].w;
                while (index < end)
                {
                    // Calculate the rgb pixel indices for this row / column
                    r = ra[index];
                    g = ga[index];
                    b = ba[index];

                    // Apply the matrix to the intermediate floating point data in order to index the 
                    // final LUT.
                    val = (int)(matrix[M00] * r + matrix[M01] * g + matrix[M02] * b + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        ro[index] = lut32[0];
                    else if (val >= lut32.Length)
                        ro[index] = lut32[lut32.Length - 1];
                    else
                        ro[index] = lut32[val];

                    val = (int)(matrix[M10] * r + matrix[M11] * g + matrix[M12] * b + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        go[index] = lut32[0];
                    else if (val >= lut32.Length)
                        go[index] = lut32[lut32.Length - 1];
                    else
                        go[index] = lut32[val];

                    val = (int)(matrix[M20] * r + matrix[M21] * g + matrix[M22] * b + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        bo[index] = lut32[0];
                    else if (val >= lut32.Length)
                        bo[index] = lut32[lut32.Length - 1];
                    else
                        bo[index] = lut32[val];

                    index++;
                }
            }
        }

        /// <summary> Performs the transform.  Pass the input throught the LookUpTableFP, apply the
        /// matrix to the output and finally pass the intermediate buffer through the
        /// LookUpTable8.  This operation is designated (LFP*M*L8) * Data are already 
        /// constructed.  Although the data for only one component is returned, the
        /// transformation must be done for all components, because the matrix application
        /// involves a linear combination of component input to produce the output.
        /// </summary>
        /// <param name="ncols">number of columns in the input
        /// </param>
        /// <param name="nrows">number of rows in the input
        /// </param>
        /// <param name="inb">input data block
        /// </param>
        /// <param name="outb">output data block
        /// </param>
        /// <exception cref="MatrixBasedTransformException">
        /// </exception>
        public virtual void apply(image_DataBlkFloat[] inb, image_DataBlkFloat[] outb)
        {

            float[][] inputData = new float[3][], output = new float[3][]; // data references.

            int nrows = inb[0].h, ncols = inb[0].w;

            if ((fBuf == null) || (fBuf[0].Length < ncols * nrows))
            {
                var tmpArray = new float[3][];
                for (var i = 0; i < 3; i++)
                {
                    tmpArray[i] = new float[ncols * nrows];
                }
                fBuf = tmpArray;
            }

            // for each component (rgb)
            for (var c = 0; c < 3; ++c)
            {

                // Reference the input and output pixels.
                inputData[c] = (float[])inb[c].Data;
                output[c] = (float[])outb[c].Data;

                // Assure a properly sized output buffer.
                if (output[c] == null || output[c].Length < inputData[c].Length)
                {
                    output[c] = new float[inputData[c].Length];
                    outb[c].Data = output[c];
                }

                // The first thing to do is to process the input into a standard form
                // and through the first input LUT, producing floating point output values
                standardizeMatrixLineThroughLut(inb[c], fBuf[c], dwMaxValue[c], fLut[c]);
            }

            var lut32 = lut.lut;

            // For each row and column 
            int index = 0, val;
            for (var y = 0; y < inb[0].h; ++y)
            {
                var end = index + inb[0].w;
                while (index < end)
                {
                    // Calculate the rgb pixel indices for this row / column

                    // Apply the matrix to the intermediate floating point data inorder to index the 
                    // final LUT.
                    val = (int)(matrix[M00] * fBuf[RED][index] + matrix[M01] * fBuf[GREEN][index] + matrix[M02] * fBuf[BLUE][index] + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        output[0][index] = lut32[0];
                    else if (val >= lut32.Length)
                        output[0][index] = lut32[lut32.Length - 1];
                    else
                        output[0][index] = lut32[val];

                    val = (int)(matrix[M10] * fBuf[RED][index] + matrix[M11] * fBuf[GREEN][index] + matrix[M12] * fBuf[BLUE][index] + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        output[1][index] = lut32[0];
                    else if (val >= lut32.Length)
                        output[1][index] = lut32[lut32.Length - 1];
                    else
                        output[1][index] = lut32[val];

                    val = (int)(matrix[M20] * fBuf[RED][index] + matrix[M21] * fBuf[GREEN][index] + matrix[M22] * fBuf[BLUE][index] + 0.5);
                    // Clip the calculated value if necessary..
                    if (val < 0)
                        output[2][index] = lut32[0];
                    else if (val >= lut32.Length)
                        output[2][index] = lut32[lut32.Length - 1];
                    else
                        output[2][index] = lut32[val];

                    index++;
                }
            }
        }

        private static void standardizeMatrixLineThroughLut(image_DataBlkInt inb, float[] output, int dwInputMaxValue, LookUpTableFP lut)
        {
            int wTemp, j = 0;
            var inputData = (int[])inb.Data; // input pixel reference
            var lutFP = lut.lut;
            for (var y = inb.uly; y < inb.uly + inb.h; ++y)
            {
                for (var x = inb.ulx; x < inb.ulx + inb.w; ++x)
                {
                    var i = inb.offset + (y - inb.uly) * inb.scanw + (x - inb.ulx); // pixel index.
                    if (inputData[i] > dwInputMaxValue)
                        wTemp = dwInputMaxValue;
                    else if (inputData[i] < 0)
                        wTemp = 0;
                    else
                        wTemp = inputData[i];
                    output[j++] = lutFP[wTemp];
                }
            }
        }


        private static void standardizeMatrixLineThroughLut(image_DataBlkFloat inb, float[] output, float dwInputMaxValue, LookUpTableFP lut)
        {
            var j = 0;
            float wTemp;
            var inputData = (float[])inb.Data!; // input pixel reference
            var lutFP = lut.lut;

            for (var y = inb.uly; y < inb.uly + inb.h; ++y)
            {
                for (var x = inb.ulx; x < inb.ulx + inb.w; ++x)
                {
                    var i = inb.offset + (y - inb.uly) * inb.scanw + (x - inb.ulx); // pixel index.
                    if (inputData[i] > dwInputMaxValue)
                        wTemp = dwInputMaxValue;
                    else if (inputData[i] < 0)
                        wTemp = 0;
                    else
                        wTemp = inputData[i];
                    output[j++] = lutFP[(int)wTemp];
                }
            }
        }
        static MatrixBasedTransformTosRGB()
        {
            RED = ICCProfile.RED;
            GREEN = ICCProfile.GREEN;
            BLUE = ICCProfile.BLUE;
        }
    }
}