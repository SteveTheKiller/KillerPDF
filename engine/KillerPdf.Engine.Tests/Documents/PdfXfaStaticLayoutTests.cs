using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaStaticLayoutTests
{
    [Fact]
    public void PlanResolvesNestedOffsetsAndMeasurementUnits()
    {
        PdfXfaInfo info = Info("""
            <template><subform name="form" layout="position" x="0.5in" y="10mm">
              <subform name="section" layout="position" x="12pt" y="0.25in">
                <field name="total" x="3pt" y="4pt" w="2in" h="8mm"/>
              </subform>
            </subform></template>
            """);

        PdfXfaFieldPlacement placement = Assert.Single(PdfXfaStaticLayout.Plan(info).Placements);

        Assert.Equal("form.section.total", placement.FieldPath);
        Assert.Equal(51, placement.X, 8);
        Assert.Equal((10 * 72 / 25.4) + 18 + 4, placement.Y, 8);
        Assert.Equal(144, placement.Width, 8);
        Assert.Equal(8 * 72 / 25.4, placement.Height, 8);
    }

    [Fact]
    public void PlanReportsFlowedFieldsWithoutInventingCoordinates()
    {
        PdfXfaInfo info = Info("""
            <template><subform name="rows" layout="tb">
              <field name="item" w="100pt" h="20pt"/>
            </subform></template>
            """);

        PdfXfaStaticLayoutPlan plan = PdfXfaStaticLayout.Plan(info);

        Assert.Empty(plan.Placements);
        Assert.Equal("rows.item", Assert.Single(plan.UnsupportedFlowedFieldPaths));
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        FormType = PdfXfaFormType.Static,
        Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes(template))]
    };
}
