using Campfire.Web.Data;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using AccountStore = Campfire.Web.Identity.Accounts;
using UserStore = Campfire.Web.Identity.Users;

namespace Campfire.Web.Http;

/// <summary>
/// Account edit, user administration, join-code rotation, logo, and custom styles.
/// Update, ban-equivalent user changes, join-code reset, logo destroy, and custom styles
/// are administrator-only. Logo show is public. Account edit is any signed-in user.
/// </summary>
public static class AccountEndpoints
{
    private const int PageSize = 500;

    // Minimal stand-in for app/assets/images/logos/app-icon.png. Rails returns image/png;
    // variant resizing is not ported. Stored logos return their own bytes instead.
    private static readonly byte[] StockLogoPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static void Map(WebApplication app)
    {
        app.MapGet("/account/edit", EditAccount);
        app.MapMethods("/account", ["PUT", "PATCH"], UpdateAccount);

        app.MapGet("/account/users", IndexUsers);
        app.MapMethods("/account/users/{id:long}", ["PUT", "PATCH"], UpdateUser);
        app.MapDelete("/account/users/{id:long}", DestroyUser);

        app.MapPost("/account/join_code", CreateJoinCode);

        app.MapGet("/account/logo", ShowLogo);
        app.MapDelete("/account/logo", DestroyLogo);

        app.MapGet("/account/custom_styles/edit", EditCustomStyles);
        app.MapMethods("/account/custom_styles", ["PUT", "PATCH"], UpdateCustomStyles);
    }

    private static IResult EditAccount(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        var account = AccountStore.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        var viewerId = SignedIn.UserId(context)!.Value;
        var admin = UserStore.CanAdminister(connection, viewerId);
        var users = UserStore.List(connection, includeBanned: admin, PageSize, 0);
        var administrators = users.Where(user => user.Role == UserStore.AdministratorRole).ToList();
        var members = users.Where(user => user.Role != UserStore.AdministratorRole).ToList();
        var inviteUrl = $"{context.Request.Scheme}://{context.Request.Host}/join/{account.Value.JoinCode}";
        var logoAttached = Attachments.Attached(
            connection,
            Attachments.AccountType,
            account.Value.Id,
            Attachments.LogoName);
        return Responses.Html(RenderEdit(account.Value, admin, viewerId, administrators, members, inviteUrl, logoAttached));
    }

