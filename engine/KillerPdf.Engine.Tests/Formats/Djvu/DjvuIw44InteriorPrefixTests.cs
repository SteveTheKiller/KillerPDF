using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44InteriorPrefixTests
{
    [Theory]
    [InlineData(98, "BhAbIE1UU15CR1RVfH99jXuOqLDf9v//+v////////8GEBsgTVRTXkJHVFV8f32Ne46osN/2///6//////9FRxUcJC1VYWRrUVJbYYWMjZmKmbC96v//////////SVRZIygtOl5tdXdhXWJsjpmepZikt8v1/////////1ZiYmtVWFxeVVVaZHuIkJSKhoeWts7i6+fy////V1Jb////Q1pdYWtPWGlslJeUoYmOm6LQ3eP24f///1JYZ2b/Tllham90e1xgbHOgqKiylpWgqtrt9//5////VWVrbVtiZGpzeoCFc3J4g6Swtbuppa263PL///n///9lbXt6YmBwcldfaHaQn6eroJqbp8DI3NzF2+b4Ky9LWkRHSl92h5CRZnB5f6qvrLidoKur2OXq89jc4fAnOUpMMzZDQ3Z9gIBqc3yDsby+x6Knsrfs9PL82OH4/x8tOjkqKzU6a3Z8fHd/ho+wv8fNr7rFv/P6AQHw+vX6LjU8PCcnLjdXY3BwrLC1tqupqrrh7vX+1eHh7x4ZJiIABQcSKzlCRDg7Pj60t7rFqrC5vOnx/QLm5vf3Ii0mJQoCAwQuMS03His5OcLGy9W0tcTT9f8ME+///QMlLDAkBwAAACQrKzQXICkpx87V3srG0tYFBBMb/wcLFDc6NC0RBgMCGyYtMyIlKCirtb/B8P//AN3u9P8rLUFDMyIZHC81NS8dGR0pRFBcXLvFz9L/BQcP8vkFDTlFSFArJywrT01CRykwP0NsbW5u0dDO3wcKHBj/BgwVRFZVWTUyODlgZWFnSElTWoeOlZTM1d/nEBclKQ4UGyNITVpcR0ZMUGt2fIJwbnJ7nKm1tQAJEhf8/wAKNTtKSkE6QUpic36AdHF0gp6wurqqraysEhYaJwQMHhxGSE5YPERTVHuDhZV/iJiey9bY3rvGzc8QFhwlDxUgIk5XXmZGSFNZh5WbrJacrLjr+///3vD4/iQtNTkhJioxTl1ma1dVW2aLnaq5sbfF1////////v//Bg8XIztPWFlKSU1YcH6HjYeKk6nN6Pz//////////zcUHCMnU1tcZUhQX2GIjIqZhZGlsub6///6/////0NQWBohJy9eaWtzVVZgZpOcnKiPl6m48P////////9FT05TKS81QGFtcnppZml0l6Oor6GmtMfy/////////0tUXV9cY2lpXVdZaIOSmp2SjpCevdTn8e7///87MktQ//9FRmRrcnpZXmtxnaOgq4+UoqjX5+//6fj//0dXU2JARk9Pb3Z9hWVpdnunr664m5+ssd/w+f/0////VV9fbEtUW1t6gYiQcHSBhrK7vMWnqra66Pj///////9iZ2t1VmJnZw==")]
    [InlineData(31, "PUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiPUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiOkVQWmVveoSPm6izv8rV3uTn5+bk5OTk5OTk5OTk5OQ9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4k1VXmZudn6Hj5mirLW9xc3S09PT0tLS0tLS0tLS0tLSY2ludHl/hIqPlpyiqK6zuLu9vby7u7u7u7u7u7u7u7t7fYCDhYiKjY+SlZibnaCio6SkpKOjo6Ojo6Ojo6Ojo5WUk5OSkZGQj4+OjYyLi4qKioqKioqKioqKioqKioqKsq6qpqGdmZWRjIeDfnp2cm9ubm9vb29vb29vb29vb2/Nxr63sKihmpKKgXlwaWFbV1VVVldXV1dXV1dXV1dXV+PZz8W7saeckoZ7b2RZUEdBPj4/QUFBQUFBQUFBQUFB7eHVyr6zp5uPgnVoW05DOTIvLzAyMjIyMjIyMjIyMjLe08i9sqidkod7bmJWS0A3MS4uLzExMTExMTExMTExMcK6saifl46GfXNpX1VMRDw3NTU2Nzc3Nzc3Nzc3Nzc3opyWkIqEfnhxa2RdVlBKRUFAQEBBQUFBQUFBQUFBQUF+e3h1cm9saWZjX1xZVlNQT05OTk9PT09PT09PT09PT1paWltbW1tbW1xcXFxdXV1dXV1dXV1dXV1dXV1dXV1dODw/QkVJTE9SVlpeYWVoam1tbW1tbG1tbW1tbW1tbW0bISctMzk/RkxTWmFnbnR5fX5+fn19fX19fX19fX19fQkRGSEqMjpCS1RdZ3B4gIeNjo6OjYyNjY2NjY2NjY2NDRYeJzA4QUpTXWdxeoSMlJmbm5qZmZmZmZmZmZmZmZkcJS02PkdPWGBqdH2HkJifpKampaSkpKSkpKSkpKSkpDE5QEhQWGBocHmCi5ScpKqvsbGwr6+vr6+vr6+vr6+vRk5VXGNqcnmAiJGZoaivtbq7vLu6urq6urq6urq6urpXXmRrcnmAh46WnqWttLvBxcfHxsXFxcXFxcXFxcXFxWVsc3qAh42Um6OqsrnAx8zR0tLR0dHR0dHR0dHR0dHRdXuCiI+Vm6KosLe+xczT2Nzd3d3c3Nzc3Nzc3Nzc3NyEi5GXnaOpr7a9xMvS2N7j5+np6Ofn5+fn5+fn5+fn5w==")]
    [InlineData(41, "QUxWYGt1f4mUoK26xtHb4+fk3tbOx8C6tbW1tbW1tbVBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tUFMVmBrdX+JlKCtusbR2+Pn5N7WzsfAurW1tbW1tbW1QUxWYGt1f4mUoK26xtHb4+fk3tbOx8C6tbW1tbW1tbVBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tUFMVmBrdX+JlKCtusbR2+Pn5N7WzsfAurW1tbW1tbW1P0pUX2p0fomUoa67x9Pd5enm4NnQycK8t7e3t7e3t7dBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tVBYYWpyeoOLlJ6ptL3Hz9XY1c/Hv7ixq6emp6enp6enZGpwdnyCiI6UnKOrsrm+wsTAurOrpJ2XkpKSkpKSkpJ6fYCEh4qNkJSYnaGmqayurqqknZWOiIF9fH19fX19fZGSkpKTk5OTlJWXmJmZmpmXkoyFfndwamVlZWVlZWVlrKmmpKGem5iWlJGPjYqHhIB6c2xlXlZQS0pLS0tLS0vEv7mzrqiinZeSjIeBfHZwamNcVU5GPjcyMTIyMjIyMtjQyMC4sKifl4+HfnZuZl5WT0hBOjIqIx4dHh4eHh4e4djOxLuxp56UioB2bGJYT0dAOjQuJyAaFRUVFRUVFRXUy8K4r6adlIqAdmxiWVBHQDs3NDEtKSUiIiIiIiIiIry0rKSdlY2FfXVsY1pSS0Q/PTw8PDw7Ojo6Ojo6Ojo6n5mTjoiCfHZwaWJbVE1IQ0FBREhMT1JVV1dXV1dXV1eAfHh1cW1qZmJdWFNPSkdFRUlPVl5lbHJ3d3d3d3d3d2BeXVxaWVhXVVJPTUpIR0hLUltmcXyGkJeYl5eXl5eXQkNERUZHSElKSklISElKTVJcaXaEkqCttre2tra2trYoKy4xNTg8P0JERUdJTE9UXGh3h5eouMfS1NLS0tLS0hccISYrMTY7QENHSk5TWF9odYWWp7nL2+bo5ubm5ubmGyAmLDI4PUNJTVJWW2BmbXaDkaCwwNDf6evp6enp6ekoLjQ6QEZMUlddYmdscnh/h5GdqbXDz9vk5eTk5OTk5DpARkxRV11jaW90en+Fi5GYoKixucPM1Nrb2tra2traTVNYXmRqcHV7gYeNk5mfpKqvtLi9wsjN0NHQ0NDQ0NBbYWdtc3l+hIuRmJ6kq7G2ur2/wcLFx8nKysrKysrKymhudHqAho2TmaCnr7W8wsfLzMvKyMfGxsXFxcXFxcXFdXuCiI6Vm6GosLi/x87U2dva19LOycbCwL/AwMDAwMCCiY+WnaOpsLe/yNDY3+Xq7Oji29PMxb+6urq6urq6ug==")]
    public void ReconstructsInteriorPixelsAtTheDeclaredSliceCount(byte slices, string expectedBase64)
    {
        // Same owned source and public oracle as the full fixture, with only
        // the declared slice count changed. Extra arithmetic bits are ignored.
        var input = Convert.FromBase64String("QVQmVEZPUk0AAAHPREpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAGxAGSBAgAgACAArrNCwYQ5eoJsZMQkBhgDAiNb78tsXgrYK1r22e/twfpOar3tIxhWKtT/qbYMvC0NSIElrlptR7r9Yy6NxT8uHZG23f+m+fKLxyxLoPfsD3TwjOw8s3jUXWpIyhq+kiPw87i7m1UdNzXk6sC89ZLUy9sYlD7BDtUy0GoM+3I6NbQWnqGSDKkIHWRWeWToW6/2PBBOsJzo50jyqgNSrcF0FkYh9r0VUP/fl0rRS+sHEMkPoYtXSWXJzLQh2059jr8ixu/qyRGk9RRdfOE5CX4PgljceGZpeHnEU+6cUS1E4NNrKXwnsGloLmt3Pig2ZglAD6m98nx5QFThq55zOZSpPbwAGdI5WwPfEc65/kZwAAN8CXt16rQrmPs9uDfdFewZ3XgqWNnk7UrLvUIi9O0gy7lFS97Iuy0MTkuReZWojhF9PtVViVJkYuLNQRDOkdOKUNcrl0WS6GZLrJPFTwC0e1PG1wvcz1PaparZzlQmNFKkiZB7urlrsrKAZVqmwbMXViuAiTtia5k+XDZPIk95lMrNAZmCHoXHQjlQgNaZ32o3ZcqePnVXYg==");
        input[43] = slices;
        var expected = Convert.FromBase64String(expectedBase64);
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
