using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Ai;

/// <param name="Limit">0 means no ceiling, and then <paramref name="Remaining"/> is null.</param>
/// <param name="Used">Requests in the last 24 hours.</param>
/// <param name="Remaining">Null when there is no limit, so the client shows nothing rather than a made-up number.</param>
/// <param name="ResetsAt">
/// When the oldest counted request falls out of the window — the moment one more becomes possible.
/// Null when nothing is spent or there is no limit.
/// </param>
public sealed record AiBudgetState(int Limit, int Used, int? Remaining, DateTimeOffset? ResetsAt);

/// <summary>
/// What one person and the whole instance may spend on the AI provider in a rolling day.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is a bill, not abuse. Every other limit in the product guards the server;
/// this one guards somebody's card. A metered endpoint charges per request whether the request was
/// useful, deliberate, or a page that re-rendered in a loop, so the ceiling has to be counted
/// somewhere the client cannot influence.
/// </para>
/// <para>
/// A <strong>rolling 24 hours</strong>, not a calendar day. A calendar day is easier to say and
/// worse to live with: it resets at a midnight in some timezone the user is not in, and it lets a
/// whole day's budget be spent twice in two hours across the boundary. Rolling means the sentence
/// the UI shows — "23 of 50 used in the last 24 hours" — is exactly what is enforced.
/// </para>
/// <para>
/// A request is charged when it is about to reach the provider, and it stays charged if the provider
/// then fails. That is deliberate: a timeout arrives <em>after</em> the model has generated tokens,
/// so a refund on failure would refund the requests that cost the most. It is stated in the docs
/// rather than hidden, because the alternative — a free retry loop against a paid endpoint — is the
/// exact thing this exists to prevent.
/// </para>
/// </remarks>
public sealed class AiBudget(
    ICompendioDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<CompendioOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>No ceiling. <c>Remaining</c> is null rather than a large number, so a client shows nothing.</summary>
    public static readonly AiBudgetState Unlimited = new(0, 0, null, null);

    /// <summary>
    /// Records one request against both ceilings, refusing with <c>ai.quota_exceeded</c> if either
    /// is spent.
    /// </summary>
    /// <remarks>
    /// Called immediately before the provider call and never before a permission check — being told
    /// you have spent your budget on a page you were not allowed to touch would be both wrong and a
    /// small disclosure.
    /// </remarks>
    public async Task ChargeAsync(AiConfiguration configuration, string feature, int inputCharacters, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var since = now - Window;
        var userId = currentUser.UserId;

        // `Guid.Empty` is the system caller — the CLI, and any future background path. Every AI
        // endpoint requires authentication, so this cannot be reached over HTTP; the guard exists so
        // that if one ever is added, system requests are not all counted against one shared person
        // who does not exist. They are still recorded, so the instance total stays honest.
        if (configuration.DailyPerUser > 0 && userId != Guid.Empty)
        {
            var used = await db.AiUsage.CountAsync(u => u.UserId == userId && u.At >= since, cancellationToken);

            if (used >= configuration.DailyPerUser)
            {
                throw await RefusalAsync(
                    ProblemCodes.AiQuotaExceeded,
                    scopeKey: "user",
                    configuration.DailyPerUser,
                    u => u.UserId == userId,
                    since,
                    cancellationToken);
            }
        }

        if (configuration.DailyPerInstance > 0)
        {
            var used = await db.AiUsage.CountAsync(u => u.At >= since, cancellationToken);

            if (used >= configuration.DailyPerInstance)
            {
                throw await RefusalAsync(
                    ProblemCodes.AiQuotaExceededInstance,
                    scopeKey: "instance",
                    configuration.DailyPerInstance,
                    _ => true,
                    since,
                    cancellationToken);
            }
        }

        db.AiUsage.Add(new AiUsageEntry
        {
            Id = Guid.CreateVersion7(),
            At = now,
            UserId = userId,
            Feature = feature,
            InputCharacters = inputCharacters,
        });

        // Saves through the request's context, which by this point in every AI handler is holding
        // reads and nothing else — the charge is the first write any of them makes. A handler that
        // later starts modifying entities *before* its provider call would have them committed here,
        // so the ordering rule is: charge before the first write, not just before the provider.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The caller's own budget, for the status endpoint.</summary>
    public Task<AiBudgetState> ForCurrentUserAsync(AiConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId;

        // Matches the exemption in ChargeAsync: a caller who is not charged is not shown a ceiling
        // they are not subject to.
        if (userId == Guid.Empty)
        {
            return Task.FromResult(Unlimited);
        }

        // Not counted when there is no personal cap: the client renders nothing in that case, so the
        // query would be two round trips per page load to produce a number nobody sees.
        return StateAsync(configuration.DailyPerUser, u => u.UserId == userId, countWithoutLimit: false, cancellationToken);
    }

    /// <summary>
    /// The whole instance's budget, for the admin screen.
    /// </summary>
    /// <remarks>
    /// Counted <em>even when no cap is set</em>, which is the whole point of it. This number exists so
    /// an administrator can pick a cap against what the instance actually spends — and with the
    /// instance cap off by default, an implementation that only counted once a cap existed would show
    /// zero to exactly the person deciding, and a real figure only to someone who had already decided.
    /// </remarks>
    public Task<AiBudgetState> ForInstanceAsync(AiConfiguration configuration, CancellationToken cancellationToken = default) =>
        StateAsync(configuration.DailyPerInstance, _ => true, countWithoutLimit: true, cancellationToken);

    /// <summary>
    /// Drops usage rows past the retention window.
    /// </summary>
    /// <remarks>
    /// Only the last 24 hours are ever counted, so anything older is kept purely so the admin screen
    /// can answer "who spent it". Left unpruned the table would grow forever to serve a question
    /// nobody asks about last spring.
    /// </remarks>
    public Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.UtcNow - TimeSpan.FromDays(Math.Max(1, options.Value.Ai.UsageRetentionDays));

        return db.AiUsage.Where(u => u.At < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    /// <param name="countWithoutLimit">
    /// Whether to run the count when no cap is set. "No cap" and "nothing spent" are different facts,
    /// and only the caller knows whether it needs the second one.
    /// </param>
    private async Task<AiBudgetState> StateAsync(
        int limit,
        System.Linq.Expressions.Expression<Func<AiUsageEntry, bool>> scope,
        bool countWithoutLimit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0 && !countWithoutLimit)
        {
            return Unlimited;
        }

        var since = clock.UtcNow - Window;
        var rows = db.AiUsage.Where(scope).Where(u => u.At >= since);

        var used = await rows.CountAsync(cancellationToken);
        var oldest = used == 0 ? (DateTimeOffset?)null : await rows.MinAsync(u => u.At, cancellationToken);

        // `remaining` stays null with no cap — there is nothing to count down from, and a number
        // there would be an invented ceiling. `used` is real either way.
        return new AiBudgetState(limit, used, limit > 0 ? Math.Max(0, limit - used) : null, oldest + Window);
    }

    /// <summary>
    /// Builds the refusal, naming which ceiling was hit and when the next request becomes possible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>When</strong> travels as a machine-readable <c>resetsAt</c> and never as prose. An
    /// earlier version put the wait in the sentence as a number of minutes, which reads well at "in
    /// about 40 minutes" and absurdly at "in about 1440 minutes" — and a rolling window produces both
    /// with equal frequency, so there was no wording that stayed sensible.
    /// </para>
    /// <para>
    /// Nor could a formatted time be built here: the detail is localized at the edge in the caller's
    /// language, and a clock time rendered server-side would carry the server's culture and, worse,
    /// the server's timezone. The client already turns <c>resetsAt</c> into a local, localized time.
    /// </para>
    /// </remarks>
    private async Task<CompendioException> RefusalAsync(
        string code,
        string scopeKey,
        int limit,
        System.Linq.Expressions.Expression<Func<AiUsageEntry, bool>> scope,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var oldest = await db.AiUsage
            .Where(scope)
            .Where(u => u.At >= since)
            .MinAsync(u => (DateTimeOffset?)u.At, cancellationToken);

        var resetsAt = (oldest ?? clock.UtcNow) + Window;

        var error = new CompendioException(code, StatusCodes.Status429TooManyRequests, limit);

        // Machine-readable beside the sentence: the client renders a countdown from these rather
        // than parsing the localized detail back apart.
        error.Extensions["scope"] = scopeKey;
        error.Extensions["limit"] = limit;
        error.Extensions["resetsAt"] = resetsAt;

        return error;
    }
}
