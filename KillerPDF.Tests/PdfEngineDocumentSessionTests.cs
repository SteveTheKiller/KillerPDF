using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
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
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path);

            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());

            Assert.Equal(path, session.Path);
            Assert.Equal(original, session.Source.ToArray());
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
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path);
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
            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path);

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

                PdfEngineDocumentSession portrait = PdfEngineDocumentSession.Open(portraitPath);
                PdfEngineDocumentSession landscape = PdfEngineDocumentSession.Open(landscapePath);

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
}
