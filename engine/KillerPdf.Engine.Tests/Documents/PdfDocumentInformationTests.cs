using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentInformationTests
{
    [Fact]
    public void Read_ReturnsMetadataVersionAndPageCount()
    {
        byte[] bytes = new PdfDocumentBuilder(PdfVersion.Pdf20)
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Technical overview",
                Author = "Steve",
                Subject = "The KillerPDF.Engine",
                Keywords = "PDF 2.0, PDF/A",
                Creator = "Tests",
                Producer = "The KillerPDF.Engine",
                Language = "en-US",
                CreationDate = new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)),
                ModificationDate = new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero),
                Trapped = PdfTrappedStatus.False
            })
            .SetPageLayout(PdfPageLayout.TwoColumnRight)
            .SetPageMode(PdfPageMode.UseOutlines)
            .SetViewerPreferences(new PdfViewerPreferences
            {
                HideToolbar = true,
                HideMenuBar = true,
                FitWindow = true,
                DisplayDocumentTitle = true
            })
            .AddBlankPage()
            .AddBlankPage()
            .SetOpenAction(1, PdfDestination.FitWidth(720))
            .Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Equal("Technical overview", info.Title);
        Assert.Equal("Steve", info.Author);
        Assert.Equal("The KillerPDF.Engine", info.Subject);
        Assert.Equal("PDF 2.0, PDF/A", info.Keywords);
        Assert.Equal("Tests", info.Creator);
        Assert.Equal("The KillerPDF.Engine", info.Producer);
        Assert.Equal("en-US", info.Language);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)), info.CreationDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero), info.ModificationDate);
        Assert.Equal(PdfTrappedStatus.False, info.Trapped);
        Assert.Equal(PdfVersion.Pdf20, info.Version);
        Assert.Equal(2, info.PageCount);
        Assert.Equal(PdfPageLayout.TwoColumnRight, info.InitialView.PageLayout);
        Assert.Equal(PdfPageMode.UseOutlines, info.InitialView.PageMode);
        Assert.True(info.InitialView.ViewerPreferences.HideToolbar);
        Assert.True(info.InitialView.ViewerPreferences.HideMenuBar);
        Assert.True(info.InitialView.ViewerPreferences.FitWindow);
        Assert.True(info.InitialView.ViewerPreferences.DisplayDocumentTitle);
        Assert.Equal(1, info.InitialView.PageIndex);
        Assert.Equal(PdfDestinationKind.FitH, info.InitialView.Destination?.Kind);
        Assert.Equal(720, info.InitialView.Destination?.Values.Single());
    }

    [Fact]
    public void Read_AllowsMissingInformationDictionary()
    {
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Null(info.Title);
        Assert.Null(info.Author);
        Assert.Equal(1, info.PageCount);
        Assert.NotNull(info.InitialView);
        Assert.Null(info.InitialView.PageLayout);
        Assert.Null(info.InitialView.PageMode);
        Assert.Null(info.InitialView.PageIndex);
    }
}
