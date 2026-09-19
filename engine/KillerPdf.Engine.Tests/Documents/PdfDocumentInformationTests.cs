using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentInformationTests
{
    [Fact]
    public void ReadTreatsANullTrappedEntryAsAbsent()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
            "<< /Title (Report) /Trapped null >>"
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(System.Text.Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = System.Text.Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R /Info 4 0 R >>\n"
            + $"startxref\n{xref}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(
            System.Text.Encoding.Latin1.GetBytes(pdf.ToString()));

        PdfDocumentInformation information = PdfDocumentInformation.Read(document);

        Assert.Null(information.Trapped);
        Assert.Equal("Report", information.Title);
    }

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

    [Fact]
    public void InitialView_AppliesCompleteSavedSelection()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetPageLayout(PdfPageLayout.OneColumn)
            .SetPageMode(PdfPageMode.UseNone)
            .SetViewerPreferences(new PdfViewerPreferences())
            .AddBlankPage().AddBlankPage()
            .SetOpenAction(0, PdfDestination.FitPage())
            .Build());
        var selection = new PdfInitialView
        {
            PageLayout = PdfPageLayout.TwoPageRight,
            PageMode = PdfPageMode.UseThumbs,
            ViewerPreferences = new PdfViewerPreferences
            {
                HideToolbar = true,
                CenterWindow = true,
                DisplayDocumentTitle = true
            },
            PageIndex = 1,
            Destination = PdfDestination.At(12, 34, 1.5)
        };

        PdfInitialView saved = PdfDocumentInformation.Read(
            PdfDocument.Open(selection.Apply(source))).InitialView;

        Assert.Equal(selection.PageLayout, saved.PageLayout);
        Assert.Equal(selection.PageMode, saved.PageMode);
        Assert.True(saved.ViewerPreferences.HideToolbar);
        Assert.True(saved.ViewerPreferences.CenterWindow);
        Assert.True(saved.ViewerPreferences.DisplayDocumentTitle);
        Assert.Equal(1, saved.PageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, saved.Destination?.Kind);
        Assert.Equal([12d, 34d, 1.5d], saved.Destination?.Values);
    }

    [Fact]
    public void InitialView_RejectsAmbiguousOpeningDestinations()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Throws<InvalidOperationException>(() => new PdfInitialView
        {
            NamedDestination = "start",
            PageIndex = 0,
            Destination = PdfDestination.FitPage()
        }.Apply(source));
        Assert.Throws<InvalidOperationException>(() => new PdfInitialView
        {
            PageIndex = 0
        }.Apply(source));
    }
}
