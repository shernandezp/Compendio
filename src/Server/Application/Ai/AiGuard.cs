using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Ai;

/// <summary>The AI features, as machine names the admin screen can switch off individually.</summary>
public static class AiFeatures
{
    public const string Improve = "improve";
    public const string Draft = "draft";
    public const string Summarize = "summarize";
    public const string Translate = "translate";
    public const string Ask = "ask";
    public const string Freshness = "freshness";

    public static readonly string[] All = [Improve, Draft, Summarize, Translate, Ask, Freshness];
}

/// <summary>
/// The five questions asked before any page content reaches a model.
/// </summary>
/// <remarks>
/// <para>
/// In one place because they must not drift: is a provider configured, is this feature switched on,
/// is this space allowed, has this secure scope opted in — and is there any budget left. Six
/// handlers each asking four of the five is how the seventh handler ends up asking three.
/// </para>
/// <para>
/// This runs <em>before</em> the prompt is built, never after. Filtering a response is not a
/// substitute for not sending the content.
/// </para>
/// </remarks>
public sealed class AiGuard(
    IAiSettings settings,
    ICompendioDbContext db,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    AiBudget budget)
{
    /// <summary>Throws <c>ai.disabled</c> when no provider is configured or the feature is off.</summary>
    public async Task<AiConfiguration> RequireEnabledAsync(string feature, CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetAsync(cancellationToken);

        if (!configuration.Enabled || configuration.DisabledFeatures.Contains(feature))
        {
            // 404, not 501: with nothing configured the action genuinely does not exist, and the
            // client renders no control that could reach it.
            throw new CompendioException(ProblemCodes.AiDisabled, StatusCodes.Status404NotFound);
        }

        return configuration;
    }

    /// <summary>
    /// Confirms the caller may read this page <em>and</em> that its content may leave the instance.
    /// </summary>
    /// <remarks>
    /// Two separate questions with two separate answers. A page the caller cannot read is a 404, as
    /// everywhere else. A page they can read but whose scope has not opted into AI is a 403 naming
    /// the reason — they already know the page exists, so there is nothing left to hide.
    /// </remarks>
    public async Task<ContentPath> RequireContentAllowedAsync(
        AiConfiguration configuration,
        ContentPath path,
        CancellationToken cancellationToken = default)
    {
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        if (!IsSpaceAllowed(configuration, path))
        {
            throw new CompendioException(ProblemCodes.AiNotAllowedHere, StatusCodes.Status403Forbidden, path.Value);
        }

        if (await permissions.SecureScopeForAsync(path, cancellationToken) is { } scope)
        {
            var allowed = await db.SecureScopes
                .AsNoTracking()
                .Where(s => s.FolderPath == scope.Value && s.RetiredAt == null)
                .Select(s => s.AllowAi)
                .FirstOrDefaultAsync(cancellationToken);

            if (!allowed)
            {
                // Excluded by default, opted in per scope. The v0 column exists for exactly this.
                throw new CompendioException(ProblemCodes.AiNotAllowedHere, StatusCodes.Status403Forbidden, path.Value);
            }
        }

        return path;
    }

    /// <summary>
    /// Whether a path may be used as AI context at all, without throwing.
    /// </summary>
    /// <remarks>
    /// Retrieval calls this per candidate: a passage that fails is dropped silently, because raising
    /// an error would itself confirm that a matching page exists in a scope the caller cannot use.
    /// </remarks>
    public async Task<bool> IsContentAllowedAsync(
        AiConfiguration configuration,
        ContentPath path,
        CancellationToken cancellationToken = default)
    {
        if (!IsSpaceAllowed(configuration, path))
        {
            return false;
        }

        if (await permissions.SecureScopeForAsync(path, cancellationToken) is not { } scope)
        {
            return true;
        }

        return await db.SecureScopes
            .AsNoTracking()
            .Where(s => s.FolderPath == scope.Value && s.RetiredAt == null)
            .Select(s => s.AllowAi)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Charges one request against the daily budget, throwing <c>ai.quota_exceeded</c> when spent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called immediately before the provider call and after every permission check, so nobody is
    /// billed for a page they were refused. It lives on the guard rather than on the budget so that
    /// there is exactly one type a new AI handler has to remember, and the compiler's demand for a
    /// configuration argument makes forgetting it awkward.
    /// </para>
    /// <para>
    /// <paramref name="promptCharacters"/> is what will actually be sent, after truncation — the
    /// point is to record the size of the thing that cost money, not the size of what the user had.
    /// </para>
    /// </remarks>
    public Task ChargeAsync(
        AiConfiguration configuration,
        string feature,
        int promptCharacters,
        CancellationToken cancellationToken = default) =>
        budget.ChargeAsync(configuration, feature, promptCharacters, cancellationToken);

    /// <summary>An empty allow-list means every space, which is the default.</summary>
    private static bool IsSpaceAllowed(AiConfiguration configuration, ContentPath path)
    {
        if (configuration.AllowedSpaces.Count == 0)
        {
            return true;
        }

        var space = path.Value.Split('/', 2)[0];
        return configuration.AllowedSpaces.Contains(space, StringComparer.OrdinalIgnoreCase);
    }
}
