using System.Net;
using System.Net.Http;

namespace KillerPDF.Services;

/// <summary>
/// Follows a killerpdf:// download's redirects one at a time. The user confirmed the link's own host, so a redirect
/// to another host asks again through <c>confirmHost</c>, and a redirect off HTTPS is refused. The inner handler
/// must not follow redirects itself.
/// </summary>
internal sealed class BrowserRedirects(HttpMessageHandler inner, Func<Uri, Task<bool>> confirmHost) : DelegatingHandler(inner)
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri current = request.RequestUri ?? throw new InvalidOperationException("The download has no address.");
        for (int hop = 0; ; hop++)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return response;
            response.Dispose();
            if (hop >= MaxRedirects) throw new HttpRequestException("The download redirected too many times.");

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("The download redirected to an address that is not HTTPS.");
            if (!string.Equals(next.IdnHost, current.IdnHost, StringComparison.OrdinalIgnoreCase)
                && !await confirmHost(next).ConfigureAwait(false))
                throw new BrowserRedirectRefusedException();

            // Every redirect of a download's GET is followed with a GET, as a browser does.
            if (hop > 0) request.Dispose();   // the caller's own request is the caller's to dispose
            request = new HttpRequestMessage(HttpMethod.Get, next);
            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

/// <summary>The user declined a host a killerpdf:// download redirected to.</summary>
internal sealed class BrowserRedirectRefusedException() : HttpRequestException("The download redirected to a host that was not confirmed.");
