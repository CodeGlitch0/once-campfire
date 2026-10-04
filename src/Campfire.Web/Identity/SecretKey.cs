namespace Campfire.Web.Identity;

/// <summary>
/// Rails <c>secret_key_base</c>. Cookie and transfer signatures are derived from this value
/// the same way <c>Rails.application.key_generator</c> derives them.
/// </summary>
public static class SecretKey
{
    public const string Default = "campfire-development-secret-key-base";

    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["Campfire:SecretKeyBase"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromEnv = configuration["SECRET_KEY_BASE"];
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        return Default;
    }
}
