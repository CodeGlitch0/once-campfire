namespace Campfire.Web.Identity;

public static class PasswordDigest
{
    // bcrypt $2a$ digest, cost 12 — Ruby bcrypt 3.1 / has_secure_password format (not the Rails test min-cost of 4).
    public const int Cost = 12;

    public static string? Hash(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: Cost);
    }

    public static bool Verify(string password, string? digest)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(digest))
        {
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, digest);
    }
}
