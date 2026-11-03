using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLogicalDocumentTests
{
    [Fact]
    public void IdentityOnlyOwnersHaveStableDistinctIdsWithoutReadSnapshots()
    {
        var first = new PdfLogicalDocument();
        var second = new PdfLogicalDocument();

        Guid firstId = first.Id;
        Guid secondId = second.Id;

        Assert.NotEqual(Guid.Empty, firstId);
        Assert.NotEqual(Guid.Empty, secondId);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, first.Id);
        Assert.Null(first.ReadSnapshot);
        Assert.Null(second.ReadSnapshot);
    }
}
