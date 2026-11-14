using System.Text;

namespace KillerPdf.Engine.Tests.Documents;

/// <summary>ASCII85 and ASCIIHex encoders for test fixtures, written the way producers such as ReportLab write them.</summary>
internal static class AsciiText
{
    internal static byte[] Ascii85(byte[] data)
    {
        var text = new StringBuilder("<~".Length + data.Length * 5 / 4 + 8);
        for (int offset = 0; offset < data.Length; offset += 4)
        {
            int count = Math.Min(4, data.Length - offset);
            uint value = 0;
            for (int i = 0; i < 4; i++) value = (value << 8) | (i < count ? data[offset + i] : 0u);
            if (value == 0 && count == 4) { text.Append('z'); continue; }
            var digits = new char[5];
            for (int i = 4; i >= 0; i--) { digits[i] = (char)('!' + value % 85); value /= 85; }
            text.Append(digits, 0, count + 1);
            if (offset % 64 == 60) text.Append('\n');
        }
        return Encoding.ASCII.GetBytes(text.Append("~>").ToString());
    }

    internal static byte[] Hex(byte[] data) => Encoding.ASCII.GetBytes(Convert.ToHexString(data) + ">");
}
