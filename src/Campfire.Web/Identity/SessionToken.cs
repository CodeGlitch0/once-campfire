using System.Security.Cryptography;

namespace Campfire.Web.Identity;

/// <summary>
/// Rails <c>has_secure_token</c> default: 24-character base58.
/// </summary>
public static class SessionToken
{
    public static string Generate()
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        Span<char> token = stackalloc char[24];
        for (var i = 0; i < token.Length; i++)
        {
            token[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(token);
    }
}
