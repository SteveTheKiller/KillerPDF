namespace CiValidation
{
    public sealed class DeliberateFailureProbe
    {
        [Xunit.Fact]
        public void DeliberatelyFailsForCiValidation()
        {
            Xunit.Assert.True(false, "Disposable CI validation PR: this failure is intentional.");
        }
    }
}
