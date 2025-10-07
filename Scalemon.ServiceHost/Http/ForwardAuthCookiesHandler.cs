using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Headers;
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
        var cookieHeader = _httpContextAccessor.HttpContext?.Request?.Headers[HeaderNames.Cookie];

        if (!StringValues.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.Remove(HeaderNames.Cookie);

            foreach (var value in cookieHeader)
            {
                if (CookieHeaderValue.TryParse(value, out var cookie))
                {
                    request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, cookie.ToString());
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, value);
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
