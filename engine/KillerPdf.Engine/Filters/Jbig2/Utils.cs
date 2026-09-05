// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;

    internal static class Utils
    {
        public static int HighestOneBit(this int number)
        {
            if (number == 0)
            {
                return 1;
            }

#if NET
            return (int)Math.Pow(2, Math.Floor(Math.Log2(number)));
#else
            return (int)Math.Pow(2, Convert.ToString(number, 2).Length - 1);
#endif
        }

        public static int GetMinY(this Jbig2Rectangle r)
        {
            return r.Y;
        }

        public static int GetMaxY(this Jbig2Rectangle r)
        {
            return r.Y + r.Height;
        }

        public static int GetMaxX(this Jbig2Rectangle r)
        {
            return r.X + r.Width;
        }

        public static int GetMinX(this Jbig2Rectangle r)
        {
            return r.X;
        }
    }
}
