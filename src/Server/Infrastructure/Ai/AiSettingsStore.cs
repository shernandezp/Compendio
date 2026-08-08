using System.Globalization;
using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Ai;

/// <inheritdoc />
/// <remarks>
/// Cached behind a version counter rather than read per request: every page render asks whether AI
/// is enabled, and three database round trips for "no" would be a tax on the common case, which is
/// an instance with no AI configured at all.
/// </remarks>
public sealed class AiSettingsStore(
    IDbContextFactory<CompendioDbContext> dbFactory,
    ISecretProtector protector,
    IClock clock,
    IOptions<CompendioOptions> options,
    ILogger<AiSettingsStore> logger) : IAiSettings
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AiConfiguration? _cached;

    public async Task<AiConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cached ??= await LoadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(SettingKeys.AiApiKey, cancellationToken);
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (protector.TryUnprotect(stored, out var plaintext))
        {
            return plaintext;
        }

        // The Data Protection key ring changed underneath us — a restored backup, or a data
        // directory moved between machines. Reporting it is the only useful thing to do: the key
        // cannot be recovered and the admin has to paste it again.
        logger.LogWarning("The stored AI API key could not be decrypted. Re-enter it in the admin screen.");
        return null;
    }

    public async Task SaveAsync(
        string? baseUrl,
        string? model,
        string? apiKey,
        IReadOnlyList<string>? allowedSpaces,
        IReadOnlyList<string>? disabledFeatures,
        int? dailyPerUser,
        int? dailyPerInstance,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (baseUrl is not null)
        {
            await WriteAsync(db, SettingKeys.AiBaseUrl, NormalizeBaseUrl(baseUrl), cancellationToken);
        }

        if (model is not null)
        {
            await WriteAsync(db, SettingKeys.AiModel, model.Trim(), cancellationToken);
        }

        if (apiKey is not null)
        {
            // Null means "leave it alone" so the admin screen can save other fields without the key
            // being round-tripped through the browser. Empty means "clear it".
            await WriteAsync(db, SettingKeys.AiApiKey,
                apiKey.Length == 0 ? string.Empty : protector.Protect(apiKey), cancellationToken);
        }

        if (allowedSpaces is not null)
        {
            await WriteAsync(db, SettingKeys.AiAllowedSpaces, string.Join('\n', allowedSpaces), cancellationToken);
        }

        if (disabledFeatures is not null)
        {
            await WriteAsync(db, SettingKeys.AiDisabledFeatures, string.Join('\n', disabledFeatures), cancellationToken);
        }

        // Clamped rather than rejected: a negative cap is a typo, and refusing the whole save over
        // one would leave the admin unable to fix the field they came here for.
        if (dailyPerUser is { } perUser)
        {
            await WriteAsync(db, SettingKeys.AiDailyPerUser,
                Math.Max(0, perUser).ToString(CultureInfo.InvariantCulture), cancellationToken);
        }

        if (dailyPerInstance is { } perInstance)
        {
            await WriteAsync(db, SettingKeys.AiDailyPerInstance,
                Math.Max(0, perInstance).ToString(CultureInfo.InvariantCulture), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        Invalidate();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        string[] keys =
        [
            SettingKeys.AiBaseUrl, SettingKeys.AiModel, SettingKeys.AiApiKey,
            SettingKeys.AiAllowedSpaces, SettingKeys.AiDisabledFeatures,
            SettingKeys.AiDailyPerUser, SettingKeys.AiDailyPerInstance,
        ];

        await db.Settings.Where(s => keys.Contains(s.Key)).ExecuteDeleteAsync(cancellationToken);
        Invalidate();
    }

    public void Invalidate() => _cached = null;

    private async Task<AiConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var values = await db.Settings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("ai."))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var baseUrl = values.GetValueOrDefault(SettingKeys.AiBaseUrl, string.Empty);
        var model = values.GetValueOrDefault(SettingKeys.AiModel, string.Empty);

        // A base URL and a model are what "configured" means. A key is optional, because Ollama and
        // LM Studio do not ask for one — and requiring a placeholder would be a papercut on the
        // deployment this product recommends most.
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            return AiConfiguration.Disabled;
        }

        return new AiConfiguration(
            Enabled: true,
            baseUrl,
            model,
            EndpointLabel: DescribeEndpoint(baseUrl),
            AllowedSpaces: Split(values.GetValueOrDefault(SettingKeys.AiAllowedSpaces)),
            DisabledFeatures: Split(values.GetValueOrDefault(SettingKeys.AiDisabledFeatures))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            // Unset means the deployment default, not "unlimited". Somebody who pastes in an OpenAI
            // key and walks away should still have a ceiling; removing it has to be a decision
            // somebody typed, which is what storing an explicit 0 records.
            DailyPerUser: ReadCap(values, SettingKeys.AiDailyPerUser, options.Value.Ai.DefaultDailyPerUser),
            DailyPerInstance: ReadCap(values, SettingKeys.AiDailyPerInstance, options.Value.Ai.DefaultDailyPerInstance));
    }

    private static int ReadCap(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;

    /// <summary>
    /// The host, for the privacy notice. Never the path or the query, which can carry a key on some
    /// gateway deployments.
    /// </summary>
    private static string DescribeEndpoint(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

    private static string NormalizeBaseUrl(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Settings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WriteAsync(CompendioDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (existing is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = clock.UtcNow });
            return;
        }

        existing.Value = value;
        existing.UpdatedAt = clock.UtcNow;
    }
}
