using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ProtocolRegistrarTests
{
    [Fact]
    public void ParsesEncodedHttpsPdfUrl()
    {
        Assert.True(ProtocolRegistrar.TryGetTargetUrl(
            "killerpdf://open?url=https%3A%2F%2Fexample.com%2Ffile.pdf%3Fx%3D1", out var target));
        Assert.Equal("https://example.com/file.pdf?x=1", target!.AbsoluteUri);
    }

    [Theory]
    [InlineData("killerpdf://open?url=http%3A%2F%2Fexample.com%2Ffile.pdf")]
    [InlineData("killerpdf://open?url=file%3A%2F%2Fc%3A%2Fsecret.pdf")]
    [InlineData("killerpdf://wrong?url=https%3A%2F%2Fexample.com%2Ffile.pdf")]
    [InlineData("https://example.com/file.pdf")]
    public void RejectsUnsafeOrUnrelatedLaunches(string value)
        => Assert.False(ProtocolRegistrar.TryGetTargetUrl(value, out _));

    // #267 follow-up: a refusal has to name its branch, or the caller cannot tell a launch that
    // was aimed at KillerPDF and refused from one that was never a handoff. A table rather than a
    // Theory because HandoffRejection is internal and cannot sit in a public test signature.
    [Fact]
    public void ReportsWhyALaunchWasRefused()
    {
        (string Launch, ProtocolRegistrar.HandoffRejection Expected)[] cases =
        [
            ("killerpdf://open?url=https%3A%2F%2Fexample.com%2Ffile.pdf",
                ProtocolRegistrar.HandoffRejection.None),
            ("killerpdf://open?url=http%3A%2F%2Fexample.com%2Ffile.pdf",
                ProtocolRegistrar.HandoffRejection.SchemeNotAllowed),
            ("killerpdf://open?url=file%3A%2F%2Fc%3A%2Fsecret.pdf",
                ProtocolRegistrar.HandoffRejection.SchemeNotAllowed),
            ("killerpdf://open?url=notaurl", ProtocolRegistrar.HandoffRejection.MalformedUrl),
            ("killerpdf://open", ProtocolRegistrar.HandoffRejection.MissingUrl),
            ("killerpdf://open?other=1", ProtocolRegistrar.HandoffRejection.MissingUrl),
            ("killerpdf://wrong?url=https%3A%2F%2Fexample.com%2Ffile.pdf",
                ProtocolRegistrar.HandoffRejection.UnknownCommand),
            ("https://example.com/file.pdf", ProtocolRegistrar.HandoffRejection.NotAHandoff),
            (@"C:\missing\file.pdf", ProtocolRegistrar.HandoffRejection.NotAHandoff),
        ];

        foreach ((string launch, ProtocolRegistrar.HandoffRejection expected) in cases)
        {
            ProtocolRegistrar.TryGetTargetUrl(launch, out _, out var rejection);
            Assert.True(expected == rejection, $"{launch} gave {rejection}, expected {expected}");
        }
    }

    [Theory]
    [InlineData("killerpdf://open?url=https%3A%2F%2Fexample.com%2Ffile.pdf", true)]
    [InlineData("killerpdf://wrong", true)]
    [InlineData("KILLERPDF://open", true)]
    [InlineData("https://example.com/file.pdf", false)]
    [InlineData(@"C:\missing\file.pdf", false)]
    [InlineData("", false)]
    public void IsHandoffLaunchMatchesTheSchemeAndNothingElse(string value, bool expected)
        => Assert.Equal(expected, ProtocolRegistrar.IsHandoffLaunch(value));
}
