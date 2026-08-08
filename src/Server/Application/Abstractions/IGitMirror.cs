namespace Compendio.Application.Abstractions;

/// <param name="Skipped">Nothing had changed, so nothing was pushed. Not a failure.</param>
public sealed record GitPushOutcome(bool Ok, bool Skipped, string Message, string? CommitSha);

/// <summary>
/// The optional git mirror, behind an interface so the application layer never spawns a process.
/// </summary>
/// <remarks>
/// Everything here degrades rather than throws when <c>git</c> is absent from <c>PATH</c>: the
/// feature reports unavailable and every other part of the product keeps working. That is the whole
/// reason the mirror is allowed to shell out to a binary at all.
/// </remarks>
public interface IGitMirror
{
    Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default);

    Task<GitPushOutcome> PushAsync(CancellationToken cancellationToken = default);
}
