using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaFlowLayoutTests
{
    [Fact]
    public void PlanRepeatsBoundValuesAndPaginatesTopToBottom()
    {
        PdfXfaInfo info = new()
        {
            FormType = PdfXfaFormType.Dynamic,
            Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes("""
                <template><subform name="rows" layout="tb">
                  <field name="item" w="70pt" h="30pt"><bind ref="$record.order.item"/></field>
                </subform></template>
                """))]
        };
        var data = new PdfFormDataSet
        {
            Fields = [new PdfFormDataField
            {
                Name = "order.item", Values = ["one", "two", "three", "four"]
            }]
        };

        PdfXfaFlowLayoutPlan plan = PdfXfaFlowLayout.Plan(info, data, 100, 100, 10);

        Assert.Equal(2, plan.PageCount);
        Assert.Equal([0, 0, 1, 1], plan.Placements.Select(item => item.PageIndex));
        Assert.Equal([10d, 40d, 10d, 40d], plan.Placements.Select(item => item.Y));
        Assert.Equal(["one", "two", "three", "four"],
            plan.Placements.Select(item => item.Value));
        Assert.Equal([0, 1, 2, 3], plan.Placements.Select(item => item.OccurrenceIndex));
    }

    [Fact]
    public void PlanHonorsExplicitPageAreaBreaks()
    {
        PdfXfaInfo info = new()
        {
            Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes("""
                <template><subform name="form" layout="tb">
                  <field name="first" w="70pt" h="20pt"/>
                  <field name="second" w="70pt" h="20pt"><breakBefore targetType="pageArea"/></field>
                </subform></template>
                """))]
        };

        PdfXfaFlowLayoutPlan plan = PdfXfaFlowLayout.Plan(
            info, new PdfFormDataSet(), 100, 100, 10);

        Assert.Equal(2, plan.PageCount);
        Assert.Equal([0, 1], plan.Placements.Select(item => item.PageIndex));
        Assert.Equal([10d, 10d], plan.Placements.Select(item => item.Y));
    }

    [Fact]
    public void PlanKeepsRepeatedTableCellsInWholeRows()
    {
        PdfXfaInfo info = new()
        {
            Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes("""
                <template><subform name="table" layout="tb"><subform name="line" layout="row" h="25pt">
                  <field name="sku" w="35pt" h="25pt"><bind ref="$record.order.sku"/></field>
                  <field name="price" w="35pt" h="20pt"><bind ref="$record.order.price"/></field>
                </subform></subform></template>
                """))]
        };
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "order.sku", Values = ["A", "B", "C"] },
                new PdfFormDataField { Name = "order.price", Values = ["1", "2", "3"] }
            ]
        };

        PdfXfaFlowLayoutPlan plan = PdfXfaFlowLayout.Plan(info, data, 90, 70, 10);

        Assert.Equal(2, plan.PageCount);
        Assert.Equal([0, 0, 0, 0, 1, 1], plan.Placements.Select(item => item.PageIndex));
        Assert.Equal([10d, 45d, 10d, 45d, 10d, 45d], plan.Placements.Select(item => item.X));
        Assert.Equal([10d, 10d, 35d, 35d, 10d, 10d], plan.Placements.Select(item => item.Y));
        Assert.Equal(["A", "1", "B", "2", "C", "3"],
            plan.Placements.Select(item => item.Value));
    }
}
