using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using UserStore = Campfire.Web.Identity.Users;

namespace Campfire.Web.Http;

/// <summary>
/// Join, user show, profile, sidebar, avatar, and ban routes.
/// Authorization matches Rails: join is unauthenticated and rejects a signed-in user;
/// profile and sidebar always use <c>Current.user</c>; bans require an administrator.
/// </summary>
public static class UserEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/join/{joinCode}", ShowJoin);
        app.MapPost("/join/{joinCode}", CreateJoin);

        app.MapGet("/users/{id:long}", ShowUser);
        app.MapGet("/users/{userId}/profile", ShowProfile);
        app.MapMethods("/users/{userId}/profile", ["PUT", "PATCH"], UpdateProfile);
        app.MapGet("/users/{userId}/sidebar", ShowSidebar);
        app.MapGet("/users/{userId}/avatar", ShowAvatar);
        app.MapDelete("/users/{userId}/avatar", DestroyAvatar);
        app.MapPost("/users/{id:long}/ban", CreateBan);
        app.MapDelete("/users/{id:long}/ban", DestroyBan);
    }

    private static IResult ShowJoin(string joinCode, HttpContext context, CampfireDatabase database)
    {
        var signedIn = RedirectIfSignedIn(context);
        if (signedIn is not null)
        {
            return signedIn;
        }

        using var connection = database.Open();
        var rejected = RejectJoinCode(connection, joinCode);
        if (rejected is not null)
        {
            return rejected;
        }

        var account = Accounts.First(connection);
        return Responses.Html(RenderJoin(account?.Name ?? "", joinCode));
    }

    private static async Task<IResult> CreateJoin(string joinCode, HttpContext context, CampfireDatabase database)
    {
        var signedIn = RedirectIfSignedIn(context);
        if (signedIn is not null)
        {
            return signedIn;
        }

        using var connection = database.Open();
        var rejected = RejectJoinCode(connection, joinCode);
        if (rejected is not null)
        {
            return rejected;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "user"))
        {
            return Results.BadRequest();
        }

        var email = EmptyToNull(PostedForm.Optional(form, context.Request.Query, "user[email_address]"));
        var created = UserStore.TryJoin(
            connection,
            Attachments.FilesRoot(database.Path),
            PostedForm.Optional(form, context.Request.Query, "user[name]"),
            email,
            PostedForm.Optional(form, context.Request.Query, "user[password]"),
            RemoteIp.Resolve(context),
            UserAgent(context.Request),
            await PostedForm.File(form, "user[avatar]"));
        if (created.DuplicateEmail)
        {
            return Responses.Redirect(context, "/session/new?email_address=" + Uri.EscapeDataString(email ?? ""));
        }

        if (created.SessionToken is not null)
        {
            WriteSession(context, created.SessionToken);
        }

        return Responses.Redirect(context, "/");
    }

    private static IResult ShowUser(long id, HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        using var connection = database.Open();
        var user = UserStore.Find(connection, id);
        if (user is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        var viewerId = SignedIn.UserId(context)!.Value;
        var viewer = UserStore.Find(connection, viewerId);
        var admin = viewer is not null && viewer.Value.Role == UserStore.AdministratorRole;
        return Responses.Html(RenderUser(user.Value, admin, viewerId == user.Value.Id));
    }

    private static IResult ShowProfile(HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        using var connection = database.Open();
        var user = UserStore.Find(connection, SignedIn.UserId(context)!.Value);
        if (user is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        var secret = Secret(context);
        var token = RailsMessages.SignRecordId(user.Value.Id, RailsMessages.AvatarPurpose, secret);
        return Responses.Html(RenderProfile(user.Value, token));
    }

    private static async Task<IResult> UpdateProfile(HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "user"))
        {
            return Results.BadRequest();
        }

        var userId = SignedIn.UserId(context)!.Value;
        using var connection = database.Open();
        if (UserStore.Find(connection, userId) is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        var password = PostedForm.Optional(form, context.Request.Query, "user[password]");
        UserStore.UpdateProfile(
            connection,
            Attachments.FilesRoot(database.Path),
            userId,
            new ProfileUpdate(
                PostedForm.Has(form, context.Request.Query, "user[name]"),
                PostedForm.Optional(form, context.Request.Query, "user[name]"),
                PostedForm.Has(form, context.Request.Query, "user[email_address]"),
                EmptyToNull(PostedForm.Optional(form, context.Request.Query, "user[email_address]")),
                PostedForm.Has(form, context.Request.Query, "user[password]"),
                password,
                PostedForm.Has(form, context.Request.Query, "user[bio]"),
                PostedForm.Optional(form, context.Request.Query, "user[bio]")),
            await PostedForm.File(form, "user[avatar]"));
        return Responses.Redirect(context, "/users/me/profile");
    }

    private static IResult ShowSidebar(HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        var sidebar = UserStore.Sidebar(connection, userId);
        return Responses.Html(RenderSidebar(sidebar.Directs, sidebar.Others, sidebar.Placeholders));
    }

    private static IResult ShowAvatar(string userId, HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        var id = RailsMessages.ReadRecordId(userId, RailsMessages.AvatarPurpose, Secret(context));
        if (id is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        using var connection = database.Open();
        var user = UserStore.Find(connection, id.Value);
        if (user is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        var stored = Attachments.Read(
            connection,
            Attachments.FilesRoot(database.Path),
            Attachments.UserType,
            user.Value.Id,
            Attachments.AvatarName);
        if (stored is not null)
        {
            return Responses.InlineBytes(stored.Value.Bytes, stored.Value.ContentType);
        }

        if (user.Value.Role == UserStore.BotRole)
        {
            return Results.Content(BotAvatarSvg, "image/svg+xml");
        }

        return Results.Content(InitialsSvg(UserStore.Initials(user.Value.Name)), "image/svg+xml");
    }

    private static IResult DestroyAvatar(HttpContext context, CampfireDatabase database)
    {
        var required = RequireUser(context);
        if (required is not null)
        {
            return required;
        }

        using var connection = database.Open();
        var userId = SignedIn.UserId(context)!.Value;
        Attachments.Destroy(
            connection,
            Attachments.FilesRoot(database.Path),
            Attachments.UserType,
            userId,
            Attachments.AvatarName);
        return Responses.Redirect(context, "/users/me/profile");
    }

    private static IResult CreateBan(long id, HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        if (UserStore.Find(connection, id) is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        if (!UserStore.TryBan(connection, id))
        {
            // Ban validation failed and the transaction rolled back (Rails RecordInvalid, unhandled → 500).
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        return Responses.Redirect(context, "/users/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IResult DestroyBan(long id, HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        if (UserStore.Find(connection, id) is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        UserStore.Unban(connection, id);
        return Responses.Redirect(context, "/users/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IResult? RedirectIfSignedIn(HttpContext context)
    {
        return SignedIn.UserId(context) is null ? null : Responses.Redirect(context, "/");
    }

    private static IResult? RequireUser(HttpContext context)
    {
        return SignedIn.UserId(context) is null ? Responses.Redirect(context, "/session/new") : null;
    }

    private static IResult? RequireAdmin(HttpContext context, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var id = SignedIn.UserId(context);
        if (id is null)
        {
            return Responses.Redirect(context, "/session/new");
        }

        return UserStore.CanAdminister(connection, id.Value)
            ? null
            : Responses.Empty(StatusCodes.Status403Forbidden);
    }

    private static IResult? RejectJoinCode(Microsoft.Data.Sqlite.SqliteConnection connection, string joinCode)
    {
        var account = Accounts.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        return string.Equals(account.Value.JoinCode, joinCode, StringComparison.Ordinal)
            ? null
            : Responses.Empty(StatusCodes.Status404NotFound);
    }

    private static void WriteSession(HttpContext context, string token)
    {
        SessionCookie.Write(context.Response, token, Secret(context));
    }

    private static string Secret(HttpContext context)
    {
        return SecretKey.Resolve(context.RequestServices.GetRequiredService<IConfiguration>());
    }

    private static string? UserAgent(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        return string.IsNullOrEmpty(agent) ? null : agent;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string RenderJoin(string accountName, string joinCode)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Sign up</title></head>
            <body class="signup">
            <form action="/join/{Responses.Encode(joinCode)}" method="post">
              <strong>{Responses.Encode(accountName)}</strong>
              <label>Name <input name="user[name]" autocomplete="name" placeholder="Name" required autofocus></label>
              <label>Email address <input type="email" name="user[email_address]" autocomplete="username" placeholder="Email address" required></label>
              <label>Password <input type="password" name="user[password]" autocomplete="new-password" placeholder="Password" maxlength="72" required></label>
              <label>Avatar <input type="file" name="user[avatar]" accept="image/*"></label>
              <button type="submit">Save</button>
            </form>
            </body>
            </html>
            """;
    }

    private static string RenderUser(UserRow user, bool admin, bool self)
    {
        var email = admin && user.EmailAddress is not null
            ? $"<div><a href=\"mailto:{Responses.Encode(user.EmailAddress)}\">{Responses.Encode(user.EmailAddress)}</a></div>"
            : "";
        var edit = self ? "<a href=\"/users/me/profile\">Edit my profile</a>" : "";
        var body = user.Status == UserStore.DeactivatedStatus
            ? $"<h1>{Responses.Encode(user.Name)}</h1><div>{Responses.Encode(user.Name)} is no longer on this account</div>"
            : $"""
                <h1>{Responses.Encode(user.Name)}</h1>
                {email}
                <div>{Responses.Encode(user.Bio)}</div>
                """;
        var ban = "";
        if (admin && !self && user.Status != UserStore.DeactivatedStatus)
        {
            var id = user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ban = user.Status == UserStore.ActiveStatus
                ? $"<form method=\"post\" action=\"/users/{id}/ban\"><button type=\"submit\">Ban {Responses.Encode(user.Name)}</button></form>"
                : $"<p>Banned</p><form method=\"post\" action=\"/users/{id}/ban\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button type=\"submit\">Remove ban</button></form>";
        }

        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>{Responses.Encode(user.Name)}</title></head>
            <body>
            {edit}
            <section class="{(user.Status == UserStore.BannedStatus ? "banned" : "")}">
            {body}
            {ban}
            </section>
            </body>
            </html>
            """;
    }

    private static string RenderProfile(UserRow user, string avatarToken)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>{Responses.Encode(user.Name)}</title></head>
            <body>
            <form action="/session" method="post"><input type="hidden" name="_method" value="delete"><button type="submit">Log out</button></form>
            <img src="/users/{Responses.Encode(avatarToken)}/avatar" alt="">
            <form action="/users/me/avatar" method="post"><input type="hidden" name="_method" value="delete"><button type="submit">Delete avatar</button></form>
            <form action="/users/me/profile" method="post">
              <input type="hidden" name="_method" value="patch">
              <label>Name <input name="user[name]" value="{Responses.Encode(user.Name)}" autocomplete="name" required></label>
              <label>Email address <input type="email" name="user[email_address]" value="{Responses.Encode(user.EmailAddress)}" autocomplete="username"></label>
              <label>Password <input type="password" name="user[password]" autocomplete="new-password" maxlength="72"></label>
              <label>Bio <textarea name="user[bio]">{Responses.Encode(user.Bio)}</textarea></label>
              <label>Avatar <input type="file" name="user[avatar]" accept="image/*"></label>
              <button type="submit">Save</button>
            </form>
            </body>
            </html>
            """;
    }

    private static string RenderSidebar(
        IReadOnlyList<SidebarRoom> directs,
        IReadOnlyList<SidebarRoom> others,
        IReadOnlyList<string> placeholders)
    {
        var directHtml = string.Concat(directs.Select(room =>
            $"<div class=\"direct{(room.Unread ? " unread" : "")}\">{Responses.Encode(room.Name)}</div>"));
        var otherHtml = string.Concat(others.Select(room =>
            $"<div class=\"room{(room.Unread ? " unread" : "")}\">{Responses.Encode(room.Name)}</div>"));
        var placeholderHtml = string.Concat(placeholders.Select(name =>
            $"<div class=\"direct-placeholder\">{Responses.Encode(name)}</div>"));
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Sidebar</title></head>
            <body>
            <div id="direct_rooms">{directHtml}{placeholderHtml}</div>
            <div id="shared_rooms">{otherHtml}</div>
            </body>
            </html>
            """;
    }

    private static string InitialsSvg(string initials)
    {
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" class="avatar">
              <text x="50%" y="50%" text-anchor="middle" dy="0.35em">{Responses.Encode(initials)}</text>
            </svg>
            """;
    }

    private const string BotAvatarSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 18 18"><rect width="18" height="18"/></svg>
        """;
}
