namespace Compendio.Application.Abstractions;

/// <param name="EndpointLabel">
/// The host content would be sent to, shown next to every AI action. Named, not buried: for this
/// audience "where does my HR policy go" is the question that decides whether the feature is used.
/// </param>
/// <param name="AllowedSpaces">
/// Depth-1 folders the AI may touch. Empty means all of them, which is the default.
/// </param>
/// <param name="DailyPerUser">
/// AI requests one person may make in a rolling 24 hours. 0 means no limit.
/// </param>
/// <param name="DailyPerInstance">
/// AI requests everybody together may make in a rolling 24 hours. 0 means no limit. Separate from
/// the per-person cap because the two failures are different: one person looping a script, and
/// three hundred people each behaving reasonably on a metered endpoint.
/// </param>
public sealed record AiConfiguration(
    bool Enabled,
    string BaseUrl,
    string Model,
    string EndpointLabel,
    IReadOnlyList<string> AllowedSpaces,
    IReadOnlySet<string> DisabledFeatures,
    int DailyPerUser,
    int DailyPerInstance)
{
    public static readonly AiConfiguration Disabled = new(
        false, string.Empty, string.Empty, string.Empty, [],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0);
}

/// <summary>
/// Where the AI endpoint, model and key live, and whether AI is configured at all.
/// </summary>
/// <remarks>
/// The key is protected with <see cref="ISecretProtector"/> — Data Protection, key ring in
/// <c>&lt;data&gt;/keys/</c> — and never with the master key, which is created lazily when the first
/// secure scope is made and therefore may not exist at all. It is never written to the content
/// folder, which users commit to git.
/// </remarks>
public interface IAiSettings
{
    /// <summary>Everything except the key. Safe to return from the API.</summary>
    Task<AiConfiguration> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The decrypted key, for the provider client only.</summary>
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);

    /// <param name="apiKey">Null leaves the stored key alone; empty clears it.</param>
    /// <remarks>Every parameter is null-means-leave-alone, so one screen can save one field.</remarks>
    Task SaveAsync(
        string? baseUrl,
        string? model,
        string? apiKey,
        IReadOnlyList<string>? allowedSpaces,
        IReadOnlyList<string>? disabledFeatures,
        int? dailyPerUser,
        int? dailyPerInstance,
        CancellationToken cancellationToken = default);

    /// <summary>Drops every AI setting, returning the instance to v0 behaviour.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    void Invalidate();
}