    private static async Task<IResult> UpdateAccount(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "account"))
        {
            return Results.BadRequest();
        }

        var account = AccountStore.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        var name = PostedForm.Optional(form, context.Request.Query, "account[name]");
        if (name is not null)
        {
            AccountStore.UpdateName(connection, account.Value.Id, name);
        }

        var logo = await PostedForm.File(form, "account[logo]");
        if (logo is not null)
        {
            Attachments.Replace(
                connection,
                Attachments.FilesRoot(database.Path),
                Attachments.AccountType,
                account.Value.Id,
                Attachments.LogoName,
                logo.Value);
            AccountStore.Touch(connection, account.Value.Id);
        }

        return Responses.Redirect(context, "/account/edit");
    }

    private static IResult IndexUsers(HttpContext context, CampfireDatabase database)
    {
        var denied = RequireUser(context);
        if (denied is not null)
        {
            return denied;
        }

        var page = 1;
        if (int.TryParse(context.Request.Query["page"], out var parsed) && parsed > 1)
        {
            page = parsed;
        }

        using var connection = database.Open();
        var users = UserStore.List(connection, includeBanned: false, PageSize, (page - 1) * PageSize);
        var items = string.Concat(users.Select(user => $"<li><strong>{Responses.Encode(user.Name)}</strong></li>"));
        return Responses.Html($"""
            <!DOCTYPE html>
            <html>
            <head><title>Users</title></head>
            <body><ul>{items}</ul></body>
            </html>
            """);
    }

    private static async Task<IResult> UpdateUser(long id, HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "user"))
        {
            return Results.BadRequest();
        }

        if (UserStore.FindActive(connection, id) is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        UserStore.SetRole(connection, id, PostedForm.Optional(form, context.Request.Query, "user[role]"));
        return Responses.Redirect(context, "/account/edit");
    }

    private static IResult DestroyUser(long id, HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        if (UserStore.FindActive(connection, id) is null)
        {
            return Responses.Empty(StatusCodes.Status404NotFound);
        }

        UserStore.Deactivate(connection, id);
        return Responses.Redirect(context, "/account/edit");
    }

    private static IResult CreateJoinCode(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var account = AccountStore.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        AccountStore.ResetJoinCode(connection, account.Value.Id);
        return Responses.Redirect(context, "/account/edit");
    }

    private static IResult ShowLogo(CampfireDatabase database)
    {
        using var connection = database.Open();
        var account = AccountStore.First(connection);
        if (account is not null)
        {
            var stored = Attachments.Read(
                connection,
                Attachments.FilesRoot(database.Path),
                Attachments.AccountType,
                account.Value.Id,
                Attachments.LogoName);
            if (stored is not null)
            {
                var image = ImageResponses.Logo(stored.Value.Bytes);
                if (image is not null)
                {
                    return image;
                }
            }
        }

        return Responses.InlineBytes(StockLogoPng, "image/png");
    }

    private static IResult DestroyLogo(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var account = AccountStore.First(connection);
        if (account is not null)
        {
            Attachments.Destroy(
                connection,
                Attachments.FilesRoot(database.Path),
                Attachments.AccountType,
                account.Value.Id,
                Attachments.LogoName);
        }

        return Responses.Redirect(context, "/account/edit");
    }

    private static IResult EditCustomStyles(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var account = AccountStore.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        return Responses.Html($"""
            <!DOCTYPE html>
            <html>
            <head><title>Custom styles</title></head>
            <body>
            <a href="/account/edit">Account</a>
            <h1>Custom CSS</h1>
            <p>Add custom CSS styles. Use Caution: you could break things.</p>
            <form action="/account/custom_styles" method="post">
              <input type="hidden" name="_method" value="patch">
              <textarea name="account[custom_styles]" aria-label="Custom CSS" rows="16">{Responses.Encode(account.Value.CustomStyles)}</textarea>
              <button type="submit">Save changes</button>
            </form>
            </body>
            </html>
            """);
    }

    private static async Task<IResult> UpdateCustomStyles(HttpContext context, CampfireDatabase database)
    {
        using var connection = database.Open();
        var denied = RequireAdmin(context, connection);
        if (denied is not null)
        {
            return denied;
        }

        var form = await PostedForm.Read(context.Request);
        if (!PostedForm.HasPrefix(form, context.Request.Query, "account"))
        {
            return Results.BadRequest();
        }

        var account = AccountStore.First(connection);
        if (account is null)
        {
            return Responses.Empty(StatusCodes.Status500InternalServerError);
        }

        if (PostedForm.Has(form, context.Request.Query, "account[custom_styles]"))
        {
            AccountStore.UpdateCustomStyles(
                connection,
                account.Value.Id,
                PostedForm.Optional(form, context.Request.Query, "account[custom_styles]"));
        }

        return Responses.Redirect(context, "/account/custom_styles/edit");
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

    private static string RenderEdit(
        AccountStore.AccountRow account,
        bool admin,
        long viewerId,
        IReadOnlyList<UserRow> administrators,
        IReadOnlyList<UserRow> members,
        string inviteUrl,
        bool logoAttached)
    {
        var adminItems = string.Concat(administrators.Select(user => UserItem(user, admin, viewerId)));
        var memberItems = string.Concat(members.Select(user => UserItem(user, admin, viewerId)));
        var divider = administrators.Count > 0 && members.Count > 0
            ? "<hr class=\"separator full-width\">"
            : "";
        var regenerate = admin
            ? "<form method=\"post\" action=\"/account/join_code\"><button type=\"submit\">Regenerate join link</button></form>"
            : "";
        var logoDelete = admin && logoAttached
            ? "<form method=\"post\" action=\"/account/logo\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button type=\"submit\">Delete logo</button></form>"
            : "";
        var nameField = admin
            ? $"""
                <form action="/account" method="post" enctype="multipart/form-data">
                  <input type="hidden" name="_method" value="patch">
                  <input name="account[name]" aria-label="Account name" value="{Responses.Encode(account.Name)}">
                  <input type="file" name="account[logo]" accept="image/*" aria-label="Upload logo">
                  <button type="submit">Save changes</button>
                </form>
                {logoDelete}
                """
            : $"<h1>{Responses.Encode(account.Name)}</h1>";
        return $"""
            <!DOCTYPE html>
            <html>
            <head><title>Account settings</title></head>
            <body>
            {(admin ? "<a href=\"/account/custom_styles/edit\">Custom styles</a>" : "")}
            {nameField}
            <p id="invite_label">Share to invite more people</p>
            <input id="invite_url" readonly aria-labelledby="invite_label" value="{Responses.Encode(inviteUrl)}">
            {regenerate}
            <turbo-frame id="account_users">
              {adminItems}
              {divider}
              {memberItems}
            </turbo-frame>
            </body>
            </html>
            """;
    }

    private static string UserItem(UserRow user, bool admin, long viewerId)
    {
        var id = user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var controls = "";
        if (admin && user.Status == UserStore.ActiveStatus && user.Role != UserStore.BotRole)
        {
            var checkedAdmin = user.Role == UserStore.AdministratorRole ? " checked" : "";
            controls = $"""
                <form action="/account/users/{id}" method="post">
                  <input type="hidden" name="_method" value="patch">
                  <input type="hidden" name="user[role]" value="member">
                  <input type="checkbox" name="user[role]" value="administrator" aria-label="Role"{checkedAdmin}>
                  <button type="submit" name="commit">Save role</button>
                </form>
                """;
        }

        // Rails button_to account_user_path, method: :delete — hidden for the current user.
        if (admin && user.Status == UserStore.ActiveStatus && user.Id != viewerId)
        {
            controls += $"<form method=\"post\" action=\"/account/users/{id}\"><input type=\"hidden\" name=\"_method\" value=\"delete\"><button type=\"submit\">Delete {Responses.Encode(user.Name)}</button></form>";
        }

        return $"""
            <li class="{(user.Status == UserStore.BannedStatus ? "banned" : "")}">
              <strong>{Responses.Encode(user.Name)}</strong>
              {controls}
            </li>
            """;
    }
}
