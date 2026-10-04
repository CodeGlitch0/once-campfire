using System.Globalization;
using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using Campfire.Web.Rooms;
using Microsoft.Extensions.Primitives;

namespace Campfire.Web.Http;

/// <summary>
/// Room index/show/destroy, open/closed/direct create and update, involvement, refresh, and settings.
/// Authorization matches Rails: shared-room create can be admin-only; a direct is visible only to its members;
/// promoting a direct is out of reach because those controllers scope to <c>without_directs</c>.
/// </summary>
public static class RoomEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/rooms", Index);
        app.MapPost("/rooms", Missing);
        app.MapGet("/rooms/new", Missing);
        app.MapMethods("/rooms/{id:long}", ["PUT", "PATCH"], Missing);
        app.MapGet("/rooms/{id:long}/edit", Missing);
        app.MapGet("/rooms/{id:long}", Show);
        app.MapDelete("/rooms/{id:long}", Destroy);
        app.MapGet("/rooms/{roomId:long}/@{messageId:long}", ShowAt);
        app.MapGet("/rooms/{roomId:long}/refresh", Refresh);
        app.MapGet("/rooms/{roomId:long}/settings", Settings);
        app.MapGet("/rooms/{roomId:long}/involvement", ShowInvolvement);
        app.MapMethods("/rooms/{roomId:long}/involvement", ["PUT", "PATCH"], UpdateInvolvement);

        app.MapGet("/rooms/opens/new", NewOpen);
        app.MapPost("/rooms/opens", CreateOpen);
        app.MapGet("/rooms/opens", MissingRoom);
        app.MapGet("/rooms/opens/{id:long}", ShowOpen);
        app.MapGet("/rooms/opens/{id:long}/edit", EditOpen);
        app.MapMethods("/rooms/opens/{id:long}", ["PUT", "PATCH"], UpdateOpen);
        app.MapDelete("/rooms/opens/{id:long}", DestroyOpen);

        app.MapGet("/rooms/closeds/new", NewClosed);
        app.MapPost("/rooms/closeds", CreateClosed);
        app.MapGet("/rooms/closeds", MissingRoom);
        app.MapGet("/rooms/closeds/{id:long}", ShowClosed);
        app.MapGet("/rooms/closeds/{id:long}/edit", EditClosed);
        app.MapMethods("/rooms/closeds/{id:long}", ["PUT", "PATCH"], UpdateClosed);
        app.MapDelete("/rooms/closeds/{id:long}", DestroyClosed);

        app.MapGet("/rooms/directs/new", NewDirect);
        app.MapPost("/rooms/directs", CreateDirect);
        app.MapGet("/rooms/directs", MissingRoom);
        app.MapGet("/rooms/directs/{id:long}", ShowDirect);
        app.MapGet("/rooms/directs/{id:long}/edit", EditDirect);
        app.MapMethods("/rooms/directs/{id:long}", ["PUT", "PATCH"], Missing);
        app.MapDelete("/rooms/directs/{id:long}", DestroyDirect);
    }

    private static IResult Missing()
    {
        return Results.StatusCode(StatusCodes.Status404NotFound);
    }

    // resources :rooms is declared before namespace :rooms, so GET /rooms/opens matches
    // rooms#show with a non-numeric id and redirects as inaccessible.
    private static IResult MissingRoom(HttpContext context)
    {
        var denied = RequireUser(context);
        return denied ?? Inaccessible(context);
    }

    private static IResult Index(HttpContext context, CampfireDatabase database)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var last = RoomStore.LastIdForUser(connection, SignedIn.UserId(context)!.Value);
        if (last is null)
        {
            return Responses.Redirect(context, "/");
        }

        return Responses.Redirect(context, "/rooms/" + last.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static IResult Show(long id, HttpContext context, CampfireDatabase database)
    {
        return RenderShow(context, database, id, RoomScope.Member);
    }

    private static IResult ShowAt(long roomId, long messageId, HttpContext context, CampfireDatabase database)
    {
        return RenderShow(context, database, roomId, RoomScope.Member, messageId);
    }

    private static IResult ShowOpen(long id, HttpContext context, CampfireDatabase database)
    {
        return RedirectToRoom(context, database, id, RoomScope.WithoutDirects);
    }

    private static IResult ShowClosed(long id, HttpContext context, CampfireDatabase database)
    {
        return RedirectToRoom(context, database, id, RoomScope.WithoutDirects);
    }

    private static IResult ShowDirect(long id, HttpContext context, CampfireDatabase database)
    {
        return RenderShow(context, database, id, RoomScope.Directs);
    }

    private static IResult NewOpen(HttpContext context, CampfireDatabase database)
    {
        return NewShared(context, database, open: true);
    }

    private static IResult NewClosed(HttpContext context, CampfireDatabase database)
    {
        return NewShared(context, database, open: false);
    }

    private static async Task<IResult> CreateOpen(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireCreate(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "room"))
        {
            return Results.BadRequest();
        }

        var userId = SignedIn.UserId(context)!.Value;
        var roomId = RoomStore.CreateOpen(connection, userId, RoomName(form, context));
        return Responses.Redirect(context, RoomPath(roomId));
    }

    private static async Task<IResult> CreateClosed(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireCreate(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "room"))
        {
            return Results.BadRequest();
        }

        var userId = SignedIn.UserId(context)!.Value;
        var roomId = RoomStore.CreateClosed(connection, userId, RoomName(form, context), UserIds(form, context.Request.Query));
        return Responses.Redirect(context, RoomPath(roomId));
    }

    private static IResult NewDirect(HttpContext context, CampfireDatabase database)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        return Responses.Html(RenderDirectNew(RoomStore.ActiveUsers(connection), userId));
    }

    private static async Task<IResult> CreateDirect(HttpContext context, CampfireDatabase database)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        var userId = SignedIn.UserId(context)!.Value;
        var selected = UserIds(form, context.Request.Query).Append(userId).Distinct().ToArray();
        using var connection = database.Open();
        var roomId = RoomStore.FindOrCreateDirect(connection, userId, selected);
        return Responses.Redirect(context, RoomPath(roomId));
    }

    private static IResult EditOpen(long id, HttpContext context, CampfireDatabase database)
    {
        return EditShared(context, database, id, open: true);
    }

    private static IResult EditClosed(long id, HttpContext context, CampfireDatabase database)
    {
        return EditShared(context, database, id, open: false);
    }

    private static async Task<IResult> UpdateOpen(long id, HttpContext context, CampfireDatabase database)
    {
        return await UpdateShared(id, context, database, RoomStore.OpenType, revise: false);
    }

    private static async Task<IResult> UpdateClosed(long id, HttpContext context, CampfireDatabase database)
    {
        return await UpdateShared(id, context, database, RoomStore.ClosedType, revise: true);
    }

    private static IResult EditDirect(long id, HttpContext context, CampfireDatabase database)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        var room = RoomStore.FindForUser(connection, userId, id, RoomScope.Directs);
        if (room is null)
        {
            return Inaccessible(context);
        }

        var viewer = Users.Find(connection, userId);
        var others = RoomStore.MembersExcept(connection, room.Value.Id, userId);
        var shown = others.Count > 0
            ? others
            : (viewer is null ? others : [new RoomUser(viewer.Value.Id, viewer.Value.Name)]);
        return Responses.Html(RenderDirectEdit(room.Value, shown));
    }

    private static IResult Destroy(long id, HttpContext context, CampfireDatabase database)
    {
        return DestroyScoped(context, database, id, RoomScope.Member, administer: true);
    }

    private static IResult DestroyOpen(long id, HttpContext context, CampfireDatabase database)
    {
        return DestroyScoped(context, database, id, RoomScope.WithoutDirects, administer: true);
    }

    private static IResult DestroyClosed(long id, HttpContext context, CampfireDatabase database)
    {
        return DestroyScoped(context, database, id, RoomScope.WithoutDirects, administer: true);
    }

    private static IResult DestroyDirect(long id, HttpContext context, CampfireDatabase database)
    {
        return DestroyScoped(context, database, id, RoomScope.Directs, administer: false);
    }

    private static IResult Refresh(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMembership(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var since = Since(context.Request.Query["since"]);
        var page = RoomStore.RefreshSince(scoped.Connection!, roomId, since);
        return Responses.Html(RenderRefresh(scoped.Room!.Value, page.Created, page.Updated));
    }

    private static IResult Settings(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMembership(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var room = scoped.Room!.Value;
        return Responses.Html($"""
            <!DOCTYPE html>
            <html>
            <head><title>Settings</title></head>
            <body>
            <h1>{Responses.Encode(room.Name)}</h1>
            <p>Room {room.Id.ToString(CultureInfo.InvariantCulture)}</p>
            </body>
            </html>
            """);
    }

    private static IResult ShowInvolvement(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMembership(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        return Responses.Html(RenderInvolvement(scoped.Room!.Value, scoped.Membership!.Value.Involvement ?? MembershipStore.Mentions));
    }

    private static async Task<IResult> UpdateInvolvement(long roomId, HttpContext context, CampfireDatabase database)
    {
        using var scoped = RequireMembership(context, database, roomId);
        if (scoped.Denied is not null)
        {
            return scoped.Denied;
        }

        var form = await PostedForm.Read(context.Request);
        var involvement = PostedForm.Optional(form, context.Request.Query, "involvement");
        if (!MembershipStore.IsInvolvement(involvement)
            || !MembershipStore.UpdateInvolvement(scoped.Connection!, scoped.Membership!.Value.Id, involvement!))
        {
            return Results.BadRequest();
        }

        return Responses.Redirect(context, RoomPath(roomId) + "/involvement");
    }

    private static IResult NewShared(HttpContext context, CampfireDatabase database, bool open)
    {
        using var connection = database.Open();
        var denied = RequireCreate(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var userId = SignedIn.UserId(context)!.Value;
        return Responses.Html(RenderSharedForm(
            open,
            persisted: false,
            roomId: null,
            RoomStore.DefaultName,
            RoomStore.ActiveUsers(connection),
            selected: open ? null : [userId],
            canAdminister: true,
            currentUserId: userId));
    }

    private static IResult EditShared(HttpContext context, CampfireDatabase database, long id, bool open)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        var room = RoomStore.FindForUser(connection, userId, id, RoomScope.WithoutDirects);
        if (room is null)
        {
            return Inaccessible(context);
        }

        var viewer = Users.Find(connection, userId);
        var canAdminister = viewer is not null && RoomStore.CanAdminister(viewer.Value, room.Value);
        return Responses.Html(RenderSharedForm(
            open,
            persisted: true,
            room.Value.Id,
            room.Value.Name ?? "",
            RoomStore.ActiveUsers(connection),
            open ? null : RoomStore.MemberIds(connection, room.Value.Id),
            canAdminister,
            userId));
    }

    private static async Task<IResult> UpdateShared(
        long id,
        HttpContext context,
        CampfireDatabase database,
        string type,
        bool revise)
    {
        using var connection = database.Open();
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        var userId = SignedIn.UserId(context)!.Value;
        var room = RoomStore.FindForUser(connection, userId, id, RoomScope.WithoutDirects);
        if (room is null)
        {
            return Inaccessible(context);
        }

        var viewer = Users.Find(connection, userId);
        if (viewer is null || !RoomStore.CanAdminister(viewer.Value, room.Value))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "room"))
        {
            return Results.BadRequest();
        }

        var revised = revise ? UserIds(form, context.Request.Query) : null;
        if (!RoomStore.UpdateShared(connection, room.Value, RoomName(form, context), type, revised))
        {
            return Inaccessible(context);
        }

        return Responses.Redirect(context, RoomPath(room.Value.Id));
    }

    private static IResult DestroyScoped(
        HttpContext context,
        CampfireDatabase database,
        long id,
        RoomScope scope,
        bool administer)
    {
        using var connection = database.Open();
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        var userId = SignedIn.UserId(context)!.Value;
        var room = RoomStore.FindForUser(connection, userId, id, scope);
        if (room is null)
        {
            return Inaccessible(context);
        }

        if (administer)
        {
            var viewer = Users.Find(connection, userId);
            if (viewer is null || !RoomStore.CanAdminister(viewer.Value, room.Value))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        RoomStore.Destroy(connection, room.Value.Id);
        return Responses.Redirect(context, "/");
    }

    private static IResult RenderShow(
        HttpContext context,
        CampfireDatabase database,
        long id,
        RoomScope scope,
        long? messageId = null)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        var room = RoomStore.FindForUser(connection, userId, id, scope);
        if (room is null)
        {
            return Inaccessible(context);
        }

        Remember(context, room.Value.Id);
        var viewer = Users.Find(connection, userId);
        var title = RoomStore.DisplayName(connection, room.Value, userId, viewer?.Name);
        var messages = MessageStore.ShowPage(connection, room.Value.Id, messageId);
        return Responses.Html(RenderRoom(room.Value, title, messages));
    }

    private static IResult RedirectToRoom(HttpContext context, CampfireDatabase database, long id, RoomScope scope)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        using var connection = database.Open();
        var room = RoomStore.FindForUser(connection, SignedIn.UserId(context)!.Value, id, scope);
        if (room is null)
        {
            return Inaccessible(context);
        }

        Remember(context, room.Value.Id);
        return Responses.Redirect(context, RoomPath(room.Value.Id));
    }

    private static Scoped RequireMembership(HttpContext context, CampfireDatabase database, long roomId)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return new Scoped(denied, null, null, null);
        }

        var connection = database.Open();
        var membership = MembershipStore.FindForUser(connection, SignedIn.UserId(context)!.Value, roomId);
        if (membership is null)
        {
            connection.Dispose();
            return new Scoped(Results.StatusCode(StatusCodes.Status404NotFound), null, null, null);
        }

        var room = RoomStore.Find(connection, roomId);
        if (room is null)
        {
            connection.Dispose();
            return new Scoped(Results.StatusCode(StatusCodes.Status404NotFound), null, null, null);
        }

        return new Scoped(null, connection, room, membership);
    }

    private static IResult? RequireUser(HttpContext context)
    {
        return SignedIn.UserId(context) is null ? Responses.Redirect(context, "/session/new") : null;
    }

    private static IResult? RequireCreate(HttpContext context, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        if (!RoomStore.CanCreateSharedRooms(connection, SignedIn.UserId(context)!.Value))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static IResult Inaccessible(HttpContext context)
    {
        return Responses.Redirect(context, "/");
    }

    private static void Remember(HttpContext context, long roomId)
    {
        var expires = SessionCookie.PermanentExpiry();
        var secure = SslPolicy.SecureCookies(context) ? "; secure" : "";
        context.Response.Headers.Append(
            "Set-Cookie",
            "last_room="
            + roomId.ToString(CultureInfo.InvariantCulture)
            + "; path=/; expires="
            + expires.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)
            + "; samesite=lax"
            + secure);
    }

    private static string? RoomName(IFormCollection? form, HttpContext context)
    {
        return PostedForm.Optional(form, context.Request.Query, "room[name]");
    }

    private static IReadOnlyList<long> UserIds(IFormCollection? form, IQueryCollection query)
    {
        var ids = new List<long>();
        Collect(ids, query["user_ids[]"]);
        Collect(ids, query["user_ids"]);
        if (form is not null)
        {
            Collect(ids, form["user_ids[]"]);
            Collect(ids, form["user_ids"]);
        }

        return ids.Distinct().ToArray();
    }

    private static void Collect(List<long> ids, StringValues values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }
        }
    }

    private static string Since(StringValues values)
    {
        var raw = values.Count == 0 ? null : values[values.Count - 1];
        if (string.IsNullOrEmpty(raw) || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            millis = 0;
        }

        try
        {
            return MembershipStore.FromEpochMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return MembershipStore.FromEpochMilliseconds(0);
        }
    }

    private static string RoomPath(long roomId)
    {
        return "/rooms/" + roomId.ToString(CultureInfo.InvariantCulture);
    }

    private static string RenderRoom(RoomRow room, string title, IReadOnlyList<MessageRow> messages)
    {
        var id = room.Id.ToString(CultureInfo.InvariantCulture);
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
            <title>{Responses.Encode(title)}</title>
            <meta name="current-room-id" content="{id}">
            </head>
            <body>
            <h1>{Responses.Encode(title)}</h1>
            <p class="room-type">{Responses.Encode(room.Type)}</p>
            {MessageEndpoints.RenderMessages(room.Id, messages)}
            {MessageEndpoints.RenderComposer(room.Id)}
            </body>
            </html>
            """;
    }

    private static string RenderSharedForm(
        bool open,
        bool persisted,
        long? roomId,
        string name,
        IReadOnlyList<RoomUser> users,
        IReadOnlyList<long>? selected,
        bool canAdminister,
        long currentUserId)
    {
        var action = open
            ? (persisted ? "/rooms/opens/" + roomId!.Value.ToString(CultureInfo.InvariantCulture) : "/rooms/opens")
            : (persisted ? "/rooms/closeds/" + roomId!.Value.ToString(CultureInfo.InvariantCulture) : "/rooms/closeds");
        var other = open
            ? (persisted ? "/rooms/closeds/" + roomId!.Value.ToString(CultureInfo.InvariantCulture) + "/edit" : "/rooms/closeds/new")
            : (persisted ? "/rooms/opens/" + roomId!.Value.ToString(CultureInfo.InvariantCulture) + "/edit" : "/rooms/opens/new");
        var method = persisted ? "<input type=\"hidden\" name=\"_method\" value=\"patch\">" : "";
        var nameField = canAdminister
            ? $"<input type=\"text\" name=\"room[name]\" id=\"room_name\" value=\"{Responses.Encode(name)}\" required>"
            : $"<h1>{Responses.Encode(name)}</h1>";
        var people = open
            ? "<p>Everyone</p>" + string.Concat(users.Select(user => $"<p>{Responses.Encode(user.Name)}</p>"))
            : string.Concat(users.Select(user =>
            {
                var id = user.Id.ToString(CultureInfo.InvariantCulture);
                if (!persisted && user.Id == currentUserId)
                {
                    return $"<input type=\"hidden\" name=\"user_ids[]\" value=\"{id}\"><span>{Responses.Encode(user.Name)}</span>";
                }

                var check = selected is not null && selected.Contains(user.Id) ? " checked" : "";
                return $"<label><input type=\"checkbox\" name=\"user_ids[]\" value=\"{id}\"{check}>{Responses.Encode(user.Name)}</label>";
            }));
        var save = canAdminister ? "<button type=\"submit\">Save</button>" : "";
        var delete = persisted && canAdminister
            ? $"<form method=\"post\" action=\"/rooms/{roomId!.Value.ToString(CultureInfo.InvariantCulture)}\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button type=\"submit\">Delete</button></form>"
            : "";
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>{(persisted ? "Edit room" : "New room")}</title></head>
            <body>
            <p><a href="{other}">Change room type</a></p>
            <form action="{action}" method="post">
            {method}
            {nameField}
            {people}
            {save}
            </form>
            {delete}
            </body>
            </html>
            """;
    }

    private static string RenderDirectNew(IReadOnlyList<RoomUser> users, long currentUserId)
    {
        var choices = string.Concat(users.Where(user => user.Id != currentUserId).Select(user =>
            $"<label><input type=\"checkbox\" name=\"user_ids[]\" value=\"{user.Id.ToString(CultureInfo.InvariantCulture)}\">{Responses.Encode(user.Name)}</label>"));
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>New ping</title></head>
            <body>
            <form action="/rooms/directs" method="post">
            {choices}
            <button type="submit">Start Ping</button>
            </form>
            </body>
            </html>
            """;
    }

    private static string RenderDirectEdit(RoomRow room, IReadOnlyList<RoomUser> users)
    {
        var names = string.Concat(users.Select(user => $"<p>{Responses.Encode(user.Name)}</p>"));
        var id = room.Id.ToString(CultureInfo.InvariantCulture);
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Edit ping</title></head>
            <body>
            {names}
            <form method="post" action="/rooms/directs/{id}">
            <input type="hidden" name="_method" value="delete">
            <button type="submit">Delete Ping</button>
            </form>
            </body>
            </html>
            """;
    }

    private static string RenderInvolvement(RoomRow room, string involvement)
    {
        var next = NextInvolvement(room.Type == RoomStore.DirectType, involvement);
        var id = room.Id.ToString(CultureInfo.InvariantCulture);
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Involvement</title></head>
            <body>
            <p class="involvement">{Responses.Encode(involvement)}</p>
            <form action="/rooms/{id}/involvement" method="post">
            <input type="hidden" name="_method" value="put">
            <input type="hidden" name="involvement" value="{Responses.Encode(next)}">
            <button type="submit">{Responses.Encode(next)}</button>
            </form>
            </body>
            </html>
            """;
    }

    private static string NextInvolvement(bool direct, string involvement)
    {
        var order = direct
            ? new[] { "everything", "nothing" }
            : new[] { "mentions", "everything", "nothing", "invisible" };
        var index = Array.IndexOf(order, involvement);
        if (index < 0)
        {
            return order[0];
        }

        return order[(index + 1) % order.Length];
    }

    private static string RenderRefresh(
        RoomRow room,
        IReadOnlyList<RefreshMessage> created,
        IReadOnlyList<RefreshMessage> updated)
    {
        var news = string.Concat(created.Select(message => $"<li class=\"new\">{message.Id.ToString(CultureInfo.InvariantCulture)}</li>"));
        var edits = string.Concat(updated.Select(message => $"<li class=\"updated\">{message.Id.ToString(CultureInfo.InvariantCulture)}</li>"));
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Refresh</title></head>
            <body>
            <h1>{Responses.Encode(room.Name)}</h1>
            <ul class="created">{news}</ul>
            <ul class="updated">{edits}</ul>
            </body>
            </html>
            """;
    }

    private sealed class Scoped : IDisposable
    {
        public Scoped(IResult? denied, Microsoft.Data.Sqlite.SqliteConnection? connection, RoomRow? room, MembershipRow? membership)
        {
            Denied = denied;
            Connection = connection;
            Room = room;
            Membership = membership;
        }

        public IResult? Denied { get; }

        public Microsoft.Data.Sqlite.SqliteConnection? Connection { get; }

        public RoomRow? Room { get; }

        public MembershipRow? Membership { get; }

        public void Dispose()
        {
            Connection?.Dispose();
        }
    }
}
