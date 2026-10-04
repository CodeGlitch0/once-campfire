using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Campfire.Web.Identity;

/// <summary>
/// Rails signed messages used by the session cookie and user transfer ids.
/// </summary>
/// <remarks>
/// Session cookie (<c>cookies.signed.permanent[:session_token]</c>, Rails 8.2 / load_defaults 8.2):
/// key = PBKDF2-HMAC-SHA256(secret_key_base, "signed cookie", 1000, 64);
/// verifier digest SHA1; serializer NullSerializer so the JSON-encoded token is signed as-is;
/// legacy metadata envelope because NullSerializer is not an envelope serializer;
/// purpose <c>cookie.session_token</c>; expiry is the permanent-cookie timestamp (20 years).
/// Wire format: Base64.strict(JSON envelope) + "--" + HMAC-SHA1 hex.
/// Transfer id (<c>signed_id(purpose: :transfer, expires_in: 4.hours)</c>):
/// <c>use_legacy_signed_id_verifier = :generate_and_verify</c> prepends
/// digest SHA256, serializer JSON, url_safe true, salt <c>active_record/signed_id</c>,
/// purpose <c>user/transfer</c>. New metadata envelope (<c>{"_rails":{"data":id,"exp":...,"pur":...}}</c>).
/// </remarks>
public static class RailsMessages
{
    public const string SessionCookieSalt = "signed cookie";
    public const string SessionCookiePurpose = "cookie.session_token";
    public const string SignedIdSalt = "active_record/signed_id";
    public const string TransferPurpose = "user/transfer";
    public const string AvatarPurpose = "user/avatar";
    public static readonly TimeSpan TransferLifetime = TimeSpan.FromHours(4);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SignSessionToken(string token, string secret, DateTimeOffset expiresAt)
    {
        var jsonToken = JsonSerializer.Serialize(token, Json);
        var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonToken));
        var envelope = "{\"_rails\":{\"message\":\"" + message + "\",\"exp\":\"" + Iso8601Millis(expiresAt) + "\",\"pur\":\"" + SessionCookiePurpose + "\"}}";
        return Sign(envelope, DeriveKey(secret, SessionCookieSalt), HashAlgorithmName.SHA1, urlSafe: false);
    }

    public static string? ReadSessionToken(string? signed, string secret)
    {
        if (string.IsNullOrEmpty(signed))
        {
            return null;
        }

        if (!TryOpen(signed, DeriveKey(secret, SessionCookieSalt), HashAlgorithmName.SHA1, 40, out var envelope))
        {
            return null;
        }

        if (!TryReadEnvelope(envelope, SessionCookiePurpose, out var messageField, out _))
        {
            return null;
        }

        byte[] inner;
        try
        {
            inner = Convert.FromBase64String(messageField);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(Encoding.UTF8.GetString(inner), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SignTransferId(long userId, string secret, DateTimeOffset expiresAt)
    {
        var envelope = "{\"_rails\":{\"data\":" + userId.ToString(CultureInfo.InvariantCulture) + ",\"exp\":\"" + Iso8601Millis(expiresAt) + "\",\"pur\":\"" + TransferPurpose + "\"}}";
        return Sign(envelope, DeriveKey(secret, SignedIdSalt), HashAlgorithmName.SHA256, urlSafe: true);
    }

    public static long? ReadTransferId(string? signed, string secret)
    {
        if (string.IsNullOrEmpty(signed))
        {
            return null;
        }

        if (!TryOpen(signed, DeriveKey(secret, SignedIdSalt), HashAlgorithmName.SHA256, 64, out var envelope))
        {
            return null;
        }

        if (!TryReadEnvelope(envelope, TransferPurpose, out _, out var data))
        {
            return null;
        }

        if (data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out var id))
        {
            return id;
        }

        return null;
    }

    /// <summary>
    /// Rails <c>signed_id(purpose: :avatar)</c>: same verifier as transfer ids, no expiry.
    /// </summary>
    public static string SignRecordId(long id, string purpose, string secret)
    {
        var envelope = "{\"_rails\":{\"data\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"pur\":\"" + purpose + "\"}}";
        return Sign(envelope, DeriveKey(secret, SignedIdSalt), HashAlgorithmName.SHA256, urlSafe: true);
    }

    public static long? ReadRecordId(string? signed, string purpose, string secret)
    {
        if (string.IsNullOrEmpty(signed))
        {
            return null;
        }

        if (!TryOpen(signed, DeriveKey(secret, SignedIdSalt), HashAlgorithmName.SHA256, 64, out var envelope))
        {
            return null;
        }

        if (!TryReadEnvelope(envelope, purpose, out _, out var data))
        {
            return null;
        }

        if (data.ValueKind == JsonValueKind.Number && data.TryGetInt64(out var id))
        {
            return id;
        }

        return null;
    }

    public static string Iso8601Millis(DateTimeOffset expiresAt)
    {
        return expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    internal static byte[] DeriveKey(string secret, string salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(salt),
            iterations: 1000,
            HashAlgorithmName.SHA256,
            outputLength: 64);
    }

    private static string Sign(string envelope, byte[] key, HashAlgorithmName digest, bool urlSafe)
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(envelope), urlSafe);
        var signature = HmacHex(digest, key, encoded);
        return encoded + "--" + signature;
    }

    private static bool TryOpen(string signed, byte[] key, HashAlgorithmName digest, int digestHexLength, out string envelope)
    {
        envelope = "";
        if (!TrySplit(signed, digestHexLength, out var encoded, out var signature))
        {
            return false;
        }

        var expected = HmacHex(digest, key, encoded);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(signature)))
        {
            return false;
        }

        byte[] raw;
        try
        {
            raw = Decode(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        envelope = Encoding.UTF8.GetString(raw);
        return true;
    }

    private static bool TryReadEnvelope(string envelope, string purpose, out string message, out JsonElement data)
    {
        message = "";
        data = default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelope);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("_rails", out var rails))
            {
                return false;
            }

            if (!rails.TryGetProperty("pur", out var pur) || pur.GetString() != purpose)
            {
                return false;
            }

            if (rails.TryGetProperty("exp", out var exp))
            {
                var text = exp.GetString();
                if (string.IsNullOrEmpty(text)
                    || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry)
                    || DateTimeOffset.UtcNow >= expiry)
                {
                    return false;
                }
            }

            if (rails.TryGetProperty("message", out var messageElement))
            {
                message = messageElement.GetString() ?? "";
                return message.Length > 0;
            }

            if (rails.TryGetProperty("data", out var dataElement))
            {
                data = dataElement.Clone();
                return true;
            }

            return false;
        }
    }

    private static bool TrySplit(string signed, int digestHexLength, out string encoded, out string digest)
    {
        encoded = "";
        digest = "";
        var index = signed.Length - digestHexLength - 2;
        if (index < 1 || signed[index] != '-' || signed[index + 1] != '-')
        {
            return false;
        }

        encoded = signed[..index];
        digest = signed[(index + 2)..];
        return digest.Length == digestHexLength;
    }

    private static string HmacHex(HashAlgorithmName digest, byte[] key, string data)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        byte[] hash = digest.Name switch
        {
            "SHA1" => HMACSHA1.HashData(key, bytes),
            "SHA256" => HMACSHA256.HashData(key, bytes),
            _ => throw new InvalidOperationException($"Unsupported digest {digest.Name}."),
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Encode(byte[] data, bool urlSafe)
    {
        var encoded = Convert.ToBase64String(data);
        if (!urlSafe)
        {
            return encoded;
        }

        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Decode(string encoded)
    {
        var standard = encoded.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2:
                standard += "==";
                break;
            case 3:
                standard += "=";
                break;
        }

        return Convert.FromBase64String(standard);
    }
}
