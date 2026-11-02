using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfEngineDocumentSessionTests
{
    [Fact]
    public void Open_OwnsImmutableBytesAndCachesPageGeometry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-session-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] original = new PdfDocumentBuilder().AddBlankPage(320, 480).Build();
            File.WriteAllBytes(path, original);
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path, null);

            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());

            Assert.Equal(path, session.Path);
            var originalPage = Assert.Single(PdfPageInformation.Read(session.Document));
            Assert.Equal(320, originalPage.Width);
            Assert.Equal(480, originalPage.Height);
            var page = Assert.Single(session.Pages);
            Assert.Equal(320, page.Width);
            Assert.Equal(480, page.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CaptureRotations_UsesNativeStateThenPreservesCompleteApplicationState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-rotations-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage(320, 480).Build();
            File.WriteAllBytes(path, source);
            PdfEngineIntegration.ApplyPageRotations(path,
                new Dictionary<int, int> { [0] = 90 });
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path, null);
            var rotations = new Dictionary<int, int>();

            session.CaptureRotations(rotations);
            Assert.Equal(90, rotations[0]);

            rotations[0] = 270;
            session.CaptureRotations(rotations);
            Assert.Equal(270, rotations[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VisualPageSize_UsesApplicationRotationWhenPresent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-visual-size-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(320, 480).Build());
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path, null);

            Assert.Equal((320d, 480d), session.VisualPageSize(0));
            Assert.Equal((480d, 320d), session.VisualPageSize(0,
                new Dictionary<int, int> { [0] = 90 }));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VisualPageSize_UsesExpectedDimensionsForNativeAndApplicationRotations()
    {
        string portraitPath = Path.Combine(Path.GetTempPath(), $"killerpdf-portrait-visual-{Guid.NewGuid():N}.pdf");
        string landscapePath = Path.Combine(Path.GetTempPath(), $"killerpdf-landscape-visual-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(portraitPath, new PdfDocumentBuilder().AddBlankPage(320, 480).Build());
            File.WriteAllBytes(landscapePath, new PdfDocumentBuilder().AddBlankPage(640, 360).Build());

            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                if (rotation != 0)
                {
                    PdfEngineIntegration.ApplyPageRotations(portraitPath,
                        new Dictionary<int, int> { [0] = rotation });
                    PdfEngineIntegration.ApplyPageRotations(landscapePath,
                        new Dictionary<int, int> { [0] = rotation });
                }

                PdfEngineDocumentSession portrait = PdfEngineDocumentSession.Open(portraitPath, null);
                PdfEngineDocumentSession landscape = PdfEngineDocumentSession.Open(landscapePath, null);

                (double, double) expectedPortrait = rotation is 90 or 270
                    ? (480d, 320d)
                    : (320d, 480d);
                (double, double) expectedLandscape = rotation is 90 or 270
                    ? (360d, 640d)
                    : (640d, 360d);

                Assert.Equal(expectedPortrait, portrait.VisualPageSize(0));
                Assert.Equal(expectedLandscape, landscape.VisualPageSize(0));

                Assert.Equal((320d, 480d), portrait.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 0 }));
                Assert.Equal((480d, 320d), portrait.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 90 }));
                Assert.Equal((320d, 480d), portrait.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 180 }));
                Assert.Equal((480d, 320d), portrait.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 270 }));

                Assert.Equal((640d, 360d), landscape.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 0 }));
                Assert.Equal((360d, 640d), landscape.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 90 }));
                Assert.Equal((640d, 360d), landscape.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 180 }));
                Assert.Equal((360d, 640d), landscape.VisualPageSize(0,
                    new Dictionary<int, int> { [0] = 270 }));
            }
        }
        finally
        {
            if (File.Exists(portraitPath)) File.Delete(portraitPath);
            if (File.Exists(landscapePath)) File.Delete(landscapePath);
        }
    }

    [Fact]
    public void Open_AcceptsTrailerSizeOneShortOfTheCrossReferenceTable()
    {
        // #406: the table lists objects 0 through 4 but the trailer says /Size 4.
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-short-size-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, ShortTrailerSizePdf());
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path, null);

            var page = Assert.Single(session.Pages);
            Assert.Equal(200, page.Width);
            Assert.Equal(300, page.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] ShortTrailerSizePdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 3 0 R /Resources << >> >>",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public void Open_ReadsOwnerRestrictedDocumentInformationAsText()
    {
        // #424: an empty user password opens the file; its strings must not stay encrypted.
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-owner-only-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Owner restricted" })
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions
                {
                    UserPassword = string.Empty,
                    OwnerPassword = "owner",
                    AllowDocumentModification = false
                })
                .AddBlankPage(320, 480)
                .Build());

            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path, null);

            Assert.True(session.Document.IsDecrypted);
            Assert.Equal("Owner restricted", PdfDocumentInformation.Read(session.Document).Title);
            Assert.True(PdfEngineIntegration.CanReadWithoutPassword(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
