using Campfire.Web.Data;
using Campfire.Web.Identity;
using SessionStore = Campfire.Web.Identity.Sessions;

namespace Campfire.Web.Http;

public static class CampfirePipeline
{
    public const string UserIdItem = "Campfire.UserId";
    public const string SessionIdItem = "Campfire.SessionId";

    public static void Use(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (IsHealth(context.Request))
            {
                await next();
                return;
            }

            if (!RequestGuards.ForgeryAllows(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            }

            var database = context.RequestServices.GetRequiredService<CampfireDatabase>();
            var secret = SecretKey.Resolve(context);
            using (var connection = database.Open())
            {
                var token = SessionCookie.ReadToken(context.Request, secret);
                if (token is not null)
                {
                    var session = SessionStore.Resume(
                        connection,
                        token,
                        RemoteIp.Resolve(context),
                        UserAgent(context.Request));
                    if (session is not null)
                    {
                        context.Items[UserIdItem] = session.Value.UserId;
                        context.Items[SessionIdItem] = session.Value.Id;
                    }
                }

                if (RequestGuards.IsUnsafe(context.Request)
                    && Bans.IsBanned(connection, RemoteIp.Resolve(context)))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }

            RequestGuards.ApplyVersionHeaders(context.Response, context.RequestServices.GetRequiredService<IConfiguration>());
            await next();
        });
    }

    private static bool IsHealth(HttpRequest request)
    {
        return request.Path.Equals("/up", StringComparison.Ordinal);
    }

    private static string? UserAgent(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        return string.IsNullOrEmpty(agent) ? null : agent;
    }
}
