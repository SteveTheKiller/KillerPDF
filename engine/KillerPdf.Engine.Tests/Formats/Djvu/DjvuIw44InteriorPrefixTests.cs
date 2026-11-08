using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44InteriorPrefixTests
{
    [Theory]
    [InlineData(98, "BhAaIExUU11BRlJUe357i3mMpq7c8///+P////////8GEBogTFRTXUFGUlR7fnuLeYymrtzz///4//////9CRRUcJC1VYGNqUFFZX4OKi5eHl6675/7/////////R1FXIygtOV1tdHZgXGFqjJeco5ahtcjz/////////1RgYGlVWFxeVVRZY3qHjpOJhIWUs8zg6eXw////VVBY////QFpdYGpOV2hrk5aSoIeMmaDO2+D03/3//1BWZWT/S1Zfam90e1xfbHKfp6exlJOeqNjq9f/2////U2Npa1hgYmhzeYCEcnF3gqOvtLmno6u42vD///f///9ja3l4YF5tcFdfZ3aQnqeqn5mZpb/G2trC2OT2KS1JWEJFSF10hY6PZm95fqquq7ecn6qp1uPo8dba3+4lN0hKMTRBQXR7fX1qc3uDsLu9xqGmsbbr8/D61t/2/x0rODcoKTM3aXR6end+ho+vv8bMrrnEvvL4AADu+PP4LDM6OiUlLDVVYW5urLC1tqupqbrg7fT909/f7RwXJCAAAwUQKTdAQjY5PDy0t7nFqa+4u+jv+wDk5fb1ICskIwgAAQIsLys1HCk3N8LGy9WztMPS9P8LEe39+wEjKi4iBQAAACIqKTIWHicnx87V3crG0dYEAxIZ/wUKEjU4MisPBQEAGSQrMSAjJiartb/B8P7+AN3u8/8qLEBCMiEYGy40NC4cGBwoQ09bW7vFz9L/BAcP8vgFDThFR08rJywqT0xCRykwP0NsbW5u0dDP3wcKHBn/BgwVRVdWWjYzOTphZWJoSUpUWoiOlZXM1d/nEBglKg8VHCRJTlxeSEhOUm14fYNyb3R9nqq3twAJEhf9/wALNj1MS0M8Q0xkdYGDdnN3haGzvbytr66vEhYbKAUNHx5ISVBbPkdWV36GiJiCi5uhztrb4b/K0NIQFx0mEBYiJFBZYGhJTFddi5ifsJmgsLzv////4vP8/yQtNjoiJywyUGBpblpYXmqPoa69tbvJ2///////////Bg8XJDxQWlpMS1Bac4GKkYuNl6zR7P///////////zsUHCQoU1xdZkpRYWOKjoyciJSotOj8///8/////0ZTWxohJy9eaWt0VldhaJSdnqqRmKq68v////////9HUU9VKS81QGFtc3ppZmp1mKOosKKntMfz/////////0xVXWBcY2lpXVdZZ4OSmp2Sjo+evNTn8e7///87MktP//9FRmRrcnlZXWtxnKKgqo6ToafW5u7/6Pf//0ZWUmE/RU5Ob3Z9hGRodnumrq22mZ6rsN7u9//y////U11daklSWVl6gYePb3OAhbC6usOlqLS45fb///3///9fZGhyU19kZA==")]
    [InlineData(31, "PUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiPUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiOkVQWmVveoSPm6izv8rV3uTn5+bk5OTk5OTk5OTk5OQ9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4k1VXmZudn6Hj5mirLW9xc3S09PT0tLS0tLS0tLS0tLSY2ludHl/hIqPlpyiqK6zuLu9vby7u7u7u7u7u7u7u7t7fYCDhYiKjY+SlZibnaCio6SkpKOjo6Ojo6Ojo6Ojo5WUk5OSkZGQj4+OjYyLi4qKioqKioqKioqKioqKioqKsq6qpqGdmZWRjIeDfnp2cm9ubm9vb29vb29vb29vb2/Nxr63sKihmpKKgXlwaWFbV1VVVldXV1dXV1dXV1dXV+PZz8W7saeckoZ7b2RZUEdBPj4/QUFBQUFBQUFBQUFB7eHVyr6zp5uPgnVoW05DOTIvLzAyMjIyMjIyMjIyMjLe08i9sqidkod7bmJWS0A3MS4uLzExMTExMTExMTExMcK6saifl46GfXNpX1VMRDw3NTU2Nzc3Nzc3Nzc3Nzc3opyWkIqEfnhxa2RdVlBKRUFAQEBBQUFBQUFBQUFBQUF+e3h1cm9saWZjX1xZVlNQT05OTk9PT09PT09PT09PT1paWltbW1tbW1xcXFxdXV1dXV1dXV1dXV1dXV1dXV1dODw/QkVJTE9SVlpeYWVoam1tbW1tbG1tbW1tbW1tbW0bISctMzk/RkxTWmFnbnR5fX5+fn19fX19fX19fX19fQkRGSEqMjpCS1RdZ3B4gIeNjo6OjYyNjY2NjY2NjY2NDRYeJzA4QUpTXWdxeoSMlJmbm5qZmZmZmZmZmZmZmZkcJS02PkdPWGBqdH2HkJifpKampaSkpKSkpKSkpKSkpDE5QEhQWGBocHmCi5ScpKqvsbGwr6+vr6+vr6+vr6+vRk5VXGNqcnmAiJGZoaivtbq7vLu6urq6urq6urq6urpXXmRrcnmAh46WnqWttLvBxcfHxsXFxcXFxcXFxcXFxWVsc3qAh42Um6OqsrnAx8zR0tLR0dHR0dHR0dHR0dHRdXuCiI+Vm6KosLe+xczT2Nzd3d3c3Nzc3Nzc3Nzc3NyEi5GXnaOpr7a9xMvS2N7j5+np6Ofn5+fn5+fn5+fn5w==")]
    [InlineData(41, "QUxWYGp0foiToKy5xNDZ4eXj3NXMxb+4tLO0tLS0tLRBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tEFMVmBqdH6Ik6CsucTQ2eHl49zVzMW/uLSztLS0tLS0QUxWYGp0foiToKy5xNDZ4eXj3NXMxb+4tLO0tLS0tLRBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tEFMVmBqdH6Ik6CsucTQ2eHl49zVzMW/uLSztLS0tLS0P0pUX2lzfoiToK26xtHb4+fl39fPx8G6trW2tra2trZBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tFBYYWlyeoKKk56osrzGzdTX083GvrewqqWkpaWlpaWlZGpwdnyBh42Tm6Kqsbe9wcK/uLGpopyVkZCRkZGRkZF6fYCDhomMkJOXnKCkqKutrKiim5SNhoB7e3t7e3t7e5GSkpKSkpOTk5SWl5eYmJeVkIqEfHVvaGRjZGRkZGRkrKmmo6Cdm5iVk5COi4mGgn54cmtkXFVOSUhJSUlJSUnEv7mzraiinJaRi4WAenRuaGFaVExEPTYwMDAwMDAwMNjQyMC3r6eflo6FfXVsZFxUTUY/ODApIhwcHBwcHBwc4djOxLqxp52TiX91amBXTkU+ODIsJR8YFBMUFBQUFBTUy8K4r6ack4qAdmxiWE9GPzo2MzAsKCQhISEhISEhIby0rKSdlY2FfXRsY1pSSkQ/PDw8PDw7Ojo6Ojo6Ojo6n5mUjoiCfHZwaWJbVE5IREFCRUhMT1JVV1hXV1dXV1eAfHh1cW5rZ2NeWVRQTEhGR0tRWF9mbXR4eXh4eHh4eGBfXVxbWllYVlRRTkxKSkpNVF5oc36JkpmamZmZmZmZQkNERkdISktMS0tLS0tNUFVfbHmHlaOwubq5ubm5ubkoKy8yNjk9QURGR0lMT1NYYGx7ipusvMvW19bW1tbW1hcdIicsMjc9QUVJTVFWXGJseYmaq73P3+rs6urq6urqGyEnLTM5P0VKT1RZXmNqcXqHlaSzxNTi7e7t7e3t7e0oLjQ6QUdNU1leZGlvdHuCipSgrLjF0t7n6Ofn5+fn5zpARkxSWF5kanB1e4GHjZOaoqqzu8XO1t3d3d3d3d3dTVNZXmRqcHZ8goiOlJqgpauwtbm+xMnO0dLR0dHR0dFbYWdtc3l/hYuRmJ6lq7G2u77AwcPFx8nLy8vLy8vLy2hudHqAhoyTmaCnrrW8wsfLy8vJyMfGxsXFxcXFxcXFdXuCiI6Um6Gor7e/xs3T2NrZ1tHNycXBv7+/v7+/v7+CiY+WnKOpr7a+x8/X3uTp6ufh2dHKxL25uLm5ubm5uQ==")]
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
