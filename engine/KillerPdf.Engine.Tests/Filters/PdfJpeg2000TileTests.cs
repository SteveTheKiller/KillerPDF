using System.Buffers.Binary;
using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;
using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpeg2000TileTests
{
    [Theory]
    [InlineData(513, 515, 1)]
    [InlineData(1025, 513, 3)]
    public void ParallelIrreversibleRowsMatchSerialSamples(int width, int height, int components)
    {
        int[][] samples = Enumerable.Range(0, components).Select(component =>
            Enumerable.Range(0, width * height).Select(index =>
                ((index % width * 17 + index / width * 29 + component * 53) & 255) - 128).ToArray()).ToArray();
        var source = new InterleavedImageSource(width, height, components, 8, new bool[components], samples);
        var parameters = new J2KEncoderConfiguration().ToParameterList();
        parameters["Wlev"] = "3";
        parameters["Ffilters"] = "w9x7";
        parameters["Qtype"] = "expounded";
        byte[] encoded = J2kImage.ToBytes(source, parameters);
        foreach (int level in new[] { -1, 2 })
        {
            Jpeg2000DecodedImage serial = PdfJpeg2000Decoder.DecodeImage(encoded, 4_000_000, level);
            for (int repeat = 0; repeat < 3; repeat++)
            {
                Jpeg2000DecodedImage parallel = PdfJpeg2000Decoder.DecodeImage(encoded, 4_000_000, level, 4);
                Assert.Equal(serial.Width, parallel.Width);
                Assert.Equal(serial.Height, parallel.Height);
                Assert.Equal(serial.Samples, parallel.Samples);
            }
        }
    }

    [Theory]
    [InlineData(33, 65, 1, 8)]
    [InlineData(65, 33, 1, 8)]
    [InlineData(129, 257, 1, 8)]
    [InlineData(257, 129, 1, 8)]
    [InlineData(33, 65, 3, 8)]
    [InlineData(65, 33, 3, 8)]
    [InlineData(129, 257, 3, 8)]
    [InlineData(257, 129, 3, 8)]
    [InlineData(33, 65, 1, 16)]
    [InlineData(65, 33, 3, 16)]
    [InlineData(129, 257, 3, 16)]
    public void LosslessRectangularTilesPreserveVaryingRowsAndColumns(int width, int height, int components, int bits)
    {
        int bias = 1 << (bits - 1);
        int maximum = (1 << bits) - 1;
        int[][] samples = Enumerable.Range(0, components).Select(component =>
            Enumerable.Range(0, width * height).Select(index =>
                ((index % width * 17 + index / width * 29 + component * 53) & maximum) - bias).ToArray()).ToArray();
        var source = new InterleavedImageSource(width, height, components, bits, new bool[components], samples);
        var parameters = new J2KEncoderConfiguration().WithLossless()
            .WithTiles(tiles => tiles.SetSize(64, 128)).ToParameterList();
        parameters["Wlev"] = "3";
        byte[] encoded = J2kImage.ToBytes(source, parameters);

        for (int repeat = 0; repeat < 2; repeat++)
        {
            Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(encoded, 1_000_000, -1);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(width * height * components * (bits / 8), decoded.Samples.Length);
            for (int pixel = 0; pixel < width * height; pixel++)
            for (int component = 0; component < components; component++)
            {
                int sample = pixel * components + component;
                int actual = bits == 8 ? decoded.Samples[sample]
                    : BinaryPrimitives.ReadUInt16BigEndian(decoded.Samples.AsSpan(sample * 2));
                Assert.Equal(samples[component][pixel] + bias, actual);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LossyReducedTilesMatchOpenJpegAndRepeatExactly(bool shortEdge)
    {
        // OpenJPEG 2.5.4 encoded RGB gradients with 32x32 tiles and a 9/7 transform.
        // The 35x37 case has edge tiles smaller than the decomposition scale.
        // References contain independently decoded pixels at half resolution.
        byte[] encoded = Convert.FromBase64String(
            shortEdge ? "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAJQAAACMAAwcHAAAAAAAPY29scgEAAAAAABAAAAJranAyY/9P/1EALwAAAAAAIwAAACUAAAAAAAAAAAAAACAAAAAgAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAMEBAAA/1wAF0JnOGdQZ1BnaFAFUAVQR1fTV9NXYv9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAAQgAAf+Tx+ouEU8Cb5tGnAFui2YAaCinaWREPHdHT6XH6i4Q3CICFcJxyLUcTzkeB+e2yg2m9xWco8fqMBFP3guTJtP16vPslUEFATtqZ47akD3oP8B9IqAfMHAiGWNpCijGdcnvA34mjcXen8A+YPAfSJAiGCM6ljd/A34mjcXekwdRwH0ioA+MOCIYIzqWNyEOVAMDfiaNxd5/wHxh4B8I4DahlyiZYV89pi6IZtl/wD4RoD4xgDahgc9dYD2mLohm2WTaoAAAP8B8YuAODDahgc9dYRXpAUgRPaYuwBwkAIxfp8eHnawpwAjgDoxfp7+drClZeD/AHRRfp8ciw/+QAAoAAQAAADUAAf+TwfGFEYMzxkjD5goRgy8whMHgwAk2jMA4IAQSwHBABBKAgICAgICA/5AACgACAAAAeAAB/5PD8gYIGp201ojD7AYM9jp1p8HH6gwIaSxsUN/AfSEgHxgwD42AKQKCNcA+YHAfGDAPNT0CgjXAfSEgD4wQDzU9iAKDwHxhACaLDenAIgAmgsB8IoAmgjGYTsAcEGiZoASAAID/f8ARAGiP/5AACgADAAAAKwAB/5PD8gICucfqBAQQw/ICBC+AwH0ggAMfgICAgICAgP/Z" : "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAMAAAADAAAwcHAAAAAAAPY29scgEAAAAAABAAAAP2anAyY/9P/1EALwAAAAAAMAAAADAAAAAAAAAAAAAAACAAAAAgAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAMEBAAA/1wAF0JnOGdQZ1BnaFAFUAVQR1fTV9NXYv9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAAQgAAf+Tx+ouEU8Cb5tGnAFui2YAaCinaWREPHdHT6XH6i4Q3CICFcJxyLUcTzkeB+e2yg2m9xWco8fqMBFP3guTJtP16vPslUEFATtqZ47akD3oP8B9IqAfMHAiGWNpCijGdcnvA34mjcXen8A+YPAfSJAiGCM6ljd/A34mjcXekwdRwH0ioA+MOCIYIzqWNyEOVAMDfiaNxd5/wHxh4B8I4DahlyiZYV89pi6IZtl/wD4RoD4xgDahgc9dYD2mLohm2WTaoAAAP8B8YuAODDahgc9dYRXpAUgRPaYuwBwkAIxfp8eHnawpwAjgDoxfp7+drClZeD/AHRRfp8ciw/+QAAoAAQAAAMcAAf+Tw/IMGf2dZBr5/ZuZmkdfw/IMEYOs09zvJaHB5jkHw/ILF9RaTT5MrkyafpfAfSEgHzBQF9aZzwNrHGLGwH0g4D6Q4BfX7wNrHGLGckfA+wEgD4woF9fviwNrHGLFwHxhoB8IwCIc6VJigykgnkrQf8A+EaA+MQAiHL798n8pIJ5K0NZ4k8B8YqAODCIcvv3ydlUwhhspIJ/AHCQAjDaheF+EIB/ACOAOijahf4QgJmq5wB0UNqF5Sfv/kAAKAAIAAADDAAH/k8PyDAi00lIY24bU101nt8fqFhPFsuq/xKXp8E83x+oYCLY4ZMwWclzhm7mPwH0h4D6QYBWBHcHaPEMLQXfAPmCwPsBAFXjpUXkLQXdzwH0h4B8wMBV46VF5anMLQXfAfGGgHwigNp2mRV1/A4SP9M3APhFgPjFANpxPAVsDhI/0zgFypnfAwHxiIA4MNpxPAV0jhB8DhI/AHBwAjF+nfWauP8AIoA6KX6ZmrkTNv8AdEF+ncHf/kAAKAAMAAADZAAH/k8fqDA7eoFNTDMfeDAHQfhPqD8fqDAzXsx7b28B9IOA+kGAM+xcLPn/B9YLH0hYHxhAQkWg6ERCCSUw3CwXzT8D7AOAfMDANA1kLPn/ARwDgwCIZYwN+JsPlDz7AyHzCwCMeKYlSySYanSJ1Eqqa2mf+kcIaQZJDRyBSqShKesBwYCIYI4DD5icfMUh8xUBCy2VVOn0/difDUB4aMX+R0Xd/Q0lk/gudog51JT5JdwMJeZxfLBw9fnV5TxGCaKAVMEoJLB/e/wd7JgmA/9k=");
        byte[] compressed = Convert.FromBase64String(
            shortEdge ? "H4sIAAAAAAACCg3L227bBAAAUKmomjrIWLq2pCXt0syNkuBFbuQFN3Ijk3nGs4zlZVbkBiszlok8zwpuZKVe8IKVesENXue16ZatGZRR2qIVMTTGRVyENCSkvfFHWDqvZ2Rk5MToWGgsfCYUmQ7Hzk6mgOnFd2aXFuexdxfI5RR78Tx/eVFkLlznlvQPl2/KxO3A6NhrJ0MnT4fHpyZmopF4PJpOxrIZIA8ni3mQwqASAVdoRCqhKo81qpdaNaobGAuPnpp4YyISnGgsBiQAEEzCWRBFILwA0zjCUajAYnIZ1wTSkGhLvfJ5IDR9Yjx6KhKbmgPmgGQiDWYgKJeDCyhCFFGGxMoMXuXIWoWui2yzxrU/WdkIhOfHpoDT0WQkDsaSUDIDQzCC5FEMw0gCZ2mSL9EizypVTpd5UxVsQ/oiMJl6fQYcj0EzCTgOIuksmkWwfAEv4iRF0SWWrZQ5SeBVSWgoYqsud9bUO4HpxdAcPAEg0TQKQBiYw2GURIs0TrI0w3EcL1QEWRS1mmxoitXQnE9X7wZml96Mo1NJbC6DJ2Ayk6dzGFsgOILmmZJQ5sVqVa7JSl3Vmrrebhrdz8x7gfn3wgk8ApKxLJ1EWKjAITiPUQLJimxZ5gVFlDRF0fW6YRqm3bLcdXs7sHD5TJqegdh4jkujfLYo5EmxyMgUp5QqWkXUpZqhamajYbVMu9N2em5vJ5C6MpnhojAP5AUQE2FCRmkFL2k0r3NVQ5BNWbU03TaajmW5TsfzvP4gcH7lrawwh4iJgpzBlRylFVidKBuMYJYlq6rYtbpTN9xmy2vbfrfb3/R3HwUWP5rOyTFUSRY1iNQRxsA4k6xYrGjzNUfUXKXh6aZvtvu2M3B7Q3/7yW7gwvW389o5TE8TRpY28yWryNtU1SnJbkX1JN1Xm/2GNWh1hh13r7e5vzV4Ogws6bMFA8BNkLJg1kbLDi64tORxii/U+7Ix0FpDw96zuvuOd+RtHfeHz4eB5ZtnL1oJ0s4wTo5zCxWPEH2m1i9rg2pjWDP36u39pnPU7h13/WebD17cf/xiGCh24u/fTn3Qg67eQVbuYdd2SPkhe+Mxv/q1uPatcus7ff17c+NHe/Nnt/+bv/vnw8OXB4FLG+coL8362XI/LwyK0pBS9kr1/YpxJLWOVftZo/tTy/u1s/VHb/D31pf/PDp8dRAg/QVmG7z6AF7ZRa99hX/8DX3jkFt9Kqz9IN96rq3/Ymz8bt39y9l56e3+23/yanj430HgfwKzCtICBAAA" : "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");
        using var input = new MemoryStream(compressed);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        byte[] expected = output.ToArray();
        byte[]? previous = null;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(encoded, 10_000, 2);
            Assert.Equal(shortEdge ? 18 : 24, decoded.Width);
            Assert.Equal(shortEdge ? 19 : 24, decoded.Height);
            Assert.Equal(expected.Length, decoded.Samples.Length);
            for (int sample = 0; sample < expected.Length; sample++)
                Assert.InRange((int)decoded.Samples[sample], expected[sample] - 1, expected[sample] + 1);
            if (previous is not null) Assert.Equal(previous, decoded.Samples);
            previous = decoded.Samples;
        }
    }
    [Theory]
    [InlineData(8, 1)]
    [InlineData(8, 3)]
    public void ReducedResolutionPreservesEveryTileIncludingPartialEdges(int bits, int components)
    {
        const int width = 70, height = 99, levels = 3;
        int[][] samples = Enumerable.Range(0, components).Select(component =>
            Enumerable.Range(0, width * height).Select(index =>
                Value(index % width, index / width, component) - (1 << (bits - 1))).ToArray()).ToArray();
        var source = new InterleavedImageSource(width, height, components, bits, new bool[components], samples);
        var parameters = new J2KEncoderConfiguration().WithLossless()
            .WithTiles(tiles => tiles.SetSize(32, 32)).ToParameterList();
        parameters["Wlev"] = levels.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] encoded = J2kImage.ToBytes(source, parameters);

        for (int level = 0; level <= levels; level++)
        {
            Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(encoded, 1_000_000, level);
            int reduction = 1 << (levels - level);
            Assert.Equal((width + reduction - 1) / reduction, decoded.Width);
            Assert.Equal((height + reduction - 1) / reduction, decoded.Height);
            for (int y = 0; y < decoded.Height; y++)
            for (int x = 0; x < decoded.Width; x++)
            for (int component = 0; component < components; component++)
            {
                int sample = (y * decoded.Width + x) * components + component;
                int actual = bits == 8 ? decoded.Samples[sample]
                    : BinaryPrimitives.ReadUInt16BigEndian(decoded.Samples.AsSpan(sample * 2));
                Assert.Equal(Value(x * reduction, y * reduction, component), actual);
            }
        }

        int Value(int x, int y, int component) =>
            (x / 32 + y / 32 * 3) * (bits == 8 ? 15 : 1000) + 20 + component * 5;
    }

    [Fact]
    public void ReducedSixteenBitTilesMatchIndependentDecoderSamples()
    {
        // OpenJPEG 2.5.4: unsigned 16-bit gray, value 1000 throughout, 32x32 tiles,
        // reversible transform. Independently decoded at all four resolutions.
        byte[] encoded = Convert.FromBase64String(
            "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAaAAAAFAAAQ8HAAAAAAAPY29scgEAAAAAABEAAAIkanAyY/9P/1EAKQAAAAAAUAAAAGgAAAAAAAAAAAAAACAAAAAgAAAAAAAAAAAAAQ8BAf9SAAwAAAABAAMEBAAB/1wADUCAiIiQiIiQiIiQ/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAKAAB/5PP/DBMEVBUoOKgAAMJCUgTAGEeYpxIP4CAgP+QAAoAAQAAACgAAf+Tz/wwTBFQVKDioAADCQlIEwBhHmKcSD+AgID/kAAKAAIAAAAiAAH/k8/8MDQRT7ci0AGEgSJfBTWjgICA/5AACgADAAAAKAAB/5PP/DBMEVBUoOKgAAMJCUgTAGEeYpxIP4CAgP+QAAoABAAAACgAAf+Tz/wwTBFQVKDioAADCQlIEwBhHmKcSD+AgID/kAAKAAUAAAAiAAH/k8/8MDQRT7ci0AGEgSJfBTWjgICA/5AACgAGAAAAKAAB/5PP/DBMEVBUoOKgAAMJCUgTAGEeYpxIP4CAgP+QAAoABwAAACgAAf+Tz/wwTBFQVKDioAADCQlIEwBhHmKcSD+AgID/kAAKAAgAAAAiAAH/k8/8MDQRT7ci0AGEgSJfBTWjgICA/5AACgAJAAAAHQAB/5PP/DAgCRL00hC0lw+AgID/kAAKAAoAAAAdAAH/k8/8MCAJEvTSELSXD4CAgP+QAAoACwAAABkAAf+Tz/wwEAhdbMKAgID/2Q==");
        for (int level = 0; level <= 3; level++)
        {
            Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(encoded, 100_000, level);
            Assert.Equal(80 >> (3 - level), decoded.Width);
            Assert.Equal(104 >> (3 - level), decoded.Height);
            Assert.Equal(16, decoded.Bits);
            for (int offset = 0; offset < decoded.Samples.Length; offset += 2)
                Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(decoded.Samples.AsSpan(offset)));
        }
    }
}
