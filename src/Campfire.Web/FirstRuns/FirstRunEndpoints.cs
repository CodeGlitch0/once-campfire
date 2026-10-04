using Campfire.Web.Data;
using Campfire.Web.Identity;

namespace Campfire.Web.FirstRuns;

public static class FirstRunEndpoints
{
    // Minimal fields from app/views/first_runs/show.html.erb. Not a visual port.
    private const string ShowHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Set up Campfire</title></head>
        <body>
        <form action="/first_run" method="post">
        <label>Name <input name="user[name]" autocomplete="name" required autofocus></label>
        <label>Email address <input type="email" name="user[email_address]" autocomplete="username" required></label>
        <label>Password <input type="password" name="user[password]" autocomplete="new-password" maxlength="72" required></label>
        <button type="submit">Save</button>
        </form>
        </body>
        </html>
        """;

    public static void Map(WebApplication app)
    {
        // Routes exist in Rails but FirstRunsController does not define these actions (ActionNotFound → 404).
        // prevent_repeats does not run, because the missing action is raised before callbacks.
        app.MapGet("/first_run/new", MissingAction);
        app.MapGet("/first_run/edit", MissingAction);
        app.MapMethods("/first_run", ["PATCH", "PUT"], MissingAction);
        app.MapDelete("/first_run", MissingAction);

        app.MapGet("/first_run", Show);
        app.MapPost("/first_run", Create);
    }

    private static IResult MissingAction() => Results.NotFound();

    private static IResult Show(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        if (Accounts.Any(connection))
        {
            return RedirectToRoot(context);
        }

        return Results.Content(ShowHtml, "text/html; charset=utf-8");
    }

    private static async Task<IResult> Create(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        if (Accounts.Any(connection))
        {
            return RedirectToRoot(context);
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        var form = await context.Request.ReadFormAsync();
        if (!form.Keys.Any(key => key == "user" || key.StartsWith("user[", StringComparison.Ordinal)))
        {
            // params.require(:user) → ActionController::ParameterMissing → 400
            return Results.BadRequest();
        }

        var outcome = FirstRun.TryCreate(connection, new FirstRunRequest(
            form["user[name]"].ToString(),
            NullIfEmpty(form["user[email_address]"].ToString()),
            form["user[password]"].ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString()));

        if (outcome.Created && outcome.SessionToken is not null)
        {
            // Rails sets cookies.signed.permanent[:session_token]. Signing uses secret_key_base and is not this task.
            // The sessions row is the first-run requirement; the cookie carries that token (httponly, lax, 20 years).
            context.Response.Cookies.Append("session_token", outcome.SessionToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddYears(20),
                Path = "/",
                IsEssential = true,
            });
        }

        return RedirectToRoot(context);
    }

    private static IResult RedirectToRoot(HttpContext context)
    {
        var root = $"{context.Request.Scheme}://{context.Request.Host}/";
        return Results.Redirect(root, permanent: false);
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
