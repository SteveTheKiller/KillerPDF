using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44InteriorTests
{
    [Fact]
    public void ReconstructsInteriorPixelsOfOwnedThirtyTwoSquareImage()
    {
        // Owned patterned PGM encoded once with public c44, one 100-slice chunk.
        // Frozen public DjVu.js grayscale samples provide a qualified interoperability
        // oracle. Linear frame expectations apply the declared INFO gamma of 2.2.
        var input = Convert.FromBase64String("QVQmVEZPUk0AAAHPREpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAGxAGSBAgAgACAArrNCwYQ5eoJsZMQkBhgDAiNb78tsXgrYK1r22e/twfpOar3tIxhWKtT/qbYMvC0NSIElrlptR7r9Yy6NxT8uHZG23f+m+fKLxyxLoPfsD3TwjOw8s3jUXWpIyhq+kiPw87i7m1UdNzXk6sC89ZLUy9sYlD7BDtUy0GoM+3I6NbQWnqGSDKkIHWRWeWToW6/2PBBOsJzo50jyqgNSrcF0FkYh9r0VUP/fl0rRS+sHEMkPoYtXSWXJzLQh2059jr8ixu/qyRGk9RRdfOE5CX4PgljceGZpeHnEU+6cUS1E4NNrKXwnsGloLmt3Pig2ZglAD6m98nx5QFThq55zOZSpPbwAGdI5WwPfEc65/kZwAAN8CXt16rQrmPs9uDfdFewZ3XgqWNnk7UrLvUIi9O0gy7lFS97Iuy0MTkuReZWojhF9PtVViVJkYuLNQRDOkdOKUNcrl0WS6GZLrJPFTwC0e1PG1wvcz1PaparZzlQmNFKkiZB7urlrsrKAZVqmwbMXViuAiTtia5k+XDZPIk95lMrNAZmCHoXHQjlQgNaZ32o3ZcqePnVXYg==");
        var expected = Convert.FromBase64String("AAgSGURMTFY7P0xNdHd1hXSGoKjX7v3/8v3///////8JEx0kT1dXYUZKV1h/goCQf5Grs+L5///9//////9GSBYeJi5ZY2RtUVNdYYmOjpuJmrK97P//////////TFZaIykvN2NvcnhbXGRqkpqbppOiucf2/////////1lfYmhTVVZiSVFfYIaLipd/go2SwdHc9OTy////UVRU////R1pfY2hUWmZtjpWXoI+QmaPM3OX05f///1NaZmb/T1lfam90e1xgbHOgqKiylpSfq9js+P/y////V2htdVdaZWpyeH6HbXB7gaqysryjoq663PL///z///9nbnp8YGJwc1hibXKboqKulZego8zP3NzF3Oj3Ki1IWD9DSlR+jI6PZm52gaWtr7aioqmt1OXr89Tc5PQnOEhLNjhDSHJ7gIBqc3yDsby+xaSosbfn7PL94d/x/h8xQD4oKjY7bHZ8fHiAiI21wsbMr7rGwfT9AAHt9Pn7LTE3PCIlMTVcZm9vq62wu6CkrL7h7vT62OLm7RsmJSUDBwsNNj08SC43QEC0uLzDr7K4vurz/QXi5vL4IScoJAkBAQYoLy81Iy44OMHHzNO1tsPL9P8MEfP+/wIsLC4jBAAAACQrKzQXICkpxs3V3MfI09YBBxMd/QQLETo8Mi8MBQYAISgqNRwjKSmttr/G9fn9Bubu9P8tMz1IJx8fGTo5LzISFSImT1RaWr3FzdH/BAoM8vcEDjhDSU0xKSksSktFRi8xPEVna29vyc3R3AkQFhX/CQ0URVVVWTUyODlgZWFnSElTWoeOlJTP1+DoEBYjJg8SGh9OT1deQUVPTnF4eYNqbHV5oqu0tAMIDBf6/wYTOj1KUTY2R0dsdnmEaW16f6mztb6gqa+wDRMZJQYNGRlERk9VQUZQVXWBiJOEiZWgxdXb3MDKz9IXHSQqDRQgIE9XXWVGSFNZh5WbrJadrLjr+///3+3w9SEoMDocJC0vVF9kbVJTXWWRn6e7q7bH1f//////////BhAaHkZSU1w/RVNVe4GBkXyGmaXY6/b/9fv//////z8UGyEqTVpeY05SXGKCio2YipOjs+D3/////////0RMUhohJy9eaWtzVVZgZpOcnKiPl6m48P////////9GTFNXKTA3PmdvcHtjZGxznaSlsZukt8X4/////////01VXF5bYGRuUVRfZI+VlKCHipWbyNjh9eP3/f87QEhS//9BQ2RsdXheX2lzmKGjqZSWoKrS5fL+7vv//0dRVWJCRlBQb3d+hGdqdXylrq+3naCrst3v+v/2////VFxga0xUW1t6gYeQcHSBhrK7u8Wmqra66fn///////9iZ2t1VWJnZw==");
        var frame = DjvuDocument.Open(input).RenderPage(0);

        Assert.Equal(32, frame.Width);
        Assert.Equal(32, frame.Height);
        for (int pixel = 0; pixel < expected.Length; pixel++)
        {
            float linear = (float)Math.Pow(expected[pixel] / 255.0, 2.2);
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(linear, frame.Pixels[pixel * 4 + channel]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }
}
