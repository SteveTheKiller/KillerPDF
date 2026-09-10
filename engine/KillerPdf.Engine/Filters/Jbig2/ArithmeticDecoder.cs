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
        private static readonly ushort[] QeValues =
        {
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401,
            0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401,
            0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
            0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1,
            0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
            0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601
        };
        private static readonly byte[] NextMps =
        {
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
        };
        private static readonly byte[] NextLps =
        {
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14,
            15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
        };
        private static readonly bool[] SwitchMps =
        {
            true, false, false, false, false, false, true, false, false, false, false, false,
            false, false, true, false, false, false, false, false, false, false, false, false,
            false, false, false, false, false, false, false, false, false, false, false, false,
            false, false, false, false, false, false, false, false, false, false, false
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
            int qeValue = QeValues[cx.Cx];
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

            if (A < QeValues[icx])
            {
                if (SwitchMps[icx])
                {
                    cx.ToggleMps();
                }

                cx.Cx = NextLps[icx];
                return 1 - mps;
            }

            cx.Cx = NextMps[icx];
            return mps;
        }

        private int LpsExchange(CX cx, int icx, int qeValue)
        {
            int mps = cx.Mps;

            if (A < qeValue)
            {
                cx.Cx = NextMps[icx];
                A = qeValue;

                return mps;
            }

            if (SwitchMps[icx])
            {
                cx.ToggleMps();
            }

            cx.Cx = NextLps[icx];
            A = qeValue;
            return 1 - mps;
        }
    }
}
