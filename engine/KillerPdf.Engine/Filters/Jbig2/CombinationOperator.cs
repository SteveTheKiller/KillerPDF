// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.

#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    /// <summary>
    /// This enumeration keeps the available logical operator defined in the JBIG2 ISO standard.
    /// </summary>
    internal enum CombinationOperator : byte
    {
        OR,
        AND,
        XOR,
        XNOR,
        REPLACE
    }

    internal static class CombinationOperators
    {
        public static CombinationOperator TranslateOperatorCodeToEnum(short combinationOperatorCode)
        {
            switch (combinationOperatorCode)
            {
                case 0:
                    return CombinationOperator.OR;

                case 1:
                    return CombinationOperator.AND;

                case 2:
                    return CombinationOperator.XOR;

                case 3:
                    return CombinationOperator.XNOR;

                default:
                    return CombinationOperator.REPLACE;
            }
        }
    }
}
