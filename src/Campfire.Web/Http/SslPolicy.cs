namespace Campfire.Web.Http;

/// <summary>
/// Rails production <c>config.assume_ssl = ENV["DISABLE_SSL"].blank?</c>.
/// Development does not assume SSL. An explicit <c>Campfire:AssumeSsl</c> wins, so tests can opt in.
/// <c>X-Forwarded-Proto</c> is not trusted until a proxy is pinned.
/// </summary>
public static class SslPolicy
{
    public static bool AssumeSsl(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Campfire:AssumeSsl"];
        if (!string.IsNullOrWhiteSpace(configured) && bool.TryParse(configured, out var explicitValue))
        {
            return explicitValue;
        }

        if (!environment.IsProduction())
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(configuration["DISABLE_SSL"]);
    }

    public static bool SecureCookies(HttpContext context)
    {
        return TreatAsHttps(context.Request);
    }

    /// <summary>
    /// Connection HTTPS, or assumed SSL. Never <c>X-Forwarded-Proto</c>.
    /// </summary>
    public static bool TreatAsHttps(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }

        var services = request.HttpContext.RequestServices;
        if (services is null)
        {
            return false;
        }

        var environment = services.GetService<IHostEnvironment>();
        var configuration = services.GetService<IConfiguration>();
        if (environment is null || configuration is null)
        {
            return false;
        }

        return AssumeSsl(environment, configuration);
    }
}
