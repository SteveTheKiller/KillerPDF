using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpegConstantBlockTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    public void Decode_PreservesConstantBlockLevelsAtEveryReduction(int reduction, bool progressive)
    {
        // Independently encoded and decoded four-quadrant grayscale references.
        byte[] jpeg = Convert.FromBase64String(progressive
            ? "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wgALCAAQABABAREA/8QAFgABAQEAAAAAAAAAAAAAAAAACAkK/9oACAEBAAAAAYfsDQAwP//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAQUCH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEABj8CH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8hH//aAAgBAQAAABAP/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQABPxAf/9k="
            : "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAQABABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP4f6/YCv9ACv2Ar/9k=");
        var decoded = PdfJpegDecoder.DecodeImage(jpeg, 256, reduction);

        int size = 16 / reduction;
        Assert.Equal(size, decoded.Width);
        Assert.Equal(size, decoded.Height);
        Assert.Equal(size * size, decoded.Samples.Length);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte expected = y < size / 2 ? (byte)(x < size / 2 ? 16 : 64)
                    : (byte)(x < size / 2 ? 192 : 240);
                Assert.Equal(expected, decoded.Samples[y * size + x]);
            }
    }
}
