using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Admin;

/// <param name="RemoteUrl">
/// Reported as configured or not, never echoed: a remote URL can carry an access token, and the
/// admin screen is not a place to display one back.
/// </param>
public sealed record GitMirrorStatusDto(
    bool Enabled,
    bool GitAvailable,
    bool RemoteConfigured,
    string Branch,
    int IntervalMinutes,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? LastCommit,
    string? LastError,
    int ConsecutiveFailures);

public sealed record GetGitMirrorStatusQuery : IQuery<GitMirrorStatusDto>;

public sealed class GetGitMirrorStatusHandler(
    ICompendioDbContext db,
    IGitMirror mirror,
    IOptions<CompendioOptions> options) : IRequestHandler<GetGitMirrorStatusQuery, GitMirrorStatusDto>
{
    public async Task<GitMirrorStatusDto> Handle(GetGitMirrorStatusQuery request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value.GitMirror;

        var values = await db.Settings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("gitmirror."))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        return new GitMirrorStatusDto(
            settings.Enabled,
            await mirror.IsGitAvailableAsync(cancellationToken),
            !string.IsNullOrWhiteSpace(settings.RemoteUrl),
            settings.Branch,
            settings.IntervalMinutes,
            ParseDate(values.GetValueOrDefault(SettingKeys.GitMirrorLastSuccessAt)),
            ParseDate(values.GetValueOrDefault(SettingKeys.GitMirrorLastAttemptAt)),
            Empty(values.GetValueOrDefault(SettingKeys.GitMirrorLastCommit)),
            Empty(values.GetValueOrDefault(SettingKeys.GitMirrorLastError)),
            int.TryParse(values.GetValueOrDefault(SettingKeys.GitMirrorConsecutiveFailures), out var failures) ? failures : 0);
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Runs the push now, for an admin who does not want to wait for the timer.</summary>
public sealed record PushGitMirrorCommand : ICommand<GitMirrorStatusDto>;

public sealed class PushGitMirrorHandler(IGitMirror mirror, ISender sender) : IRequestHandler<PushGitMirrorCommand, GitMirrorStatusDto>
{
    public async Task<GitMirrorStatusDto> Handle(PushGitMirrorCommand request, CancellationToken cancellationToken = default)
    {
        await mirror.PushAsync(cancellationToken);
        return await sender.Send(new GetGitMirrorStatusQuery(), cancellationToken);
    }
}
