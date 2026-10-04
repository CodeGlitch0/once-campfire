using System.Globalization;
using Campfire.Web.Data;
using Campfire.Web.Rooms;

namespace Campfire.Web.Http;

/// <summary>
/// <c>Messages::BoostsController</c> index/new/create/destroy. No bot routes and no Action Cable.
/// A non-member misses <c>reachable_messages</c> and gets 404, matching <c>RecordNotFound</c>.
/// Destroy is limited to the current user's own boost.
/// </summary>
public static class BoostEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/messages/{messageId:long}/boosts", Index);
        app.MapPost("/messages/{messageId:long}/boosts", Create);
        app.MapGet("/messages/{messageId:long}/boosts/new", New);
        app.MapDelete("/messages/{messageId:long}/boosts/{id:long}", Destroy);
    }

    private static IResult Index(long messageId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireReachable(context, database, messageId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var boosts = BoostStore.Ordered(scoped.Connection!, messageId);
        return Responses.Html(Frame("Boosts", RenderIndex(messageId, boosts)));
    }

    private static IResult New(long messageId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireReachable(context, database, messageId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        return Responses.Html(Frame("New boost", RenderNew(messageId)));
    }

    private static async Task<IResult> Create(long messageId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireReachable(context, database, messageId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "boost"))
        {
            return Results.BadRequest();
        }

        BoostStore.Create(
            scoped.Connection!,
            messageId,
            scoped.UserId,
            PostedForm.Optional(form, context.Request.Query, "boost[content]") ?? "");
        return Responses.Redirect(context, BoostsPath(messageId));
    }

    private static IResult Destroy(long messageId, long id, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireReachable(context, database, messageId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var boost = BoostStore.FindOwn(scoped.Connection!, messageId, id, scoped.UserId);
        if (boost is null)
        {
            return Results.NotFound();
        }

        BoostStore.Destroy(scoped.Connection!, boost.Value);
        return Responses.Html(Frame("Boost", "<p>Deleted</p>"));
    }

    private static string RenderNew(long messageId)
    {
        var action = BoostsPath(messageId);
        return $"""
            <form action="{action}" method="post">
            <label>Add a boost
            <input type="text" name="boost[content]" maxlength="16" required autocomplete="off">
            </label>
            <button type="submit">Submit</button>
            </form>
            <a href="{action}">Cancel</a>
            """;
    }

    private static string RenderIndex(long messageId, IReadOnlyList<BoostRow> boosts)
    {
        var items = string.Concat(boosts.Select(boost =>
        {
            var id = boost.Id.ToString(CultureInfo.InvariantCulture);
            var action = BoostsPath(messageId) + "/" + id;
            return $"""
                <div id="boost_{id}" class="boost">
                <span>{Responses.Encode(boost.Content)}</span>
                <form action="{action}" method="post">
                <input type="hidden" name="_method" value="delete">
                <button type="submit">Delete this boost</button>
                </form>
                </div>
                """;
        }));
        var list = BoostsPath(messageId);
        return $"""
            <div id="boosts_message_{messageId.ToString(CultureInfo.InvariantCulture)}">{items}</div>
            <a href="{list}/new">Add a boost</a>
            """;
    }

    private static string BoostsPath(long messageId)
    {
        return "/messages/" + messageId.ToString(CultureInfo.InvariantCulture) + "/boosts";
    }

    private static Scoped RequireReachable(HttpContext context, CampfireDatabase database, long messageId)
    {
        if (SignedIn.UserId(context) is not long userId)
        {
            return new Scoped(Responses.Redirect(context, "/session/new"), null, 0);
        }

        var connection = database.Open();
        if (!BoostStore.Reachable(connection, userId, messageId))
        {
            connection.Dispose();
            return new Scoped(Results.NotFound(), null, 0);
        }

        return new Scoped(null, connection, userId);
    }

    private static string Frame(string title, string body)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>{title}</title></head>
            <body>
            {body}
            </body>
            </html>
            """;
    }

    private sealed record Scoped(IResult? Denied, Microsoft.Data.Sqlite.SqliteConnection? Connection, long UserId) : IDisposable
    {
        public void Dispose()
        {
            Connection?.Dispose();
        }
    }
}
