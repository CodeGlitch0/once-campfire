using System.Net;
using System.Net.Http.Headers;
using Campfire.Web.Data;
using Campfire.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using SessionStore = Campfire.Web.Identity.Sessions;

namespace Campfire.Web.Tests;

public sealed class JoinUsersAndAccountTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];
    private static readonly byte[] LogoBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x09, 0x08, 0x07, 0x06];

    [Fact]
    public async Task Join_WithValidCode_CreatesUser_AndInvalidCode_IsNotFound()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);

        string code;
        using (var connection = host.Open())
        {
            code = (string)Scalar(connection, "SELECT join_code FROM accounts")!;
        }

        using (var invalid = await client.GetAsync("/join/not"))
        {
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
            Assert.Empty(await invalid.Content.ReadAsByteArrayAsync());
        }

        using (var invalidPost = await client.PostAsync("/join/not", Form(
            ("user[name]", "Nope"),
            ("user[email_address]", "nope@example.com"),
            ("user[password]", "secret123456"))))
        {
            Assert.Equal(HttpStatusCode.NotFound, invalidPost.StatusCode);
            Assert.Empty(await invalidPost.Content.ReadAsByteArrayAsync());
        }

        using (var signedIn = await Send(client, HttpMethod.Get, "/join/" + code, adminCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal("/", signedIn.Headers.Location?.AbsolutePath);
        }

        using var created = await client.PostAsync("/join/" + code, Form(
            ("user[name]", "New Person"),
            ("user[email_address]", "new@37signals.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/", created.Headers.Location?.AbsolutePath);
        var memberCookie = Cookie(created);

        using var connectionAfter = host.Open();
        Assert.Equal(2L, Count(connectionAfter, "users"));
        Assert.Equal("New Person", Scalar(connectionAfter, "SELECT name FROM users WHERE email_address = 'new@37signals.com'"));
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT role FROM users WHERE email_address = 'new@37signals.com'"));
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT status FROM users WHERE email_address = 'new@37signals.com'"));
        var digest = (string)Scalar(connectionAfter, "SELECT password_digest FROM users WHERE email_address = 'new@37signals.com'")!;
        Assert.StartsWith("$2a$12$", digest, StringComparison.Ordinal);
        Assert.True(PasswordDigest.Verify("secret123456", digest));
        Assert.Equal(1L, Scalar(connectionAfter, """
            SELECT COUNT(*) FROM memberships
            WHERE user_id = (SELECT id FROM users WHERE email_address = 'new@37signals.com')
              AND room_id = (SELECT id FROM rooms WHERE name = 'All Talk' AND type = 'Rooms::Open')
            """));
        Assert.Equal(2L, Count(connectionAfter, "sessions"));
        Assert.False(string.IsNullOrEmpty(RailsMessages.ReadSessionToken(memberCookie, SecretKey.Default)));

        using (var duplicate = await client.PostAsync("/join/" + code, Form(
            ("user[name]", "Another"),
            ("user[email_address]", "new@37signals.com"),
            ("user[password]", "secret123456"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, duplicate.StatusCode);
            Assert.Equal("/session/new", duplicate.Headers.Location?.AbsolutePath);
            var query = duplicate.Headers.Location?.Query ?? "";
            Assert.Contains("email_address=", query, StringComparison.Ordinal);
            Assert.True(
                query.Contains("new%4037signals.com", StringComparison.OrdinalIgnoreCase)
                || query.Contains("new@37signals.com", StringComparison.Ordinal));
        }

        Assert.Equal(2L, Count(connectionAfter, "users"));
    }

    [Fact]
    public async Task JoinCode_Rotate_ChangesCode_AndOldCodeStopsWorking()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");

        var oldCode = await JoinCode(host);
        using (var forbidden = await Send(client, HttpMethod.Post, "/account/join_code", memberCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        Assert.Equal(oldCode, await JoinCode(host));

        using var rotated = await Send(client, HttpMethod.Post, "/account/join_code", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, rotated.StatusCode);
        Assert.Equal("/account/edit", rotated.Headers.Location?.AbsolutePath);

        var newCode = await JoinCode(host);
        Assert.NotEqual(oldCode, newCode);
        Assert.Matches("^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$", newCode);

        using var oldGet = await client.GetAsync("/join/" + oldCode);
        Assert.Equal(HttpStatusCode.NotFound, oldGet.StatusCode);
        using var oldPost = await client.PostAsync("/join/" + oldCode, Form(
            ("user[name]", "Late"),
            ("user[email_address]", "late@example.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.NotFound, oldPost.StatusCode);

        using var fresh = await client.GetAsync("/join/" + newCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        using var connection = host.Open();
        Assert.Equal(2L, Count(connection, "users"));
    }

    [Fact]
    public async Task Ban_CreateAndDestroy_MatchesAdministratorEffect_AndRejectsMembers()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com", "203.0.113.1");
        var otherCookie = await Join(client, await JoinCode(host), "Other", "other@example.com");
        long memberId;
        using (var connection = host.Open())
        {
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
            SessionStore.Start(connection, null, memberId, "203.0.113.2", "Test");
        }

        using (var forbidden = await Send(client, HttpMethod.Post, "/users/" + memberId + "/ban", memberCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
            Assert.Equal(0L, Count(connection, "bans"));
        }

        using var banned = await Send(client, HttpMethod.Post, "/users/" + memberId + "/ban", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, banned.StatusCode);
        Assert.Equal("/users/" + memberId, banned.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal(2L, Scalar(connection, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
            Assert.Equal(2L, Count(connection, "bans"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM bans WHERE ip_address = '203.0.113.1' AND user_id = $id", ("$id", memberId)));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM bans WHERE ip_address = '203.0.113.2' AND user_id = $id", ("$id", memberId)));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sessions WHERE user_id = $id", ("$id", memberId)));
        }

        using (var blocked = await Send(client, HttpMethod.Post, "/session", cookie: null, form: Form(
            ("email_address", "nobody@example.com"),
            ("password", "secret123456")), forwardedFor: "203.0.113.1"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        }

        using (var memberUnban = await Send(client, HttpMethod.Delete, "/users/" + memberId + "/ban", otherCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberUnban.StatusCode);
        }

        using var unbanned = await Send(client, HttpMethod.Delete, "/users/" + memberId + "/ban", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, unbanned.StatusCode);
        Assert.Equal("/users/" + memberId, unbanned.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
            Assert.Equal(0L, Count(connection, "bans"));
        }

        using var allowed = await Send(client, HttpMethod.Post, "/session", cookie: null, form: Form(
            ("email_address", "nobody@example.com"),
            ("password", "secret123456")), forwardedFor: "203.0.113.1");
        Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
    }

    [Fact]
    public async Task Avatar_Show_ReturnsStoredBytes_AndDestroyRemovesThem()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member Person", "member@example.com");
        long adminId;
        long memberId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users WHERE role = 1")!;
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
        }

        var adminToken = RailsMessages.SignRecordId(adminId, RailsMessages.AvatarPurpose, SecretKey.Default);

        using (var missing = await Send(client, HttpMethod.Get, "/users/not-a-valid-token/avatar", adminCookie))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var anonymous = await client.GetAsync("/users/" + adminToken + "/avatar"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Equal("/session/new", anonymous.Headers.Location?.AbsolutePath);
        }

        using (var uploaded = await Send(
            client,
            HttpMethod.Put,
            "/users/me/profile",
            adminCookie,
            Multipart("user[avatar]", "avatar.png", "image/png", PngBytes)))
        {
            Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
            Assert.Equal("/users/me/profile", uploaded.Headers.Location?.AbsolutePath);
        }

        using (var shown = await Send(client, HttpMethod.Get, "/users/" + adminToken + "/avatar", memberCookie))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.Equal("image/png", shown.Content.Headers.ContentType?.MediaType);
            Assert.Equal(PngBytes, await shown.Content.ReadAsByteArrayAsync());
        }

        using (var stolen = await Send(client, HttpMethod.Delete, "/users/" + adminId + "/avatar", memberCookie))
        {
            Assert.Equal(HttpStatusCode.Redirect, stolen.StatusCode);
        }

        using (var stillThere = await Send(client, HttpMethod.Get, "/users/" + adminToken + "/avatar", adminCookie))
        {
            Assert.Equal(PngBytes, await stillThere.Content.ReadAsByteArrayAsync());
        }

        using var removed = await Send(client, HttpMethod.Delete, "/users/" + memberId + "/avatar", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal("/users/me/profile", removed.Headers.Location?.AbsolutePath);

        using var after = await Send(client, HttpMethod.Get, "/users/" + adminToken + "/avatar", adminCookie);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("image/svg+xml", after.Content.Headers.ContentType?.MediaType);
        Assert.Contains(">A<", await after.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var connectionAfter = host.Open();
        Assert.Equal(0L, Count(connectionAfter, "active_storage_attachments"));
        Assert.Equal(0L, Count(connectionAfter, "active_storage_blobs"));
    }

    [Fact]
    public async Task Logo_Show_ReturnsStoredBytes_AndDestroyRemovesThem()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        await Join(client, await JoinCode(host), "Member", "member@example.com");
        var memberCookie = CookieFromLogin(client, "member@example.com");

        using (var stock = await client.GetAsync("/account/logo"))
        {
            Assert.Equal(HttpStatusCode.OK, stock.StatusCode);
            Assert.Equal("image/png", stock.Content.Headers.ContentType?.MediaType);
            Assert.NotEqual(LogoBytes, await stock.Content.ReadAsByteArrayAsync());
        }

        using (var uploaded = await Send(
            client,
            HttpMethod.Put,
            "/account",
            adminCookie,
            Multipart("account[logo]", "logo.png", "image/png", LogoBytes)))
        {
            Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
            Assert.Equal("/account/edit", uploaded.Headers.Location?.AbsolutePath);
        }

        using (var shown = await client.GetAsync("/account/logo"))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.Equal("image/png", shown.Content.Headers.ContentType?.MediaType);
            Assert.Equal(LogoBytes, await shown.Content.ReadAsByteArrayAsync());
        }

        var member = await memberCookie;
        using (var forbidden = await Send(client, HttpMethod.Delete, "/account/logo", member))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var still = await client.GetAsync("/account/logo"))
        {
            Assert.Equal(LogoBytes, await still.Content.ReadAsByteArrayAsync());
        }

        using var removed = await Send(client, HttpMethod.Delete, "/account/logo", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal("/account/edit", removed.Headers.Location?.AbsolutePath);

        using var after = await client.GetAsync("/account/logo");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(LogoBytes, await after.Content.ReadAsByteArrayAsync());

        using var connection = host.Open();
        Assert.Equal(0L, Count(connection, "active_storage_attachments"));
    }

    [Fact]
    public async Task CustomStyles_Update_IsAdminOnly()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");

        using (var forbiddenEdit = await Send(client, HttpMethod.Get, "/account/custom_styles/edit", memberCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenEdit.StatusCode);
        }

        using (var forbidden = await Send(client, HttpMethod.Put, "/account/custom_styles", memberCookie, Form(
            ("account[custom_styles]", ":root { --color-text: red; }"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(DBNull.Value, Scalar(connection, "SELECT custom_styles FROM accounts") ?? DBNull.Value);
        }

        using var updated = await Send(client, HttpMethod.Put, "/account/custom_styles", adminCookie, Form(
            ("account[custom_styles]", ":root { --color-text: red; }")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal("/account/custom_styles/edit", updated.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal(":root { --color-text: red; }", Scalar(connection, "SELECT custom_styles FROM accounts"));
        }

        using var edit = await Send(client, HttpMethod.Get, "/account/custom_styles/edit", adminCookie);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Contains(":root { --color-text: red; }", await edit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_Show_IsCurrentUser_EvenWhenUrlNamesAnotherUser()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        await FirstRun(client, "Admin", "admin@example.com");
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");
        long adminId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'admin@example.com'")!;
        }

        using (var anonymous = await client.GetAsync("/users/me/profile"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Equal("/session/new", anonymous.Headers.Location?.AbsolutePath);
        }

        using var shown = await Send(client, HttpMethod.Get, "/users/" + adminId + "/profile", memberCookie);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        var html = await shown.Content.ReadAsStringAsync();
        Assert.Contains("value=\"Member\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"member@example.com\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"Admin\"", html, StringComparison.Ordinal);

        using var updated = await Send(client, HttpMethod.Put, "/users/" + adminId + "/profile", memberCookie, Form(
            ("user[name]", "Renamed"),
            ("user[bio]", "Acrobat")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal("/users/me/profile", updated.Headers.Location?.AbsolutePath);

        using var connectionAfter = host.Open();
        Assert.Equal("Admin", Scalar(connectionAfter, "SELECT name FROM users WHERE email_address = 'admin@example.com'"));
        Assert.Equal("Renamed", Scalar(connectionAfter, "SELECT name FROM users WHERE email_address = 'member@example.com'"));
        Assert.Equal("Acrobat", Scalar(connectionAfter, "SELECT bio FROM users WHERE email_address = 'member@example.com'"));
    }

    [Fact]
    public async Task AccountEdit_Update_IsAdminOnly()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");

        using (var anonymous = await client.GetAsync("/account/edit"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Equal("/session/new", anonymous.Headers.Location?.AbsolutePath);
        }

        using (var memberEdit = await Send(client, HttpMethod.Get, "/account/edit", memberCookie))
        {
            Assert.Equal(HttpStatusCode.OK, memberEdit.StatusCode);
        }

        using (var forbidden = await Send(client, HttpMethod.Put, "/account", memberCookie, Form(("account[name]", "Different"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal("Campfire", Scalar(connection, "SELECT name FROM accounts"));
        }

        using var updated = await Send(client, HttpMethod.Put, "/account", adminCookie, Form(("account[name]", "Different")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal("/account/edit", updated.Headers.Location?.AbsolutePath);

        using var connectionAfter = host.Open();
        Assert.Equal("Different", Scalar(connectionAfter, "SELECT name FROM accounts"));
    }

    [Fact]
    public async Task AccountUsers_RoleChangeAndDeactivate_AreAdminOnly()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");
        long memberId;
        using (var connection = host.Open())
        {
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
        }

        using (var forbiddenRole = await Send(client, HttpMethod.Put, "/account/users/" + memberId, memberCookie, Form(("user[role]", "administrator"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRole.StatusCode);
        }

        using (var forbiddenDelete = await Send(client, HttpMethod.Delete, "/account/users/" + memberId, memberCookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT role FROM users WHERE id = $id", ("$id", memberId)));
            Assert.Equal(0L, Scalar(connection, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
        }

        using var promoted = await Send(client, HttpMethod.Put, "/account/users/" + memberId, adminCookie, Form(("user[role]", "administrator")));
        Assert.Equal(HttpStatusCode.Redirect, promoted.StatusCode);
        Assert.Equal("/account/edit", promoted.Headers.Location?.AbsolutePath);
        using (var connection = host.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT role FROM users WHERE id = $id", ("$id", memberId)));
        }

        using var demoted = await Send(client, HttpMethod.Put, "/account/users/" + memberId, adminCookie, Form(("user[role]", "owner")));
        Assert.Equal(HttpStatusCode.Redirect, demoted.StatusCode);
        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT role FROM users WHERE id = $id", ("$id", memberId)));
        }

        using var removed = await Send(client, HttpMethod.Delete, "/account/users/" + memberId, adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal("/account/edit", removed.Headers.Location?.AbsolutePath);

        using var connectionAfter = host.Open();
        Assert.Equal(1L, Scalar(connectionAfter, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT COUNT(*) FROM users WHERE status = 0 AND id = $id", ("$id", memberId)));
        var email = (string)Scalar(connectionAfter, "SELECT email_address FROM users WHERE id = $id", ("$id", memberId))!;
        Assert.Matches("^member-deactivated-[0-9a-f-]{36}@example.com$", email);
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT COUNT(*) FROM sessions WHERE user_id = $id", ("$id", memberId)));
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT COUNT(*) FROM memberships WHERE user_id = $id", ("$id", memberId)));
    }

    [Fact]
    public async Task Sidebar_AndUserShow_RequireSignIn()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client, "Admin Person", "admin@example.com");
        long adminId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users WHERE role = 1")!;
        }

        using (var sidebar = await client.GetAsync("/users/me/sidebar"))
        {
            Assert.Equal(HttpStatusCode.Redirect, sidebar.StatusCode);
            Assert.Equal("/session/new", sidebar.Headers.Location?.AbsolutePath);
        }

        using (var shown = await Send(client, HttpMethod.Get, "/users/me/sidebar", adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.Contains("All Talk", await shown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var user = await client.GetAsync("/users/" + adminId))
        {
            Assert.Equal(HttpStatusCode.Redirect, user.StatusCode);
        }

        using var page = await Send(client, HttpMethod.Get, "/users/" + adminId, adminCookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Admin Person", html, StringComparison.Ordinal);
        Assert.Contains("mailto:admin@example.com", html, StringComparison.Ordinal);

        var memberCookie = await Join(client, await JoinCode(host), "Member", "member@example.com");
        using var hidden = await Send(client, HttpMethod.Get, "/users/" + adminId, memberCookie);
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.DoesNotContain("mailto:", await hidden.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostBan_WithMethodDelete_Unbans()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        await Join(client, await JoinCode(host), "Member", "member@example.com", "203.0.113.9");
        long memberId;
        using (var connection = host.Open())
        {
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
        }

        using var banned = await Send(client, HttpMethod.Post, "/users/" + memberId + "/ban", adminCookie);
        Assert.Equal(HttpStatusCode.Redirect, banned.StatusCode);

        using var unbanned = await Send(
            client,
            HttpMethod.Post,
            "/users/" + memberId + "/ban",
            adminCookie,
            Form(("_method", "delete")));
        Assert.Equal(HttpStatusCode.Redirect, unbanned.StatusCode);
        Assert.Equal("/users/" + memberId, unbanned.Headers.Location?.AbsolutePath);

        using var connectionAfter = host.Open();
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT status FROM users WHERE id = $id", ("$id", memberId)));
        Assert.Equal(0L, Count(connectionAfter, "bans"));
    }

    [Fact]
    public async Task RoleCheckboxPair_InOnePost_StoresAdministrator()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        await Join(client, await JoinCode(host), "Member", "member@example.com");
        long memberId;
        using (var connection = host.Open())
        {
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
        }

        using var promoted = await Send(
            client,
            HttpMethod.Post,
            "/account/users/" + memberId,
            adminCookie,
            Form(
                ("_method", "patch"),
                ("user[role]", "member"),
                ("user[role]", "administrator")));
        Assert.Equal(HttpStatusCode.Redirect, promoted.StatusCode);
        Assert.Equal("/account/edit", promoted.Headers.Location?.AbsolutePath);

        using var connectionAfter = host.Open();
        Assert.Equal(1L, Scalar(connectionAfter, "SELECT role FROM users WHERE id = $id", ("$id", memberId)));

        using var edit = await Send(client, HttpMethod.Get, "/account/edit", adminCookie);
        var html = await edit.Content.ReadAsStringAsync();
        Assert.Contains("aria-label=\"Account name\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Upload logo\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"invite_url\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"invite_label\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Role\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"commit\"", html, StringComparison.Ordinal);
        Assert.Contains(">Save role</button>", html, StringComparison.Ordinal);

        using var styles = await Send(client, HttpMethod.Get, "/account/custom_styles/edit", adminCookie);
        Assert.Contains("aria-label=\"Custom CSS\"", await styles.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredHtmlAndSvg_AreNotServedInlineAsThoseTypes()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var adminCookie = await FirstRun(client);
        long adminId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users")!;
            Attachments.Replace(
                connection,
                Attachments.FilesRoot(host.DatabasePath),
                Attachments.UserType,
                adminId,
                Attachments.AvatarName,
                new BlobPayload("evil.html", "text/html", "<html><script>alert(1)</script></html>"u8.ToArray()));
        }

        var token = RailsMessages.SignRecordId(adminId, RailsMessages.AvatarPurpose, SecretKey.Default);
        using (var shown = await Send(client, HttpMethod.Get, "/users/" + token + "/avatar", adminCookie))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.NotEqual("text/html", shown.Content.Headers.ContentType?.MediaType);
            var body = await shown.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
            Assert.DoesNotContain("alert(1)", body, StringComparison.Ordinal);
        }

        using (var connection = host.Open())
        {
            Attachments.Replace(
                connection,
                Attachments.FilesRoot(host.DatabasePath),
                Attachments.UserType,
                adminId,
                Attachments.AvatarName,
                new BlobPayload("evil.svg", "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>stored-svg</script></svg>"u8.ToArray()));
            var accountId = (long)Scalar(connection, "SELECT id FROM accounts")!;
            Attachments.Replace(
                connection,
                Attachments.FilesRoot(host.DatabasePath),
                Attachments.AccountType,
                accountId,
                Attachments.LogoName,
                new BlobPayload("logo.html", "text/html", "<html>stored-logo</html>"u8.ToArray()));
        }

        using (var svg = await Send(client, HttpMethod.Get, "/users/" + token + "/avatar", adminCookie))
        {
            var body = await svg.Content.ReadAsStringAsync();
            Assert.DoesNotContain("stored-svg", body, StringComparison.Ordinal);
            Assert.NotEqual("text/html", svg.Content.Headers.ContentType?.MediaType);
        }

        using var logo = await client.GetAsync("/account/logo");
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("stored-logo", await logo.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("inline", logo.Content.Headers.ContentDisposition?.DispositionType);
    }

    private static async Task<string> FirstRun(HttpClient client, string name = "Admin", string email = "admin@example.com")
    {
        using var response = await client.PostAsync("/first_run", Form(
            ("user[name]", name),
            ("user[email_address]", email),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Cookie(response);
    }

    private static async Task<string> Join(HttpClient client, string code, string name, string email, string? forwardedFor = null)
    {
        using var response = await Send(client, HttpMethod.Post, "/join/" + code, cookie: null, form: Form(
            ("user[name]", name),
            ("user[email_address]", email),
            ("user[password]", "secret123456")), forwardedFor: forwardedFor);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Cookie(response);
    }

    private static async Task<string> JoinCode(Host host)
    {
        using var connection = host.Open();
        return (string)Scalar(connection, "SELECT join_code FROM accounts")!;
    }

    private static async Task<string> CookieFromLogin(HttpClient client, string email)
    {
        using var response = await client.PostAsync("/session", Form(
            ("email_address", email),
            ("password", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Cookie(response);
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie = null,
        HttpContent? form = null,
        string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "session_token=" + cookie);
        }

        if (!string.IsNullOrEmpty(forwardedFor))
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        if (form is not null)
        {
            request.Content = form;
        }

        return await client.SendAsync(request);
    }

    private static string Cookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var signed = SessionCookie.ValueFromSetCookieHeader(string.Join("\n", values!));
        Assert.False(string.IsNullOrEmpty(signed));
        return signed!;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
    }

    private static MultipartFormDataContent Multipart(string fieldName, string fileName, string contentType, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, fieldName, fileName);
        return content;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        return (long)Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\"")!;
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return command.ExecuteScalar();
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _path;
        private readonly WebApplicationFactory<Program> _factory;

        public Host()
        {
            _path = Path.Combine(Path.GetTempPath(), $"campfire-join-{Guid.NewGuid():N}.sqlite");
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Campfire:DatabasePath", _path);
            });
        }

        public string DatabasePath => _path;

        public HttpClient CreateClient()
        {
            return _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });
        }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={_path}");
            connection.Open();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            TryDelete(_path);
            TryDelete(_path + "-wal");
            TryDelete(_path + "-shm");
            var files = _path + ".files";
            if (Directory.Exists(files))
            {
                Directory.Delete(files, recursive: true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
