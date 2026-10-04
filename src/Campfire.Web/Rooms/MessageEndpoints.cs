using System.Globalization;
using Campfire.Web.Data;
using Campfire.Web.Identity;
using Campfire.Web.Rooms;
using Microsoft.Extensions.Primitives;

namespace Campfire.Web.Http;

/// <summary>
/// Nested message create/index/show/update/destroy. No bot JSON routes and no Action Cable.
/// A create records the body; it does not open a websocket.
/// </summary>
public static class MessageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rooms/{roomId:long}/messages", Index);
        app.MapPost("/rooms/{roomId:long}/messages", Create);
        app.MapGet("/rooms/{roomId:long}/messages/new", New);
        app.MapGet("/rooms/{roomId:long}/messages/{id:long}", Show);
        app.MapGet("/rooms/{roomId:long}/messages/{id:long}/edit", Edit);
        app.MapMethods("/rooms/{roomId:long}/messages/{id:long}", ["PUT", "PATCH"], Update);
        app.MapDelete("/rooms/{roomId:long}/messages/{id:long}", Destroy);
    }

    public static string RenderMessages(long roomId, IReadOnlyList<MessageRow> messages)
    {
        var room = roomId.ToString(CultureInfo.InvariantCulture);
        var items = string.Concat(messages.Select(message =>
        {
            var id = message.Id.ToString(CultureInfo.InvariantCulture);
            var client = Responses.Encode(message.ClientMessageId);
            var body = Responses.Encode(message.Body);
            return $"""
                <article id="message_{client}" class="message" data-message-id="{id}" data-client-message-id="{client}">
                <a class="message__permalink" href="/rooms/{room}/@{id}">{id}</a>
                <div class="message__body">{body}</div>
                </article>
                """;
        }));
        return $"<div class=\"messages\" id=\"room_{room}_messages\">{items}</div>";
    }

    public static string RenderComposer(long roomId)
    {
        var action = "/rooms/" + roomId.ToString(CultureInfo.InvariantCulture) + "/messages";
        return $"""
            <form id="composer" action="{action}" method="post" enctype="multipart/form-data">
            <textarea name="message[body]" aria-label="Write a message"></textarea>
            <input type="hidden" name="message[client_message_id]" value="">
            <input type="file" name="message[attachment]">
            <button type="submit">Send</button>
            </form>
            """;
    }

    private static IResult Index(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMember(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        if (!Cursor(context.Request.Query["before"], out var before) || !Cursor(context.Request.Query["after"], out var after))
        {
            return Results.NotFound();
        }

        var page = MessageStore.IndexPage(scoped.Connection!, roomId, before, after);
        if (page is null)
        {
            return Results.NotFound();
        }

        if (page.Count == 0)
        {
            return Results.StatusCode(StatusCodes.Status204NoContent);
        }

        return Responses.Html(Frame("Messages", RenderMessages(roomId, page)));
    }

    private static async Task<IResult> Create(long roomId, HttpContext context, CampfireDatabase database)
    {
        if (SignedIn.UserId(context) is not long userId)
        {
            return Responses.Redirect(context, "/session/new");
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "message"))
        {
            return Results.BadRequest();
        }

        using var connection = database.Open();
        if (MembershipStore.FindForUser(connection, userId, roomId) is null)
        {
            return Responses.Html(RoomNotFound());
        }

        var clientId = PostedForm.Has(form, context.Request.Query, "message[client_message_id]")
            ? PostedForm.Optional(form, context.Request.Query, "message[client_message_id]") ?? ""
            : null;
        var message = MessageStore.Create(
            connection,
            roomId,
            userId,
            PostedForm.Optional(form, context.Request.Query, "message[body]"),
            clientId);
        return Responses.Html(Frame("Message", RenderMessages(roomId, [message])));
    }

    private static IResult New(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMember(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        return Responses.Html(Frame("New message", RenderComposer(roomId)));
    }

    private static IResult Show(long roomId, long id, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMember(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var message = MessageStore.FindInRoom(scoped.Connection!, roomId, id);
        if (message is null)
        {
            return Results.NotFound();
        }

        return Responses.Html(Frame("Message", RenderMessages(roomId, [message.Value])));
    }

    private static IResult Edit(long roomId, long id, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMessage(context, database, roomId, id, administer: true);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        return Responses.Html(RenderEdit(scoped.Message!.Value));
    }

    private static async Task<IResult> Update(long roomId, long id, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMessage(context, database, roomId, id, administer: true);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "message"))
        {
            return Results.BadRequest();
        }

        MessageStore.Update(
            scoped.Connection!,
            scoped.Message!.Value,
            PostedForm.Has(form, context.Request.Query, "message[body]"),
            PostedForm.Optional(form, context.Request.Query, "message[body]"),
            PostedForm.Has(form, context.Request.Query, "message[client_message_id]"),
            PostedForm.Optional(form, context.Request.Query, "message[client_message_id]"));
        return Responses.Redirect(context, MessagePath(roomId, id));
    }

    private static IResult Destroy(long roomId, long id, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMessage(context, database, roomId, id, administer: true);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        MessageStore.Destroy(scoped.Connection!, scoped.Message!.Value);
        return Responses.Html(Frame("Message", "<p>Deleted</p>"));
    }

    private static Scoped RequireMessage(
        HttpContext context,
        CampfireDatabase database,
        long roomId,
        long messageId,
        bool administer)
    {
        var scoped = RequireMember(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped;
        }

        var message = MessageStore.FindInRoom(scoped.Connection!, roomId, messageId);
        if (message is null)
        {
            scoped.Dispose();
            return new Scoped(Results.NotFound(), null, null, 0);
        }

        if (administer)
        {
            var user = Users.Find(scoped.Connection!, scoped.UserId);
            if (user is null || !MessageStore.CanAdminister(user.Value, message.Value.CreatorId))
            {
                scoped.Dispose();
                return new Scoped(Results.StatusCode(StatusCodes.Status403Forbidden), null, null, 0);
            }
        }

        return scoped with { Message = message };
    }

    private static Scoped RequireMember(HttpContext context, CampfireDatabase database, long roomId)
    {
        if (SignedIn.UserId(context) is not long userId)
        {
            return new Scoped(Responses.Redirect(context, "/session/new"), null, null, 0);
        }

        var connection = database.Open();
        if (MembershipStore.FindForUser(connection, userId, roomId) is null)
        {
            connection.Dispose();
            return new Scoped(Results.NotFound(), null, null, 0);
        }

        return new Scoped(null, connection, null, userId);
    }

    private static bool Cursor(StringValues values, out long? id)
    {
        id = null;
        var raw = values.Count == 0 ? null : values[values.Count - 1];
        if (string.IsNullOrEmpty(raw))
        {
            return true;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            return false;
        }

        id = parsed;
        return true;
    }

    private static string MessagePath(long roomId, long messageId)
    {
        return "/rooms/"
            + roomId.ToString(CultureInfo.InvariantCulture)
            + "/messages/"
            + messageId.ToString(CultureInfo.InvariantCulture);
    }

    private static string RenderEdit(MessageRow message)
    {
        var action = MessagePath(message.RoomId, message.Id);
        var body = Responses.Encode(message.Body);
        return Frame("Edit message", $"""
            <form action="{action}" method="post">
            <input type="hidden" name="_method" value="patch">
            <textarea name="message[body]">{body}</textarea>
            <button type="submit">Save changes</button>
            </form>
            <form action="{action}" method="post">
            <input type="hidden" name="_method" value="delete">
            <button type="submit">Delete message</button>
            </form>
            """);
    }

    private static string RoomNotFound()
    {
        return """
            <turbo-frame id="composer-frame">
            <span>This room was deleted.</span>
            </turbo-frame>
            """;
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

    private sealed record Scoped(IResult? Denied, Microsoft.Data.Sqlite.SqliteConnection? Connection, MessageRow? Message, long UserId) : IDisposable
    {
        public void Dispose()
        {
            Connection?.Dispose();
        }
    }
}
