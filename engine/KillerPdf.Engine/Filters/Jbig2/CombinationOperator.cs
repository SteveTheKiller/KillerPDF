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
            return combinationOperatorCode switch
            {
                0 => CombinationOperator.OR,
                1 => CombinationOperator.AND,
                2 => CombinationOperator.XOR,
                3 => CombinationOperator.XNOR,
                _ => CombinationOperator.REPLACE,
            };
        }
    }
}
