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
}
