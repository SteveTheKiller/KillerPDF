using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SvgSignatureImporterTests
{
    [Fact]
    public void Import_ReadsPassiveVectorPathsAndRejectsExternalContent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-svg-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string valid = Path.Combine(root, "signature.svg");
            File.WriteAllText(valid,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 150\"><path d=\"M 10 80 C 80 10 160 140 390 60\"/></svg>");
            SvgSignatureDocument signature = SvgSignatureImporter.Import(valid);

            Assert.Equal(400, signature.Width);
            Assert.Equal(150, signature.Height);
            Assert.Equal("M 10 80 C 80 10 160 140 390 60", Assert.Single(signature.Paths));

            string external = Path.Combine(root, "external.svg");
            File.WriteAllText(external,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><image href=\"https://example.com/a.png\"/><path d=\"M0 0L1 1\"/></svg>");
            Assert.Throws<InvalidDataException>(() => SvgSignatureImporter.Import(external));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
