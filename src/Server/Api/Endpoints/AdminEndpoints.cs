using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Admin;
using Compendio.Domain.Security;

namespace Compendio.Api.Endpoints;

/// <summary>
/// Administration: users, groups, access rules, secure scopes, audit and status.
/// </summary>
/// <remarks>
/// The whole group requires the <c>Admin</c> role. The ACL endpoints are the exception: they need
/// <c>manage</c> on the folder, which an editor can hold, and the handlers enforce that themselves.
/// </remarks>
public static class AdminEndpoints
{
    public const string AdminPolicy = "admin";

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization(AdminPolicy).WithTags("Admin");

        // ---- Users -------------------------------------------------------------------------------
        admin.MapGet("/users", async (IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListAsync(ct)));

        admin.MapPost("/users", async (CreateUserBody body, IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.CreateAsync(new CreateUserRequest(
                body.UserName, body.Password, body.DisplayName, body.Email, body.Role, body.PreferredLanguage), ct)));

        admin.MapPut("/users/{id:guid}", async (Guid id, UpdateUserBody body, IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.UpdateAsync(id, body.DisplayName, body.Email, body.Role, body.Active, body.PreferredLanguage, ct)));

        admin.MapPost("/users/{id:guid}/password", async (Guid id, SetPasswordBody body, IAccountService accounts, CancellationToken ct) =>
        {
            await accounts.ChangePasswordAsync(id, null, body.NewPassword, requireCurrent: false, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/users/{id:guid}", async (Guid id, IAccountService accounts, CancellationToken ct) =>
        {
            await accounts.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // ---- Groups ------------------------------------------------------------------------------
        admin.MapGet("/groups", async (IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.ListGroupsAsync(ct)));

        admin.MapPost("/groups", async (CreateGroupBody body, IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.CreateGroupAsync(body.Name, ct)));

        admin.MapPut("/groups/{id:guid}", async (Guid id, UpdateGroupBody body, IAccountService accounts, CancellationToken ct) =>
            Results.Ok(await accounts.UpdateGroupAsync(id, body.Name, body.Active, body.MemberIds, ct)));

        admin.MapDelete("/groups/{id:guid}", async (Guid id, IAccountService accounts, CancellationToken ct) =>
        {
            await accounts.DeleteGroupAsync(id, ct);
            return Results.NoContent();
        });

        // ---- Secure scopes -----------------------------------------------------------------------
        admin.MapGet("/secure-scopes", async (ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new ListSecureScopesQuery(), ct)));

        admin.MapPost("/secure-scopes", async (CreateSecureScopeBody body, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new CreateSecureScopeCommand(body.Path, body.IndexContent, body.AllowAi), ct)));

        admin.MapPut("/secure-scopes/{*path}", async (string path, UpdateSecureScopeBody body, ISender sender, CancellationToken ct) =>
        {
            await sender.Send(new UpdateSecureScopeCommand(path, body.IndexContent, body.AllowAi), ct);
            return Results.NoContent();
        });

        // ---- Audit and status --------------------------------------------------------------------
        admin.MapGet("/audit", async (int? page, int? pageSize, string? action, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetAuditLogQuery(page ?? 1, pageSize ?? 50, action), ct)));

        admin.MapGet("/status", async (ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetStatusQuery(), ct)));

        admin.MapPost("/reindex", async (bool? dropSecure, ISearchIndex index, CancellationToken ct) =>
        {
            await index.RebuildAsync(progress: null, dropSecure ?? false, ct);
            return Results.NoContent();
        });

        admin.MapPost("/reconcile", async (Engine.Reconciler reconciler, CancellationToken ct) =>
            Results.Ok(await reconciler.RunAsync(ct)));

        // ---- Deleted pages -----------------------------------------------------------------------
        // The recovery the tombstones exist for. A page's versions outlive its file for the history
        // retention window; this is how an administrator gets the page back, history and all.
        admin.MapGet("/deleted-pages", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListDeletedPagesQuery(), ct)))
            .WithName("ListDeletedPages");

        admin.MapPost("/deleted-pages/{pageId:guid}/restore", async (Guid pageId, RestoreDeletedPageBody? body, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new RestoreDeletedPageCommand(pageId, body?.TargetPath), ct)))
            .WithName("RestoreDeletedPage")
            .WithSummary("Brings a deleted page back, with its history, where it was or at the given path.");

        // A server-side backup, written to the data directory's backups folder under a timestamped
        // name. The path is never taken from the caller — the API only ever writes here, so a
        // request cannot direct the archive somewhere it should not go.
        admin.MapPost("/backup", async (BackupBody? body, Hosting.DataDirectory dataDirectory, IServiceProvider services, CancellationToken ct) =>
        {
            Directory.CreateDirectory(dataDirectory.Backups);
            var name = $"compendio-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
            var output = Path.Combine(dataDirectory.Backups, name);

            var result = await Hosting.BackupCommand.CreateAsync(services, output, body?.Passphrase, ct);
            return Results.Ok(result);
        });

        // ---- Git mirror --------------------------------------------------------------------------
        admin.MapGet("/git-mirror", async (ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetGitMirrorStatusQuery(), ct)));

        admin.MapPost("/git-mirror/push", async (ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new PushGitMirrorCommand(), ct)));

        // ---- Access rules ------------------------------------------------------------------------
        // Outside the admin group: `manage` on a folder is enough, and an editor can hold it.
        var acl = app.MapGroup("/api/v1/acl").RequireAuthorization().WithTags("Admin");

        // Declared before the catch-all so the literal segment wins, and with the path as a query
        // parameter because a catch-all cannot carry a suffix.
        acl.MapGet("/effective", async (string path, Guid userId, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new EffectiveAccessQuery(path, userId), ct)));

        acl.MapGet("/{*path}", async (string path, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetAclQuery(path), ct)));

        acl.MapPut("/{*path}", async (string path, SetAclBody body, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new SetAclCommand(path, body.InheritParent, body.Entries), ct)));
    }
}

public sealed record CreateUserBody(string UserName, string Password, string DisplayName, string? Email, UserRole Role, string? PreferredLanguage);

public sealed record UpdateUserBody(string? DisplayName, string? Email, UserRole? Role, bool? Active, string? PreferredLanguage);

public sealed record SetPasswordBody(string NewPassword);

public sealed record CreateGroupBody(string Name);

public sealed record BackupBody(string? Passphrase);

public sealed record RestoreDeletedPageBody(string? TargetPath);

public sealed record UpdateGroupBody(string? Name, bool? Active, IReadOnlyList<Guid>? MemberIds);

public sealed record CreateSecureScopeBody(string Path, bool IndexContent = false, bool AllowAi = false);

public sealed record UpdateSecureScopeBody(bool? IndexContent, bool? AllowAi);

public sealed record SetAclBody(bool InheritParent, IReadOnlyList<SetAclEntry> Entries);
