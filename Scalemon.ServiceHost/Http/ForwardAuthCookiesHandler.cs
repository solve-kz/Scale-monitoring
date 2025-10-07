using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Scalemon.ServiceHost.Http;

public class ForwardAuthCookiesHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ForwardAuthCookiesHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext?.Request is not { Cookies.Count: > 0 } requestContext)
        {
            return base.SendAsync(request, cancellationToken);
        }

        var cookiePairs = requestContext.Cookies
            .Select(cookie => $"{cookie.Key}={cookie.Value}")
            .Where(pair => !string.IsNullOrWhiteSpace(pair))
            .ToArray();

        if (cookiePairs.Length > 0)
        {
            request.Headers.Remove(HeaderNames.Cookie);
            request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, string.Join("; ", cookiePairs));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
