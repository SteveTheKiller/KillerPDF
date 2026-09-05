// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    ///  This class represents the arithmetic decoder, described in ISO/IEC 14492:2001 in E.3
    /// </summary>
    internal sealed class ArithmeticDecoder
    {
        private static readonly int[][] QE = new[]
        {
            new[] { 0x5601, 01, 01, 1 },
            new[] { 0x3401, 02, 06, 0 },
            new[] { 0x1801, 03, 09, 0 },
            new[] { 0x0AC1, 04, 12, 0 },
            new[] { 0x0521, 05, 29, 0 },
            new[] { 0x0221, 38, 33, 0 },
            new[] { 0x5601, 07, 06, 1 },
            new[] { 0x5401, 08, 14, 0 },
            new[] { 0x4801, 09, 14, 0 },
            new[] { 0x3801, 10, 14, 0 },
            new[] { 0x3001, 11, 17, 0 },
            new[] { 0x2401, 12, 18, 0 },
            new[] { 0x1C01, 13, 20, 0 },
            new[] { 0x1601, 29, 21, 0 },
            new[] { 0x5601, 15, 14, 1 },
            new[] { 0x5401, 16, 14, 0 },
            new[] { 0x5101, 17, 15, 0 },
            new[] { 0x4801, 18, 16, 0 },
            new[] { 0x3801, 19, 17, 0 },
            new[] { 0x3401, 20, 18, 0 },
            new[] { 0x3001, 21, 19, 0 },
            new[] { 0x2801, 22, 19, 0 },
            new[] { 0x2401, 23, 20, 0 },
            new[] { 0x2201, 24, 21, 0 },
            new[] { 0x1C01, 25, 22, 0 },
            new[] { 0x1801, 26, 23, 0 },
            new[] { 0x1601, 27, 24, 0 },
            new[] { 0x1401, 28, 25, 0 },
            new[] { 0x1201, 29, 26, 0 },
            new[] { 0x1101, 30, 27, 0 },
            new[] { 0x0AC1, 31, 28, 0 },
            new[] { 0x09C1, 32, 29, 0 },
            new[] { 0x08A1, 33, 30, 0 },
            new[] { 0x0521, 34, 31, 0 },
            new[] { 0x0441, 35, 32, 0 },
            new[] { 0x02A1, 36, 33, 0 },
            new[] { 0x0221, 37, 34, 0 },
            new[] { 0x0141, 38, 35, 0 },
            new[] { 0x0111, 39, 36, 0 },
            new[] { 0x0085, 40, 37, 0 },
            new[] { 0x0049, 41, 38, 0 },
            new[] { 0x0025, 42, 39, 0 },
            new[] { 0x0015, 43, 40, 0 },
            new[] { 0x0009, 44, 41, 0 },
            new[] { 0x0005, 45, 42, 0 },
            new[] { 0x0001, 45, 43, 0 },
            new[] { 0x5601, 46, 46, 0 }
        };

        private readonly IImageInputStream iis;

        private int b;

        private int ct;
        private long streamPos0;

        public int A { get; private set; }

        public long C { get; private set; }

        public ArithmeticDecoder(IImageInputStream iis)
        {
            this.iis = iis;
            Init();
        }

        private void Init()
        {
            streamPos0 = iis.Position;
            b = iis.Read();

            C = b << 16;

            ByteIn();

            C <<= 7;
            ct -= 7;
            A = 0x8000;
        }

        public int Decode(CX cx)
        {
            int d;
            int qeValue = QE[cx.Cx][0];
            int icx = cx.Cx;

            A -= qeValue;

            if (C >> 16 < qeValue)
            {
                d = LpsExchange(cx, icx, qeValue);
                ReNormalize();
            }
            else
            {
                C -= qeValue << 16;
                if ((A & 0x8000) == 0)
                {
                    d = MpsExchange(cx, icx);
                    ReNormalize();
                }
                else
                {
                    return cx.Mps;
                }
            }

            return d;
        }

        private void ByteIn()
        {
            if (iis.Position > streamPos0)
            {
                iis.Seek(iis.Position - 1);
            }

            b = iis.Read();

            if (b == 0xFF)
            {
                int b1 = iis.Read();
                if (b1 > 0x8f)
                {
                    C += 0xff00;
                    ct = 8;
                    iis.Seek(iis.Position - 2);
                }
                else
                {
                    C += b1 << 9;
                    ct = 7;
                }
            }
            else
            {
                b = iis.Read();
                C += b << 8;
                ct = 8;
            }

            C &= 0xffffffffL;
        }

        private void ReNormalize()
        {
            do
            {
                if (ct == 0)
                {
                    ByteIn();
                }

                A <<= 1;
                C <<= 1;
                ct--;
            } while ((A & 0x8000) == 0);

            C &= 0xffffffffL;
        }

        private int MpsExchange(CX cx, int icx)
        {
            int mps = cx.Mps;

            if (A < QE[icx][0])
            {
                if (QE[icx][3] == 1)
                {
                    cx.ToggleMps();
                }

                cx.Cx = QE[icx][2];
                return 1 - mps;
            }

            cx.Cx = QE[icx][1];
            return mps;
        }

        private int LpsExchange(CX cx, int icx, int qeValue)
        {
            int mps = cx.Mps;

            if (A < qeValue)
            {
                cx.Cx = QE[icx][1];
                A = qeValue;

                return mps;
            }

            if (QE[icx][3] == 1)
            {
                cx.ToggleMps();
            }

            cx.Cx = QE[icx][2];
            A = qeValue;
            return 1 - mps;
        }
    }
}
