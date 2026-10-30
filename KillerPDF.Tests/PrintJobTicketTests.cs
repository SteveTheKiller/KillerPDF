using System.Printing;
using System.IO;
using System.Text;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintJobTicketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_UsesDocumentScopeFromCapabilitiesEvenWhenTheSeedHasOnlyJobScope(bool twoSided)
    {
        var ticket = new PrintTicket { Duplexing = Duplexing.TwoSidedShortEdge };
        ticket = PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, twoSided, false, null, null),
            copies: 1, fromDriverDialog: false, documentDuplex: true);

        Assert.Equal(twoSided, PrintJobTicket.Read(ticket).TwoSided);
        Assert.Null(ticket.Duplexing);
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords", "TwoSidedLongEdge", true)]
    [InlineData("http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords", "OneSided", false)]
    [InlineData("urn:printer-driver", "TwoSidedLongEdge", false)]
    public void Capabilities_RecognizeDocumentDuplexByNamespace(string keywordNamespace, string option, bool expected)
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <f:PrintCapabilities xmlns:f="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:k="{keywordNamespace}" version="1">
              <f:Feature name="k:DocumentDuplex"><f:Option name="k:{option}" /></f:Feature>
            </f:PrintCapabilities>
            """));
        Assert.Equal(expected, PrintJobTicket.SupportsDocumentDuplex(xml));
    }

    [Theory]
    [InlineData("TwoSidedLongEdge", false, false, false, "OneSided")]
    [InlineData("OneSided", true, false, false, "TwoSidedLongEdge")]
    [InlineData("TwoSidedShortEdge", true, true, true, "TwoSidedShortEdge")]
    [InlineData("TwoSidedShortEdge", false, false, true, "OneSided")]
    [InlineData("TwoSidedShortEdge", true, false, true, "TwoSidedLongEdge")]
    public void Apply_OverridesDocumentDuplexDefaultAndPreservesDriverSettings(
        string initial, bool twoSided, bool shortEdge, bool fromDriverDialog, string expected)
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <f:PrintTicket xmlns:f="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:k="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords"
                xmlns:driver="urn:printer-driver" version="1">
              <f:Feature name="k:DocumentDuplex"><f:Option name="k:{initial}">
                <f:ScoredProperty name="driver:Binding"><f:Value>keep binding</f:Value></f:ScoredProperty>
              </f:Option></f:Feature>
              <f:Feature name="driver:Finishing"><f:Option name="driver:Staple" /></f:Feature>
            </f:PrintTicket>
            """));
        var ticket = new PrintTicket(xml);

        ticket = PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, twoSided, false, null, null, shortEdge),
            copies: 1, fromDriverDialog: fromDriverDialog);

        Assert.Equal(twoSided, PrintJobTicket.Read(ticket).TwoSided);
        using var result = ticket.GetXmlStream();
        XDocument document = XDocument.Load(result);
        XNamespace f = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        XElement finishing = Assert.Single(document.Root!.Elements(f + "Feature"),
            element => (string?)element.Attribute("name") == "driver:Finishing");
        Assert.Equal("driver:Staple", (string?)finishing.Element(f + "Option")?.Attribute("name"));
        XElement duplexOption = Assert.Single(document.Descendants(f + "Option"), element =>
            ((string?)element.Attribute("name"))?.EndsWith(":" + expected, StringComparison.Ordinal) == true);
        if (initial == expected)
            Assert.Equal("keep binding", duplexOption.Element(f + "ScoredProperty")?.Element(f + "Value")?.Value);
        Assert.Null(ticket.Duplexing);
        Assert.Equal(1, ticket.CopyCount);
    }

    [Theory]
    [InlineData("OneSided", false)]
    [InlineData("TwoSidedLongEdge", true)]
    [InlineData("TwoSidedShortEdge", true)]
    public void Read_RecognizesDocumentLevelDuplex(string option, bool twoSided)
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <f:PrintTicket xmlns:f="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:k="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
              <f:Feature name="k:DocumentDuplex"><f:Option name="k:{option}" /></f:Feature>
            </f:PrintTicket>
            """));
        var ticket = new PrintTicket(xml);
        Assert.Null(ticket.Duplexing);
        Assert.Equal(twoSided, PrintJobTicket.Read(ticket).TwoSided);
    }

    [Fact]
    public void Apply_WithoutADriverTicket_WritesEveryWindowSetting()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            CopyCount = 5
        };
        var letter = new PageMediaSize(PageMediaSizeName.NorthAmericaLetter, 816, 1056);

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(true, true, true, letter, InputBin.Manual),
            copies: 1, fromDriverDialog: false);

        Assert.Equal(1, ticket.CopyCount);
        Assert.Equal(PageOrientation.Landscape, ticket.PageOrientation);
        Assert.Equal(Duplexing.TwoSidedLongEdge, ticket.Duplexing);
        Assert.Equal(OutputColor.Grayscale, ticket.OutputColor);
        Assert.Equal(PageMediaSizeName.NorthAmericaLetter, ticket.PageMediaSize.PageMediaSizeName);
        Assert.Equal(InputBin.Manual, ticket.InputBin);
    }

    [Fact]
    public void Apply_WithADriverTicket_KeepsTheDriverVariantWhereTheWindowAgrees()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            InputBin = InputBin.Tractor
        };

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(true, true, true, null, null, ShortEdge: true),
            copies: 1, fromDriverDialog: true);

        Assert.Equal(PageOrientation.ReverseLandscape, ticket.PageOrientation);
        Assert.Equal(Duplexing.TwoSidedShortEdge, ticket.Duplexing);
        Assert.Equal(OutputColor.Monochrome, ticket.OutputColor);
        Assert.Equal(InputBin.Tractor, ticket.InputBin);
    }

    [Fact]
    public void Apply_WithADriverTicket_FollowsTheWindowWhereItWasChangedAfterward()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.Landscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Grayscale
        };

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, false, false, null, null),
            copies: 1, fromDriverDialog: true);

        Assert.Equal(PageOrientation.Portrait, ticket.PageOrientation);
        Assert.Equal(Duplexing.OneSided, ticket.Duplexing);
        Assert.Equal(OutputColor.Color, ticket.OutputColor);
    }

    [Fact]
    public void Read_ReportsTheWindowSettingsADriverTicketImplies()
    {
        var ticket = new PrintTicket
        {
            PageOrientation = PageOrientation.ReverseLandscape,
            Duplexing = Duplexing.TwoSidedShortEdge,
            OutputColor = OutputColor.Monochrome,
            CopyCount = 3
        };

        PrintJobTicket.DriverChoices choices = PrintJobTicket.Read(ticket);

        Assert.True(choices.Landscape);
        Assert.True(choices.TwoSided);
        Assert.True(choices.Grayscale);
        Assert.Equal(3, choices.Copies);
    }

    [Fact]
    public void Read_IsSilentWhereTheTicketSaysNothing()
    {
        PrintJobTicket.DriverChoices choices = PrintJobTicket.Read(new PrintTicket());

        Assert.Null(choices.Landscape);
        Assert.Null(choices.TwoSided);
        Assert.Null(choices.Grayscale);
        Assert.Null(choices.Copies);
        Assert.Null(choices.Media);
        Assert.Null(choices.ShortEdge);
    }

    [Fact]
    public void Apply_WritesTheBindingSideTheWindowAskedFor()
    {
        var ticket = new PrintTicket();

        PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, true, false, null, null, ShortEdge: true),
            copies: 1, fromDriverDialog: false);

        Assert.Equal(Duplexing.TwoSidedShortEdge, ticket.Duplexing);
    }

    [Fact]
    public void Apply_DocumentScope_WritesTheShortEdgeOption()
    {
        var ticket = new PrintTicket { Duplexing = Duplexing.TwoSidedLongEdge };
        ticket = PrintJobTicket.Apply(ticket,
            new PrintJobTicket.Settings(false, true, false, null, null, ShortEdge: true),
            copies: 1, fromDriverDialog: false, documentDuplex: true);

        using var xml = ticket.GetXmlStream();
        XDocument document = XDocument.Load(xml);
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "Option"),
            element => ((string?)element.Attribute("name"))?.EndsWith(":TwoSidedShortEdge") == true);
        Assert.Null(ticket.Duplexing);
    }

    [Theory]
    [InlineData("OneSided", false)]
    [InlineData("TwoSidedLongEdge", false)]
    [InlineData("TwoSidedShortEdge", true)]
    public void Read_ReportsTheBindingSideTheDriverPicked(string option, bool shortEdge)
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes($"""
            <f:PrintTicket xmlns:f="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                xmlns:k="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
              <f:Feature name="k:DocumentDuplex"><f:Option name="k:{option}" /></f:Feature>
            </f:PrintTicket>
            """));
        Assert.Equal(shortEdge, PrintJobTicket.Read(new PrintTicket(xml)).ShortEdge);
    }

    [Fact]
    public void TrimToDevMode_KeepsThePublicPartAndTheDriverData()
    {
        var buffer = new byte[400];
        BitConverter.GetBytes((ushort)220).CopyTo(buffer, 68);
        BitConverter.GetBytes((ushort)100).CopyTo(buffer, 70);

        Assert.Equal(320, PrinterDriverProperties.TrimToDevMode(buffer).Length);
    }

    [Fact]
    public void TrimToDevMode_LeavesABufferAloneWhenItsSizesDoNotFit()
    {
        var oversized = new byte[100];
        BitConverter.GetBytes((ushort)220).CopyTo(oversized, 68);

        Assert.Same(oversized, PrinterDriverProperties.TrimToDevMode(oversized));
    }
}
