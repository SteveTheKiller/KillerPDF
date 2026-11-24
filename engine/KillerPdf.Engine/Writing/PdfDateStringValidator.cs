using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

internal static class PdfDateStringValidator
{
    // A date is a text string (ISO 32000-2 7.9.4), so Microsoft Print To PDF's UTF-16BE dates are dates too.
    public static bool IsValid(PdfString date)
    {
        string text;
        try
        {
            text = PdfUnicodeEncoding.DecodeTextString(date.Bytes.Span, "A PDF date");
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (text.Length < 6 || text[0] != 'D' || text[1] != ':') return false;
        int index = 2;
        if (!ReadDigits(text, ref index, 4, out int year) || year is < 1 or > 9999)
            return false;
        int[] components = new int[5];
        int componentCount = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            if (componentCount == components.Length
                || !ReadDigits(text, ref index, 2, out components[componentCount]))
                return false;
            componentCount++;
        }
        int month = componentCount > 0 ? components[0] : 1;
        int day = componentCount > 1 ? components[1] : 1;
        if (month is < 1 or > 12 || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || componentCount > 2 && components[2] is < 0 or > 23
            || componentCount > 3 && components[3] is < 0 or > 59
            || componentCount > 4 && components[4] is < 0 or > 59)
            return false;
        if (index == text.Length) return true;
        if (componentCount != components.Length) return false;
        // ISO 32000-1 7.9.4 lets Z carry an HH'mm offset too, as Adobe signers write Z00'00'.
        if (text[index] == 'Z' && index + 1 == text.Length) return true;
        if (text[index] is not ('+' or '-' or 'Z')) return false;
        index++;
        if (!ReadDigits(text, ref index, 2, out int offsetHour)
            || offsetHour > 23) return false;
        // ISO 32000-1 7.9.4: the apostrophe after HH is only present with HH, and mm only
        // after that apostrophe, so +01 and +01' are dates too, as PdfDateText already reads them.
        if (index == text.Length) return true;
        if (text[index] != '\'') return false;
        index++;
        if (index == text.Length) return true;
        if (!ReadDigits(text, ref index, 2, out int offsetMinute)
            || offsetMinute > 59) return false;
        if (index < text.Length && text[index] == '\'') index++;
        return index == text.Length;
    }

    private static bool ReadDigits(
        string text, ref int index, int count, out int value)
    {
        value = 0;
        if (index + count > text.Length) return false;
        for (int offset = 0; offset < count; offset++)
        {
            char character = text[index + offset];
            if (!char.IsAsciiDigit(character)) return false;
            value = checked(value * 10 + character - '0');
        }
        index += count;
        return true;
    }
}
