using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfTimestampAuthorityTests : IDisposable
{
    private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");
    private readonly RSA _signerKey = RSA.Create(2048);
    private readonly X509Certificate2 _signer;
    private readonly RSA _authorityKey = RSA.Create(2048);
    private readonly X509Certificate2 _authority;

    public PdfTimestampAuthorityTests()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _signer = new CertificateRequest("CN=KillerPDF Test Signer", _signerKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        var authorityRequest = new CertificateRequest("CN=KillerPDF Test Timestamp Authority",
            _authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        _authority = authorityRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    public void Dispose()
    {
        _signer.Dispose();
        _signerKey.Dispose();
        _authority.Dispose();
        _authorityKey.Dispose();
    }

    [Fact]
    public void AddTimestamp_EmbedsATokenThatVerifiesAgainstTheSignature()
    {
        byte[] content = "signed byte range"u8.ToArray();
        byte[] stamped = PdfTimestampAuthority.AddTimestamp(SignDetached(content), Authority);

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(stamped);
        cms.CheckSignature(verifySignatureOnly: true);
        SignerInfo signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        CryptographicAttributeObject attribute = Assert.Single(signer.UnsignedAttributes
            .Cast<CryptographicAttributeObject>(),
            a => a.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid);
        Assert.True(Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData,
            out Rfc3161TimestampToken? token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(signer, out X509Certificate2? tsa));
        Assert.Equal(_authority.Thumbprint, tsa!.Thumbprint);
        tsa.Dispose();
    }

    [Fact]
    public void AddTimestamp_LeavesATimestampedSignatureUnchanged()
    {
        byte[] stamped = PdfTimestampAuthority.AddTimestamp(SignDetached(new byte[] { 1, 2, 3 }), Authority);
        int calls = 0;

        byte[] again = PdfTimestampAuthority.AddTimestamp(stamped, request =>
        {
            calls++;
            return Authority(request);
        });

        Assert.Equal(stamped, again);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AddTimestamp_RejectsAReplyForADifferentSignature()
    {
        byte[] otherRequest = Rfc3161TimestampRequest.CreateFromHash(
            SHA256.HashData("something else"u8), HashAlgorithmName.SHA256,
            requestSignerCertificates: true, nonce: new byte[] { 1, 2, 3 }).Encode();

        Assert.Throws<PdfTimestampException>(() => PdfTimestampAuthority.AddTimestamp(
            SignDetached(new byte[] { 4, 5, 6 }), _ => Authority(otherRequest)));
    }

    [Fact]
    public void AddTimestamp_RejectsARefusedRequest()
    {
        Assert.Throws<PdfTimestampException>(() => PdfTimestampAuthority.AddTimestamp(
            SignDetached(new byte[] { 7, 8, 9 }), _ => Refusal()));
    }

    [Fact]
    public void Sign_WithRequestedTimestamp_EmbedsTheTokenInTheSignatureContents()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 300).Build();

        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(source),
            data => SignDetached(data), new PdfSignatureOptions
            {
                RequestTimestamp = true,
                TimestampTransport = Authority,
                SignerCertificate = _signer.RawDataMemory,
            });

        byte[] contents = ReadSignatureContents(signed);
        var cms = new SignedCms();
        cms.Decode(contents);
        Assert.Contains(cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>(),
            a => a.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid);
        // A genuine token bound to this signature is what baseline-T evidence means.
        Assert.Equal(PdfPadesProfile.BaselineT,
            Assert.Single(PdfSignatureInspection.Inspect(PdfDocument.Open(signed)).Entries).PadesProfile);
    }

    [Fact]
    public void ATokenForAnotherSignatureIsNotBaselineT()
    {
        // A genuine token, but its imprint covers a different signature value.
        var other = new SignedCms();
        other.Decode(PdfTimestampAuthority.AddTimestamp(SignDetached("other content"u8.ToArray()), Authority));
        byte[] foreignToken = other.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>()
            .Single(a => a.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid).Values[0].RawData;

        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 300).Build()), data =>
            {
                var cms = new SignedCms();
                cms.Decode(SignDetached(data));
                cms.SignerInfos[0].AddUnsignedAttribute(
                    new AsnEncodedData(PdfTimestampAuthority.SignatureTimestampTokenOid, foreignToken));
                return cms.Encode();
            }, new PdfSignatureOptions { SignerCertificate = _signer.RawDataMemory });

        PdfSignatureInspectionEntry entry = Assert.Single(PdfSignatureInspection.Inspect(PdfDocument.Open(signed)).Entries);
        Assert.True(entry.Verification.IsCryptographicallyValid);
        Assert.Equal(PdfPadesProfile.BaselineB, entry.PadesProfile);
    }

    [Fact]
    public void Sign_RequestedTimestampNeedsAServerOrATransport()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Throws<ArgumentException>(() => PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions { RequestTimestamp = true }));
        Assert.Throws<ArgumentException>(() => PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                RequestTimestamp = true,
                TimestampServerUrl = "ftp://timestamp.example.test"
            }));
    }

    [Fact]
    public void HttpTransport_PostsATimestampQueryAndReturnsTheReply()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "application/timestamp-reply", [9, 8, 7]);
        using var client = new HttpClient(handler);
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("http://timestamp.example.test/tsa"), client);

        byte[] reply = send(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 9, 8, 7 }, reply);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/timestamp-query", handler.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Body);
    }

    [Fact]
    public async Task HttpTransport_DeadlineCancelsAStalledResponseBody()
    {
        // The transport's deadline is a real timer that starts when the request starts. On a starved machine the
        // start-up of the first HTTP call (JIT, thread hand-offs) can take longer than the deadline itself, and then
        // the deadline expires before the body is ever read, which says nothing about reading the body. An attempt
        // counts only when the body read began; one that lost that race is discarded and run again with a fresh timer.
        // One ordinary exchange first, so the first-use cost of the HTTP stack is not spent against the deadline below.
        using (var warm = new HttpClient(new StubHandler(HttpStatusCode.OK, "application/timestamp-reply", [1])))
            PdfTimestampAuthority.HttpTransport(new Uri("http://timestamp.example.test/tsa"), warm)(new byte[] { 1 });
        const int attempts = 20;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            using var body = new StalledReplyStream();
            using var client = new HttpClient(new StubHandler(HttpStatusCode.OK,
                "application/timestamp-reply", [], body: body))
                { Timeout = Timeout.InfiniteTimeSpan };
            Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
                new Uri("http://timestamp.example.test/tsa"), client, timeout: TimeSpan.FromSeconds(1));
            Task<Exception?> request = Task.Factory.StartNew<Exception?>(
                () => Record.Exception(() => send(new byte[] { 1, 2, 3 })),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool counted;
            try
            {
                // Whichever happens first: the read starts (the attempt counts) or the request ends without one.
                Task first = await Task.WhenAny(body.ReadStarted.Task, request);
                counted = first == body.ReadStarted.Task;
                if (!counted) continue;
                // The body never ends by itself, so only the transport's own deadline can end the request. This wait
                // is not a race against a clock, it is the wait for that deadline; it is bounded only so that a
                // transport which ignores the deadline fails the test instead of hanging it.
                Task completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(ReferenceEquals(request, completed),
                    "The timestamp transport ignored its one-second deadline after response headers arrived.");
                Assert.IsAssignableFrom<OperationCanceledException>(await request);
                return;
            }
            finally
            {
                body.Release.TrySetResult();
                client.Dispose();
                await request.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Assert.Fail($"The body read never began before the one-second deadline in {attempts} attempts.");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "application/timestamp-reply")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    public void HttpTransport_RejectsAnErrorOrANonTimestampReply(HttpStatusCode status, string mediaType)
    {
        using var client = new HttpClient(new StubHandler(status, mediaType, [1]));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client);

        Assert.Throws<PdfTimestampException>(() => send(new byte[] { 1 }));
    }

    [Theory]
    [InlineData(false)]   // the reply declares its length
    [InlineData(true)]    // the reply is streamed without one
    public void HttpTransport_RefusesRepliesLargerThanTheLimit(bool undeclaredLength)
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "application/timestamp-reply",
            new byte[64], undeclaredLength));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client, maximumResponseBytes: 63);

        var error = Assert.Throws<PdfTimestampException>(() => send(new byte[] { 1 }));
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_AcceptsAReplyExactlyAtTheLimit()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "application/timestamp-reply",
            new byte[64], undeclaredLength: true));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client, maximumResponseBytes: 64);

        Assert.Equal(64, send(new byte[] { 1 }).Length);
    }

    [Fact]
    public void HttpTransport_RejectsANonHttpAddress() =>
        Assert.Throws<ArgumentException>(() =>
            PdfTimestampAuthority.HttpTransport(new Uri("file:///C:/tsa")));

    private byte[] SignDetached(ReadOnlyMemory<byte> data)
    {
        var cms = new SignedCms(new ContentInfo(data.ToArray()), detached: true);
        cms.ComputeSignature(new CmsSigner(_signer) { DigestAlgorithm = Sha256 }, silent: true);
        return cms.Encode();
    }

    // A minimal RFC 3161 authority: granted status, a TSTInfo echoing the request's imprint and
    // nonce, signed by a certificate with the critical timeStamping usage and the ESS
    // signing-certificate-v2 attribute that RFC 5816 requires.
    private byte[] Authority(ReadOnlyMemory<byte> requestBytes)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out Rfc3161TimestampRequest? request, out _));
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.32473.1"),
            request!.HashAlgorithmId, request.GetMessageHash(), "*"u8.ToArray(),
            DateTimeOffset.UtcNow, nonce: request.GetNonce());
        var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _authority)
        {
            DigestAlgorithm = Sha256,
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47",
            SigningCertificateV2(_authority)));
        token.ComputeSignature(signer, silent: true);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteInteger(0);
        writer.PopSequence();
        writer.WriteEncodedValue(token.Encode());
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] Refusal()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteInteger(2);
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] ReadSignatureContents(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int start = text.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        int end = text.IndexOf('>', start);
        byte[] reserved = Convert.FromHexString(text[start..end]);
        int length = new AsnReader(reserved, AsnEncodingRules.BER).ReadEncodedValue().Length;
        return reserved[..length];
    }

    private sealed class StubHandler(HttpStatusCode status, string mediaType, byte[] reply,
        bool undeclaredLength = false, Stream? body = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public byte[]? Body { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content?.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            HttpContent content = body is not null ? new StreamContent(body) : undeclaredLength
                ? new StreamContent(new UnseekableStream(reply)) : new ByteArrayContent(reply);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(status) { Content = content };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class StalledReplyStream : MemoryStream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    // A body with no known length, the way a chunked reply arrives.
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
