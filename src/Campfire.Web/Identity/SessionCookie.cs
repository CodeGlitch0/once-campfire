using System.Globalization;

namespace Campfire.Web.Identity;

/// <summary>
/// Writes and reads the Rails <c>session_token</c> cookie:
/// signed, httponly, samesite=lax, path=/, expires in 20 years. Secure when the
/// connection is HTTPS or Rails <c>assume_ssl</c> is on. <c>X-Forwarded-Proto</c> is not consulted.
/// </summary>
public static class SessionCookie
{
    public const string Name = "session_token";

    public static DateTimeOffset PermanentExpiry(DateTimeOffset? now = null)
    {
        return (now ?? DateTimeOffset.UtcNow).AddYears(20);
    }

    public static void Write(HttpResponse response, string token, string secret, bool secure, DateTimeOffset? expiresAt = null)
    {
        var expires = expiresAt ?? PermanentExpiry();
        var signed = RailsMessages.SignSessionToken(token, secret, expires);
        response.Headers.Append("Set-Cookie", Format(signed, expires, secure));
    }

    public static void Clear(HttpResponse response, bool secure)
    {
        response.Headers.Append(
            "Set-Cookie",
            "session_token=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT; httponly; samesite=lax" + SecureFlag(secure));
    }

    public static string? FromRequest(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Cookie", out var headers))
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var part in header.Split(';'))
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith(Name + "=", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = trimmed[(Name.Length + 1)..];
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    public static string? ReadToken(HttpRequest request, string secret)
    {
        var raw = FromRequest(request);
        var token = RailsMessages.ReadSessionToken(raw, secret);
        if (token is not null || string.IsNullOrEmpty(raw))
        {
            return token;
        }

        if (raw.Contains('%', StringComparison.Ordinal))
        {
            token = RailsMessages.ReadSessionToken(Uri.UnescapeDataString(raw), secret);
            if (token is not null)
            {
                return token;
            }
        }

        if (raw.Contains(' ', StringComparison.Ordinal))
        {
            return RailsMessages.ReadSessionToken(raw.Replace(' ', '+'), secret);
        }

        return null;
    }

    public static string? ValueFromSetCookieHeader(string setCookie)
    {
        foreach (var line in setCookie.Split('\n'))
        {
            var segment = line.Split(';')[0].Trim();
            if (!segment.StartsWith(Name + "=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = segment[(Name.Length + 1)..];
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static string Format(string value, DateTimeOffset expires, bool secure)
    {
        var httpDate = expires.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        return $"session_token={value}; path=/; expires={httpDate}; httponly; samesite=lax{SecureFlag(secure)}";
    }

    private static string SecureFlag(bool secure)
    {
        return secure ? "; secure" : "";
    }
}
