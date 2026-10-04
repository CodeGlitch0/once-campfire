namespace Campfire.Web.Http;

/// <summary>
/// Rails 8.2 forgery protection for ApplicationController (<c>load_defaults 8.2</c>):
/// <c>protect_from_forgery with: :exception</c> and
/// <c>forgery_protection_verification_strategy = :header_only</c>.
/// GET/HEAD pass. Otherwise Origin must be blank or equal the request base URL
/// (origin "null" fails), and Sec-Fetch-Site must be same-origin or same-site.
/// A missing Sec-Fetch-Site is allowed only when the request is not HTTPS
/// (<c>verified_via_header_only?</c>). Failure is 422
/// (<c>InvalidCrossOriginRequest</c> → unprocessable content).
/// Production does not reject a missing authenticity token under header_only.
/// </summary>
public static class RequestGuards
{
    public static bool IsUnsafe(HttpRequest request)
    {
        return !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
    }

    public static bool ForgeryAllows(HttpRequest request)
    {
        if (!IsUnsafe(request))
        {
            return true;
        }

        return OriginAllows(request) && FetchSiteAllows(request);
    }

    public static string AppVersion(IConfiguration configuration)
    {
        var revision = FirstNonEmpty(configuration["Campfire:GitRevision"], configuration["GIT_REVISION"]);
        var version = FirstNonEmpty(configuration["Campfire:AppVersion"], configuration["APP_VERSION"]);
        if (string.IsNullOrEmpty(version))
        {
            version = string.IsNullOrEmpty(revision) ? "0" : revision;
        }

        return version;
    }

    public static void ApplyVersionHeaders(HttpResponse response, IConfiguration configuration)
    {
        var revision = FirstNonEmpty(configuration["Campfire:GitRevision"], configuration["GIT_REVISION"]);
        response.Headers["X-Version"] = AppVersion(configuration);
        if (!string.IsNullOrEmpty(revision))
        {
            response.Headers["X-Rev"] = revision;
        }
    }

    private static bool OriginAllows(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var values))
        {
            return true;
        }

        var origin = values.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (origin == "null")
        {
            return false;
        }

        var baseUrl = $"{request.Scheme}://{request.Host}";
        return string.Equals(origin, baseUrl, StringComparison.Ordinal);
    }

    private static bool FetchSiteAllows(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Sec-Fetch-Site", out var values) || string.IsNullOrEmpty(values.ToString()))
        {
            return !SslPolicy.TreatAsHttps(request);
        }

        return values.ToString().ToLowerInvariant() switch
        {
            "same-origin" or "same-site" => true,
            _ => false,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
