// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This class represents the arithmetic integer decoder, described in ISO/IEC 14492:2001 (Annex A).
    /// </summary>
    internal sealed class ArithmeticIntegerDecoder
    {
        private readonly ArithmeticDecoder decoder;

        private int prev;

        public ArithmeticIntegerDecoder(ArithmeticDecoder decoder)
        {
            this.decoder = decoder;
        }

        /// <summary>
        /// Arithmetic Integer Decoding Procedure, Annex A.2.
        /// </summary>
        /// <param name="cxIAx">cxIAx to be decoded</param>
        /// <returns>Decoded value.</returns>
        public long Decode(CX cxIAx)
        {
            int v = 0;
            int d, s;

            int bitsToRead;
            int offset;

            if (cxIAx is null)
            {
                cxIAx = new CX(512, 1);
            }

            prev = 1;

            cxIAx.Index = prev;
            s = decoder.Decode(cxIAx);
            SetPrev(s);

            cxIAx.Index = prev;
            d = decoder.Decode(cxIAx);
            SetPrev(d);

            if (d == 1)
            {
                cxIAx.Index = prev;
                d = decoder.Decode(cxIAx);
                SetPrev(d);

                if (d == 1)
                {
                    cxIAx.Index = prev;
                    d = decoder.Decode(cxIAx);
                    SetPrev(d);

                    if (d == 1)
                    {
                        cxIAx.Index = prev;
                        d = decoder.Decode(cxIAx);
                        SetPrev(d);

                        if (d == 1)
                        {
                            cxIAx.Index = prev;
                            d = decoder.Decode(cxIAx);
                            SetPrev(d);

                            if (d == 1)
                            {
                                bitsToRead = 32;
                                offset = 4436;
                            }
                            else
                            {
                                bitsToRead = 12;
                                offset = 340;
                            }
                        }
                        else
                        {
                            bitsToRead = 8;
                            offset = 84;
                        }
                    }
                    else
                    {
                        bitsToRead = 6;
                        offset = 20;
                    }
                }
                else
                {
                    bitsToRead = 4;
                    offset = 4;
                }
            }
            else
            {
                bitsToRead = 2;
                offset = 0;
            }

            for (int i = 0; i < bitsToRead; i++)
            {
                cxIAx.Index = prev;
                d = decoder.Decode(cxIAx);
                SetPrev(d);
                v = v << 1 | d;
            }

            v += offset;

            if (s == 0)
            {
                return v;
            }

            if (s == 1 && v > 0)
            {
                return -v;
            }

            return long.MaxValue;
        }

        /// <summary>
        /// The IAID decoding procedure, Annex A.3.
        /// </summary>
        /// <param name="cxIAID">The contexts and statistics for decoding procedure.</param>
        /// <param name="symCodeLen">Symbol code length</param>
        /// <returns>The decoded value</returns>
        public int DecodeIAID(CX cxIAID, long symCodeLen)
        {
            // A.3 1)
            prev = 1;

            // A.3 2)
            for (int i = 0; i < symCodeLen; i++)
            {
                cxIAID.Index = prev;
                prev = prev << 1 | decoder.Decode(cxIAID);
            }

            // A.3 3) & 4)
            return prev - (1 << (int)symCodeLen);
        }

        private void SetPrev(int bit)
        {
            if (prev < 256)
            {
                prev = (prev << 1 | bit) & 0x1ff;
            }
            else
            {
                prev = ((prev << 1 | bit) & 511 | 256) & 0x1ff;
            }
        }
    }
}
