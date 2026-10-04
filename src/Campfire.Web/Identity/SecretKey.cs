namespace Campfire.Web.Identity;

/// <summary>
/// Rails <c>secret_key_base</c>. Cookie and transfer signatures are derived from this value
/// the same way <c>Rails.application.key_generator</c> derives them.
/// </summary>
public static class SecretKey
{
    public const string Default = "campfire-development-secret-key-base";

    public static string Resolve(HttpContext context)
    {
        return Resolve(
            context.RequestServices.GetRequiredService<IConfiguration>(),
            context.RequestServices.GetRequiredService<IHostEnvironment>());
    }

    /// <summary>
    /// Development may fall back to <see cref="Default"/>. Every other environment refuses
    /// a missing secret and refuses that constant. Call this at startup so the process
    /// does not sign cookies or transfer ids with the development constant.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = FirstNonEmpty(configuration["Campfire:SecretKeyBase"], configuration["SECRET_KEY_BASE"]);
        if (environment.IsDevelopment())
        {
            return configured ?? Default;
        }

        if (configured is null || configured == Default)
        {
            throw new InvalidOperationException(
                "Refusing to start: signing secret is missing or is the development constant. Set SECRET_KEY_BASE.");
        }

        return configured;
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
