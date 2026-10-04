using System.Net;
using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using SessionStore = Campfire.Web.Identity.Sessions;

namespace Campfire.Web.Sessions;

public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/session/new", ShowNew);
        app.MapPost("/session", Create);
        app.MapGet("/session", MissingAction);
        app.MapGet("/session/edit", MissingAction);
        app.MapMethods("/session", ["PATCH", "PUT"], MissingAction);
        app.MapDelete("/session", Destroy);

        app.MapGet("/session/transfers/{id}", ShowTransfer);
        app.MapMethods("/session/transfers/{id}", ["PUT", "PATCH"], UpdateTransfer);
    }

    private static IResult MissingAction() => Results.NotFound();

    private static IResult ShowNew(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        if (!Accounts.Any(connection))
        {
            return Redirect(context, "/first_run");
        }

        var page = LoadPage(connection, RequestGuards.AppVersion(context.RequestServices.GetRequiredService<IConfiguration>()));
        return Html(RenderSignIn(page, alert: false, emailValue: null));
    }

    private static async Task<IResult> Create(HttpContext context, CampfireDatabase database, LoginRateLimit rateLimit)
    {
        var ip = RemoteIp.Resolve(context) ?? "";
        if (rateLimit.IsLimited(ip))
        {
            using var limited = database.Open();
            var limitedPage = LoadPage(limited, RequestGuards.AppVersion(context.RequestServices.GetRequiredService<IConfiguration>()));
            return Html(RenderSignIn(limitedPage, alert: true, emailValue: null), StatusCodes.Status429TooManyRequests);
        }

        if (!context.Request.HasFormContentType && context.Request.Query.Count == 0)
        {
            return Results.BadRequest();
        }

        string email;
        string password;
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            email = form["email_address"].ToString();
            password = form["password"].ToString();
        }
        else
        {
            email = context.Request.Query["email_address"].ToString();
            password = context.Request.Query["password"].ToString();
        }

        using var connection = database.Open();
        var userId = SessionStore.Authenticate(connection, email, password);
        if (userId is null)
        {
            var page = LoadPage(connection, RequestGuards.AppVersion(context.RequestServices.GetRequiredService<IConfiguration>()));
            return Html(RenderSignIn(page, alert: true, emailValue: email), StatusCodes.Status401Unauthorized);
        }

        var token = SessionStore.Start(
            connection,
            transaction: null,
            userId.Value,
            RemoteIp.Resolve(context),
            UserAgent(context.Request));
        WriteSessionCookie(context, token);
        return Redirect(context, "/");
    }

    private static async Task<IResult> Destroy(HttpContext context, CampfireDatabase database)
    {
        if (context.Items[CampfirePipeline.SessionIdItem] is not long sessionId
            || context.Items[CampfirePipeline.UserIdItem] is not long userId)
        {
            return Redirect(context, "/session/new");
        }

        using var connection = database.Open();
        var endpoint = await PushEndpoint(context);
        if (!string.IsNullOrEmpty(endpoint))
        {
            SessionStore.RemovePushSubscription(connection, userId, endpoint);
        }

        SessionStore.Destroy(connection, sessionId);
        SessionCookie.Clear(context.Response, SslPolicy.SecureCookies(context));
        return Redirect(context, "/");
    }

    private static IResult ShowTransfer(string id)
    {
        return Html($"""
            <!DOCTYPE html>
            <html>
            <head><title>Session transfer</title></head>
            <body><p>Session transfer</p><form method="post" action="/session/transfers/{Encode(id)}"></form></body>
            </html>
            """);
    }

    private static IResult UpdateTransfer(string id, HttpContext context, CampfireDatabase database)
    {
        var secret = Secret(context);
        var userId = RailsMessages.ReadTransferId(id, secret);
        using var connection = database.Open();
        if (userId is null || !SessionStore.ActiveUserExists(connection, userId.Value))
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        var token = SessionStore.Start(
            connection,
            transaction: null,
            userId.Value,
            RemoteIp.Resolve(context),
            UserAgent(context.Request));
        WriteSessionCookie(context, token);
        return Redirect(context, "/");
    }

    private static async Task<string?> PushEndpoint(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync();
            var value = form["push_subscription_endpoint"].ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        var query = context.Request.Query["push_subscription_endpoint"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static void WriteSessionCookie(HttpContext context, string token)
    {
        SessionCookie.Write(context.Response, token, Secret(context), SslPolicy.SecureCookies(context));
    }

    private static string Secret(HttpContext context)
    {
        return SecretKey.Resolve(context);
    }

    private static IResult Redirect(HttpContext context, string path)
    {
        return Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}{path}", permanent: false);
    }

    private static IResult Html(string body, int statusCode = StatusCodes.Status200OK)
    {
        return Results.Content(body, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static string? UserAgent(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        return string.IsNullOrEmpty(agent) ? null : agent;
    }

    private readonly record struct SignInPage(string AccountName, string? AdminName, string? AdminEmail, string Version);

    private static SignInPage LoadPage(Microsoft.Data.Sqlite.SqliteConnection connection, string version)
    {
        string accountName = "";
        using (var account = connection.CreateCommand())
        {
            account.CommandText = "SELECT name FROM accounts ORDER BY id LIMIT 1";
            if (account.ExecuteScalar() is string name)
            {
                accountName = name;
            }
        }

        string? adminName = null;
        string? adminEmail = null;
        using (var admin = connection.CreateCommand())
        {
            admin.CommandText = """
                SELECT name, email_address
                FROM users
                WHERE role = 1
                ORDER BY id
                LIMIT 1
                """;
            using var reader = admin.ExecuteReader();
            if (reader.Read())
            {
                adminName = reader.IsDBNull(0) ? null : reader.GetString(0);
                adminEmail = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        return new SignInPage(accountName, adminName, adminEmail, version);
    }

    private static string RenderSignIn(SignInPage page, bool alert, string? emailValue)
    {
        var alertClass = alert ? " shake" : "";
        var alertText = alert
            ? "<p class=\"alert\">Too many requests or unauthorized.</p>"
            : "";
        var help = "";
        if (page.AdminName is not null || page.AdminEmail is not null)
        {
            var name = page.AdminName ?? "";
            var email = page.AdminEmail ?? "";
            help = $"""
                <div class="txt-align-center margin-block-double full-width">
                  <a class="btn center" title="Email {Encode(name)}" href="mailto:&quot;{Encode(name)}&quot; &lt;{Encode(email)}&gt;">
                    <span>{Encode(email)}</span>
                  </a>
                  <div class="txt-align-center center margin-block txt-subtle">Campfire&trade; version <span class="version-badge">{Encode(page.Version)}</span></div>
                </div>
                """;
        }

        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Sign in</title></head>
            <body>
            <section class="txt-align-center">
              <div class="panel{alertClass}">
                {alertText}
                <form action="/session" method="post">
                  <fieldset>
                    <legend><strong>{Encode(page.AccountName)}</strong></legend>
                    <label>
                      <input type="email" name="email_address" required autofocus autocomplete="username" placeholder="Enter your email address" value="{Encode(emailValue ?? "")}">
                    </label>
                    <label>
                      <input type="password" name="password" required autocomplete="current-password" placeholder="Enter your password" maxlength="72">
                    </label>
                    <button type="submit" name="log_in">Go</button>
                  </fieldset>
                </form>
              </div>
              {help}
            </section>
            </body>
            </html>
            """;
    }

    private static string Encode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
