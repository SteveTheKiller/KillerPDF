using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44InteriorTests
{
    [Fact]
    public void ReconstructsInteriorPixelsOfOwnedThirtyTwoSquareImage()
    {
        // Owned patterned PGM encoded once with public c44, one 100-slice chunk.
        // DjVuLibre 3.5.29 ddjvu -format=pgm grayscale samples provide the compatibility
        // oracle. Linear frame expectations apply the declared INFO gamma of 2.2.
        var input = Convert.FromBase64String("QVQmVEZPUk0AAAHPREpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAGxAGSBAgAgACAArrNCwYQ5eoJsZMQkBhgDAiNb78tsXgrYK1r22e/twfpOar3tIxhWKtT/qbYMvC0NSIElrlptR7r9Yy6NxT8uHZG23f+m+fKLxyxLoPfsD3TwjOw8s3jUXWpIyhq+kiPw87i7m1UdNzXk6sC89ZLUy9sYlD7BDtUy0GoM+3I6NbQWnqGSDKkIHWRWeWToW6/2PBBOsJzo50jyqgNSrcF0FkYh9r0VUP/fl0rRS+sHEMkPoYtXSWXJzLQh2059jr8ixu/qyRGk9RRdfOE5CX4PgljceGZpeHnEU+6cUS1E4NNrKXwnsGloLmt3Pig2ZglAD6m98nx5QFThq55zOZSpPbwAGdI5WwPfEc65/kZwAAN8CXt16rQrmPs9uDfdFewZ3XgqWNnk7UrLvUIi9O0gy7lFS97Iuy0MTkuReZWojhF9PtVViVJkYuLNQRDOkdOKUNcrl0WS6GZLrJPFTwC0e1PG1wvcz1PaparZzlQmNFKkiZB7urlrsrKAZVqmwbMXViuAiTtia5k+XDZPIk95lMrNAZmCHoXHQjlQgNaZ32o3ZcqePnVXYg==");
        var expected = Convert.FromBase64String("AAgSGERLS1U6PkpMcnVzg3GEnqbU6/r/8Pv///////8JEx0jT1ZWYEVJVVd9gH6OfI+psd/2///6//////9DRhYeJi1YYmNsT1JcYIeMjJmHl7C76f//////////SlRYIykvN2JucXdaWmNokZiZpJGgt8T0/////////1ddYGZTVVZiSVBeX4WKiJZ9gIuQv8/Z8eLw////T1JR////RVpfY2hUWWVtjZSVno2Ol6HJ2ePx4////1FYY2T/TVZdam90e1xfbHKfp6exlJKdqdbq9f/w////VGZqc1RYY2hyeH2HbG96gKmxsbuhoKy42vD///r///9la3h6Xl9ucVhibHGboaGtlJafocrN2tnD2uX1KCtGVj1BR1J8ioyNZm52gKStrrWhoair0uTp8dLa4vIlNkZJNDZARnB5fn5qc3uDsLy+xKOnsLbm6vD7393v/B0vPjwmKDQ5anR6eniAiIy1wcXLrrnFwPP7AADr8vf6Ky81OiAjLzNaZG1tq62vu6Cjq73g7fP41+Hk6xkkIyMBBgkLNDs6Riw1Pj60uLzDr7G4veny/APh5PH3HyUmIgcAAAQmLS0zISw2NsHHzNO0tcLK8/8LEPH8/wAqKiwhAwAAACIpKTIWHicnxs3U3MfI09UBBhIc/AMJEDg6MC0KAwQAHyYoMxshJyettr/F9fn9BuXt9P8sMjxHJh4eGDk4LjERFCElTlNZWb3FzdH/BAoM8vcEDjdDSU0wKSksSktFRi8xPEVna29vyc3R3QkQFhX/CQ0VRVZVWTYzOTphZWJoSUpUWoiOlZXP1+DoERYjJxASGyBPUFlfQ0ZRUHJ5eoVsbnd7o622tgMIDBf6/wcUOz5MUjg4SUlveXuGa3B8gay2t8Ciq7GyDRMaJgcOGhtFSFFXREhTWXmEi5eIjZijyNje38TN0tUXHiQqDhUhIlFZX2hJTFddi5ifsJmgsLzu////4/H0+SEpMDodJi8xVmFmcFVXYWiVo6u/r7rL2f//////////BhAbH0dUVV5AR1VXfoSFlICKnKnc7/n/+f7//////0MUGyEqTlpfZU9TXmSEjI+ajZWltuP6/////////0dPVRohKC9eaWt0VldhaJSdnqqRmKq68v////////9HTlRZKTA4PmdvcHtjZG1znaWlsZylt8X4/////////05VXF9bYGRuUVRfZI+VlKCHipWbyNjh9eP2/f87QEdR//9BQmRsdHdeX2lyl6GiqZOVn6nR5PH97fv//0ZQVGFBRU9Pb3Z+g2dpdHukra62nJ6psNvt+P/0////U1peakpSWlp6gIePb3OAhLG6usOkp7S45vb///z///9fZWhyU19kZA==");
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
