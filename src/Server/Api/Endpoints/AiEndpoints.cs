using Common.Mediator;
using Compendio.Application.Admin;
using Compendio.Application.Ai;

namespace Compendio.Api.Endpoints;

/// <summary>
/// The AI assistant. Every action refuses with <c>404 ai.disabled</c> when nothing is configured.
/// </summary>
/// <remarks>
/// <para>
/// The routes are always mapped and <c>GET /ai/status</c> always answers. Mapping conditionally was
/// the alternative and it is worse in practice: mapping happens at startup and configuration happens
/// at runtime, so it would mean restarting the service after pasting a base URL into a form.
/// </para>
/// <para>
/// What the acceptance criterion actually tests is that with no provider configured every action
/// returns 404 and the client renders no AI control anywhere — which this shape satisfies, and which
/// the status endpoint is what makes possible.
/// </para>
/// </remarks>
public static class AiEndpoints
{
    public static void MapAi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai").RequireAuthorization().WithTags("AI");

        group.MapGet("/status", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetAiStatusQuery(), ct)))
            .WithName("GetAiStatus")
            .WithSummary("Always present. Reports enabled:false when no provider is configured.");

        group.MapPost("/improve", async (AiTextRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ImproveWritingCommand(request.Path, request.Text), ct)))
            .WithName("AiImproveWriting");

        group.MapPost("/summarize", async (AiTextRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SummarizePageCommand(request.Path, request.Text), ct)))
            .WithName("AiSummarize");

        group.MapPost("/freshness", async (AiPathRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new FreshnessHintsCommand(request.Path), ct)))
            .WithName("AiFreshnessHints");

        group.MapPost("/draft", async (AiDraftRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(
                    new DraftFromBulletsCommand(request.FolderPath ?? string.Empty, request.Bullets, request.TemplateId), ct)))
            .WithName("AiDraftFromBullets");

        group.MapPost("/translate", async (AiTranslateRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new TranslatePageCommand(request.Path, request.TargetLanguage), ct)))
            .WithName("AiTranslatePage")
            .WithSummary("Writes the sibling page badged machine-translated. The badge clears on a human save.");

        group.MapPost("/ask", async (AiAskRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new AskWikiQuery(request.Question), ct)))
            .WithName("AiAskWiki")
            .WithSummary("Answers from permission-filtered passages, citing only sources the caller may read.");

        var admin = app.MapGroup("/api/v1/admin/ai")
            .RequireAuthorization(AdminEndpoints.AdminPolicy)
            .WithTags("AI");

        admin.MapGet("/", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetAiSettingsQuery(), ct)))
            .WithName("GetAiSettings");

        admin.MapPut("/", async (AiSettingsRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SaveAiSettingsCommand(
                    request.BaseUrl, request.Model, request.ApiKey,
                    request.AllowedSpaces, request.DisabledFeatures,
                    request.DailyPerUser, request.DailyPerInstance), ct)))
            .WithName("SaveAiSettings")
            .WithSummary("Every field is optional; an omitted one is left as it was.");

        admin.MapDelete("/", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ClearAiSettingsCommand(), ct)))
            .WithName("ClearAiSettings")
            .WithSummary("Returns the instance to v0 behaviour: no AI affordance anywhere.");

        admin.MapPost("/test", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new TestAiConnectionCommand(), ct)))
            .WithName("TestAiConnection");
    }
}

public sealed record AiPathRequest(string Path);

/// <param name="Text">A selection. Absent, the whole page body is used.</param>
public sealed record AiTextRequest(string Path, string? Text);

public sealed record AiDraftRequest(string? FolderPath, string Bullets, string? TemplateId);

public sealed record AiTranslateRequest(string Path, string TargetLanguage);

public sealed record AiAskRequest(string Question);

/// <param name="ApiKey">Omit to leave the stored key alone; send an empty string to clear it.</param>
/// <param name="DailyPerUser">Requests one person may make in a rolling 24 hours. 0 removes the cap.</param>
/// <param name="DailyPerInstance">Requests everybody together may make. 0 removes the cap.</param>
public sealed record AiSettingsRequest(
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    IReadOnlyList<string>? AllowedSpaces,
    IReadOnlyList<string>? DisabledFeatures,
    int? DailyPerUser,
    int? DailyPerInstance);
