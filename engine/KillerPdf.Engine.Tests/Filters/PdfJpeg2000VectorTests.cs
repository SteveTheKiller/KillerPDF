using System.Numerics;
using CoreJ2K.j2k.wavelet.synthesis;
using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpeg2000VectorTests
{
    [Theory]
    [InlineData(1979, true)]
    [InlineData(1979, false)]
    [InlineData(3209, true)]
    [InlineData(3209, false)]
    public void ColumnsMatchScalarFilterBitsAtEveryBoundary(int seed, bool even)
    {
        var random = new Random(seed);
        var filter = new SynWTFilterFloatLift9x7();
        int lanes = Vector<float>.Count;
        for (int length = 1; length <= 257; length++)
        {
            var input = new float[length * lanes];
            for (int i = 0; i < input.Length; i++)
                input[i] = (float)((random.NextDouble() - .5) * 16384);
            float[] original = (float[])input.Clone();
            var actual = new float[input.Length];
            PdfJpeg2000Decoder.SynthesizeFloatColumns(input, actual, length, even);
            Assert.Equal(original, input);
            int lowCount = (length + (even ? 1 : 0)) / 2;
            for (int lane = 0; lane < lanes; lane++)
            {
                var column = new float[length];
                for (int y = 0; y < length; y++) column[y] = input[y * lanes + lane];
                var expected = new float[length];
                if (even)
                    filter.synthetize_lpf(column, 0, lowCount, 1, column, lowCount, length - lowCount, 1, expected, 0, 1);
                else
                    filter.synthetize_hpf(column, 0, lowCount, 1, column, lowCount, length - lowCount, 1, expected, 0, 1);
                for (int y = 0; y < length; y++)
                    Assert.Equal(BitConverter.SingleToInt32Bits(expected[y]),
                        BitConverter.SingleToInt32Bits(actual[y * lanes + lane]));
            }
        }
    }
}
