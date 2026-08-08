using Common.Mediator;
using Compendio.Application.Common;
using Compendio.Application.Pages;

namespace Compendio.Api.Endpoints;

/// <summary>
/// Pages. Binding and dispatch only — every decision is in a handler.
/// </summary>
/// <remarks>
/// <para>
/// The catch-all <c>{*path}</c> routes carry a content-relative path with forward slashes, which is
/// the same shape used everywhere else in the product. Validation happens in <c>PathPolicy</c> via
/// the handlers, not here: an endpoint that validated paths would be the second implementation of
/// the rules.
/// </para>
/// <para>
/// Sub-resource actions (<c>move</c>, <c>checkbox</c>, <c>versions</c>, <c>diff</c>,
/// <c>backlinks</c>) sit on their own literal routes with the path as a parameter, rather than as
/// <c>/pages/{*path}/move</c>. A catch-all has to be the last thing in a route template, so the
/// nested form never matches and returns 405 — the spec's route table assumed otherwise. Literal
/// segments outrank the catch-all in routing precedence, so <c>/pages/move</c> is unambiguous
/// against a page whose path is <c>move.md</c>.
/// </para>
/// </remarks>
public static class PageEndpoints
{
    public static void MapPages(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pages").RequireAuthorization().WithTags("Pages");

        group.MapPost("/move", async (MovePageRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new MovePageCommand(request.Path, request.TargetPath), ct)))
            .WithName("MovePage");

        group.MapPost("/checkbox", async (CheckboxRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(
                    new ToggleCheckboxCommand(request.Path, request.Offset, request.Checked, request.ExpectedHash), ct)))
            .WithName("ToggleCheckbox")
            .WithSummary("Ticks a checklist item from read mode. A byte substitution, not a re-serialization.");

        group.MapPost("/title", async (SetPageTitleRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SetPageTitleCommand(request.Path, request.Title), ct)))
            .WithName("SetPageTitle")
            .WithSummary("Changes a page's title in front matter, leaving the body and the file name unchanged.");

        group.MapGet("/backlinks", async (string path, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new Application.Search.GetBacklinksQuery(path), ct)))
            .WithName("GetBacklinks");

        group.MapGet("/{*path}", async (string path, bool? raw, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetPageQuery(path, raw ?? false), ct)))
            .WithName("GetPage")
            .WithSummary("Reads a page, its rendered HTML and its metadata.");

        group.MapPost("/", async (CreatePageRequest request, ISender sender, CancellationToken ct) =>
            {
                var page = await sender.Send(new CreatePageCommand(
                    request.FolderPath ?? string.Empty,
                    request.Title,
                    request.Content,
                    request.TemplateId,
                    request.Lang,
                    request.TranslationKey), ct);

                return Results.Created($"/api/v1/pages/{page.Path}", page);
            })
            .WithName("CreatePage");

        group.MapPut("/{*path}", async (string path, SavePageRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(
                    new SavePageCommand(path, request.Content, request.ExpectedHash, request.Normalized, request.Note,
                        request.MaterialRevision), ct)))
            .WithName("SavePage")
            .WithSummary("Saves a page. The expected hash is required; a mismatch is a 409 carrying both versions.");

        group.MapDelete("/{*path}", async (string path, ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new DeletePageCommand(path), ct);
                return Results.NoContent();
            })
            .WithName("DeletePage");
    }
}

public sealed record CreatePageRequest(
    string? FolderPath,
    string Title,
    string? Content,
    string? TemplateId,
    string? Lang,
    string? TranslationKey);

/// <param name="MaterialRevision">
/// The editor's explicit "everyone needs to read this again". Default off, so an ordinary save never
/// re-opens an acknowledgment.
/// </param>
public sealed record SavePageRequest(
    string Content,
    string ExpectedHash,
    bool Normalized = false,
    string? Note = null,
    bool MaterialRevision = false);

public sealed record MovePageRequest(string Path, string TargetPath);

public sealed record SetPageTitleRequest(string Path, string Title);

public sealed record MoveFolderRequest(string Path, string TargetPath);

public sealed record CheckboxRequest(string Path, int Offset, bool Checked, string ExpectedHash);
