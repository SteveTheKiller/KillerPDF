using Xunit;

namespace KillerPDF.Tests;

public sealed class SignedFieldAssociationTests
{
    [Fact]
    public void HistoryRestoreKeepsTheFieldAssociation()
    {
        var signed = new SignatureAnnotation { PageIndex = 0, FieldObjectNumber = 12 };
        var restored = new SignatureAnnotation { PageIndex = 0 };

        restored.CopyHistoryIdentityFrom(signed);

        Assert.Equal(signed.HistoryId, restored.HistoryId);
        Assert.Same(restored, SignatureAnnotation.ForField([restored], 12));
    }

    [Fact]
    public void AnIndependentCopyDoesNotClaimTheField()
    {
        PageAnnotation[] page =
        [
            new SignatureAnnotation { PageIndex = 0 },
            new SignatureAnnotation { PageIndex = 0, FieldObjectNumber = 12 },
        ];

        Assert.Same(page[1], SignatureAnnotation.ForField(page, 12));
    }

    [Fact]
    public void FieldIsUnsignedWithoutAMatchingSignature()
    {
        PageAnnotation[] page = [new SignatureAnnotation { FieldObjectNumber = 12 }];

        Assert.Null(SignatureAnnotation.ForField(page, 13));
        Assert.Null(SignatureAnnotation.ForField(null, 12));
    }
}
