using Common.Mediator;
using Compendio.Application.Attachments;
using Compendio.Application.Folders;
using Compendio.Application.Search;
using Compendio.Application.Tree;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Api.Endpoints;

/// <summary>Tree, folders, attachments and search — the read surfaces around pages.</summary>
public static class ContentEndpoints
{
    public static void MapTree(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/tree", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetTreeQuery(), ct)))
            .RequireAuthorization()
            .WithTags("Tree")
            .WithName("GetTree")
            .WithSummary("The navigation tree, filtered by the evaluator. Nodes at 'none' are absent, not greyed out.");

    public static void MapFolders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/folders").RequireAuthorization().WithTags("Folders");

        group.MapPost("/", async (CreateFolderRequest request, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new CreateFolderCommand(request.ParentPath ?? string.Empty, request.Name), ct)));

        // Literal route rather than /{*path}/move: a catch-all must end a route template.
        group.MapPost("/move", async (MoveFolderRequest request, ISender sender, CancellationToken ct) =>
        {
            await sender.Send(new MoveFolderCommand(request.Path, request.TargetPath), ct);
            return Results.NoContent();
        });

        group.MapDelete("/{*path}", async (string path, ISender sender, CancellationToken ct) =>
        {
            await sender.Send(new DeleteFolderCommand(path), ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Attachments, served through an authorized endpoint and never as static files.
    /// </summary>
    /// <remarks>
    /// <c>no-store</c> and no <c>ETag</c>: an ETag derived from plaintext would let a cache confirm
    /// the contents of a secure file to somebody who cannot read it.
    /// </remarks>
    public static void MapAttachments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/attachments").RequireAuthorization().WithTags("Attachments");

        group.MapGet("/{*path}", async (string path, ISender sender, HttpContext http, CancellationToken ct) =>
        {
            var content = await sender.Send(new GetAttachmentQuery(path), ct);

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.ContentDisposition =
                $"{(content.Inline ? "inline" : "attachment")}; filename=\"{content.FileName}\"";

            return Results.File(content.Bytes, content.ContentType, enableRangeProcessing: false);
        });

        group.MapPost("/", async (HttpRequest request, ISender sender, IOptions<CompendioOptions> options, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    return Results.BadRequest();
                }

                var form = await request.ReadFormAsync(ct);
                var pagePath = form["pagePath"].ToString();
                var file = form.Files.GetFile("file");

                if (file is null || string.IsNullOrWhiteSpace(pagePath))
                {
                    return Results.BadRequest();
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);

                return Results.Ok(await sender.Send(
                    new UploadAttachmentCommand(pagePath, file.FileName, buffer.ToArray()), ct));
            })
            .DisableAntiforgery(); // SameSite=Strict cookies are the CSRF posture; there is no token machinery.

        group.MapDelete("/{*path}", async (string path, ISender sender, CancellationToken ct) =>
        {
            await sender.Send(new DeleteAttachmentCommand(path), ct);
            return Results.NoContent();
        });
    }

    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Search");

        group.MapGet("/search", async (string? q, int? page, int? pageSize, string? @in, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SearchPagesQuery(q ?? string.Empty, page ?? 1, pageSize ?? 20, @in), ct)))
            .WithName("Search");

        group.MapGet("/search/suggest", async (string? q, int? limit, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SuggestQuery(q ?? string.Empty, limit ?? 10), ct)))
            .WithName("Suggest");

        // The link autocomplete the editor uses. Same predicate as search, or the editor becomes a
        // page-name oracle for every restricted folder.
        group.MapGet("/links/suggest", async (string? q, int? limit, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SuggestQuery(q ?? string.Empty, limit ?? 10), ct)))
            .WithName("SuggestLinks");

        group.MapGet("/tags", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetTagsQuery(), ct)))
            .WithName("GetTags");

        group.MapGet("/recent", async (int? limit, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new RecentlyUpdatedQuery(limit ?? 10), ct)))
            .WithName("GetRecentlyUpdated");
    }
}

public sealed record CreateFolderRequest(string? ParentPath, string Name);
