using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Ai;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Admin;

/// <param name="HasApiKey">Whether a key is stored. The key itself is never returned.</param>
/// <param name="DailyPerUser">Requests one person may make in a rolling 24 hours. 0 means no limit.</param>
/// <param name="DailyPerInstance">Requests everybody together may make. 0 means no limit.</param>
/// <param name="InstanceUsage">
/// What the instance has actually spent in the last 24 hours. The number that turns the cap from a
/// guess into a decision — an admin setting a limit without it is picking a number blind.
/// </param>
/// <param name="TopSpenders">Who spent it, most first. Names and counts, never prompts.</param>
public sealed record AiSettingsDto(
    bool Enabled,
    string BaseUrl,
    string Model,
    bool HasApiKey,
    string EndpointLabel,
    IReadOnlyList<string> AllowedSpaces,
    IReadOnlyList<string> DisabledFeatures,
    IReadOnlyList<string> AvailableFeatures,
    int DailyPerUser,
    int DailyPerInstance,
    AiBudgetState InstanceUsage,
    IReadOnlyList<AiSpenderDto> TopSpenders);

public sealed record AiSpenderDto(string DisplayName, int Requests);

public sealed record GetAiSettingsQuery : IQuery<AiSettingsDto>;

public sealed class GetAiSettingsHandler(
    IAiSettings settings,
    AiBudget budget,
    ICompendioDbContext db,
    IUserDirectory directory,
    IClock clock) : IRequestHandler<GetAiSettingsQuery, AiSettingsDto>
{
    public async Task<AiSettingsDto> Handle(GetAiSettingsQuery request, CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetAsync(cancellationToken);
        var key = await settings.GetApiKeyAsync(cancellationToken);

        return new AiSettingsDto(
            configuration.Enabled,
            configuration.BaseUrl,
            configuration.Model,
            HasApiKey: !string.IsNullOrEmpty(key),
            configuration.EndpointLabel,
            configuration.AllowedSpaces,
            configuration.DisabledFeatures.ToArray(),
            AiFeatures.All,
            configuration.DailyPerUser,
            configuration.DailyPerInstance,
            await budget.ForInstanceAsync(configuration, cancellationToken),
            await TopSpendersAsync(cancellationToken));
    }

    /// <summary>
    /// The five heaviest users of the last 24 hours.
    /// </summary>
    /// <remarks>
    /// Five, because the question this answers is "is one person doing this or is it everyone" and a
    /// full leaderboard invites reading it as a performance metric. A user who has since been deleted
    /// shows as their id rather than being dropped, so the counts still add up.
    ///
    /// <see cref="Guid.Empty"/> — the system caller — is excluded rather than shown as a raw id next
    /// to real names, since no HTTP path can produce one and an operator reading this wants people.
    /// </remarks>
    private async Task<IReadOnlyList<AiSpenderDto>> TopSpendersAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow - TimeSpan.FromHours(24);

        var counts = await db.AiUsage
            .AsNoTracking()
            .Where(u => u.At >= since && u.UserId != null && u.UserId != Guid.Empty)
            .GroupBy(u => u.UserId!.Value)
            .Select(g => new { UserId = g.Key, Requests = g.Count() })
            .OrderByDescending(g => g.Requests)
            .Take(5)
            .ToListAsync(cancellationToken);

        if (counts.Count == 0)
        {
            return [];
        }

        var names = await directory.SubjectNamesAsync(cancellationToken);

        return [.. counts.Select(c => new AiSpenderDto(
            names.GetValueOrDefault(c.UserId) ?? c.UserId.ToString(),
            c.Requests))];
    }
}

/// <param name="ApiKey">
/// Null leaves the stored key alone, so the admin screen can save other fields without the key
/// making a round trip through a browser. An empty string clears it.
/// </param>
public sealed record SaveAiSettingsCommand(
    string? BaseUrl,
    string? Model,
    string? ApiKey,
    IReadOnlyList<string>? AllowedSpaces,
    IReadOnlyList<string>? DisabledFeatures,
    int? DailyPerUser,
    int? DailyPerInstance) : ICommand<AiSettingsDto>;

public sealed class SaveAiSettingsHandler(
    IAiSettings settings,
    ICompendioDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ISender sender) : IRequestHandler<SaveAiSettingsCommand, AiSettingsDto>
{
    public async Task<AiSettingsDto> Handle(SaveAiSettingsCommand request, CancellationToken cancellationToken = default)
    {
        await settings.SaveAsync(
            request.BaseUrl, request.Model, request.ApiKey,
            request.AllowedSpaces, request.DisabledFeatures,
            request.DailyPerUser, request.DailyPerInstance, cancellationToken);

        // Audited without the endpoint's credentials, and without the base URL's query string —
        // some gateway deployments carry a key there.
        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = currentUser.UserId,
            Action = "ai.settings.changed",
            TargetType = "settings",
            TargetPath = "ai",
        });

        await db.SaveChangesAsync(cancellationToken);

        return await sender.Send(new GetAiSettingsQuery(), cancellationToken);
    }
}

/// <summary>Removes every AI setting, returning the instance to v0 behaviour.</summary>
public sealed record ClearAiSettingsCommand : ICommand<AiSettingsDto>;

public sealed class ClearAiSettingsHandler(
    IAiSettings settings,
    ICompendioDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    ISender sender) : IRequestHandler<ClearAiSettingsCommand, AiSettingsDto>
{
    public async Task<AiSettingsDto> Handle(ClearAiSettingsCommand request, CancellationToken cancellationToken = default)
    {
        await settings.ClearAsync(cancellationToken);

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = currentUser.UserId,
            Action = "ai.settings.cleared",
            TargetType = "settings",
            TargetPath = "ai",
        });

        await db.SaveChangesAsync(cancellationToken);

        return await sender.Send(new GetAiSettingsQuery(), cancellationToken);
    }
}

/// <summary>A one-token round trip, reporting the model's own reply or the transport error.</summary>
public sealed record TestAiConnectionCommand : ICommand<AiProbeResult>;

public sealed class TestAiConnectionHandler(IAiProvider provider) : IRequestHandler<TestAiConnectionCommand, AiProbeResult>
{
    public Task<AiProbeResult> Handle(TestAiConnectionCommand request, CancellationToken cancellationToken = default) =>
        provider.ProbeAsync(cancellationToken);
}
