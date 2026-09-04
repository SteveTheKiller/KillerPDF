using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaImagesTests
{
    [Fact]
    public void ReadPreservesEmbeddedImageBytesAndExternalReferences()
    {
        PdfXfaInfo info = Info("""
            <template><subform name="form">
              <field name="photo"><value><image contentType="image/jpeg" transferEncoding="base64">AQIDBA==</image></value><ui><imageEdit/></ui></field>
              <field name="logo"><value><image contentType="image/png" href="assets/logo.png"/></value><ui><imageEdit/></ui></field>
            </subform></template>
            """);

        IReadOnlyList<PdfXfaImageValue> images = PdfXfaImages.Read(info);

        Assert.Equal(2, images.Count);
        Assert.Equal("form.photo", images[0].FieldPath);
        Assert.Equal("image/jpeg", images[0].ContentType);
        Assert.Equal([1, 2, 3, 4], images[0].Data.ToArray());
        Assert.False(images[0].IsExternal);
        Assert.Equal("assets/logo.png", images[1].Href);
        Assert.True(images[1].IsExternal);
        Assert.True(images[1].Data.IsEmpty);
    }

    [Fact]
    public void ReadRejectsInvalidOrUnsupportedEmbeddedEncodings()
    {
        Assert.Throws<FormatException>(() => PdfXfaImages.Read(Info(
            """<template><field name="bad"><value><image>not base64</image></value></field></template>""")));
        Assert.Throws<NotSupportedException>(() => PdfXfaImages.Read(Info(
            """<template><field name="bad"><value><image transferEncoding="none">raw</image></value></field></template>""")));
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("template", Encoding.UTF8.GetBytes(template))]
    };
}
