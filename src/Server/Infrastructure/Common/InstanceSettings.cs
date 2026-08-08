using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Entities;
using Compendio.Domain.Localization;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Common;

/// <summary>
/// Stored settings with a configuration fallback, cached until something changes them.
/// </summary>
/// <remarks>
/// Loaded synchronously and defensively: this sits on the request path for language resolution, and
/// a database that is not ready yet — the very first request of a fresh install — must produce the
/// configured defaults rather than an exception.
/// </remarks>
public sealed class InstanceSettings(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IOptions<CompendioOptions> options,
    ILogger<InstanceSettings> logger) : IInstanceSettings
{
    private readonly Lock _gate = new();
    private readonly InstanceOptions _configured = options.Value.Instance;

    private Values? _cached;

    public string DefaultLanguage => Load().DefaultLanguage;

    public PermissionLevel DefaultAccess => Load().DefaultAccess;

    public string InstanceName => Load().InstanceName;

    public bool ForceSingleLanguage => Load().ForceSingleLanguage;

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private Values Load()
    {
        var cached = _cached;
        if (cached is not null)
        {
            return cached;
        }

        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var fallback = new Values(
                SupportedLanguages.ResolveOrFallback(_configured.DefaultLanguage, SupportedLanguages.Spanish),
                _configured.DefaultAccess,
                string.IsNullOrWhiteSpace(_configured.Name) ? CompendioConstants.ProductName : _configured.Name,
                _configured.ForceSingleLanguage);

            try
            {
                using var db = dbFactory.CreateDbContext();

                var stored = db.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == SettingKeys.InstanceDefaultLanguage
                                || s.Key == SettingKeys.InstanceDefaultAccess
                                || s.Key == SettingKeys.InstanceName
                                || s.Key == SettingKeys.ForceSingleLanguage)
                    .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);

                _cached = new Values(
                    stored.TryGetValue(SettingKeys.InstanceDefaultLanguage, out var language)
                        ? SupportedLanguages.ResolveOrFallback(language, fallback.DefaultLanguage)
                        : fallback.DefaultLanguage,
                    stored.TryGetValue(SettingKeys.InstanceDefaultAccess, out var access)
                     && Enum.TryParse<PermissionLevel>(access, ignoreCase: true, out var parsedAccess)
                        ? parsedAccess
                        : fallback.DefaultAccess,
                    stored.TryGetValue(SettingKeys.InstanceName, out var name) && name.Length > 0
                        ? name
                        : fallback.InstanceName,
                    stored.TryGetValue(SettingKeys.ForceSingleLanguage, out var force)
                     && bool.TryParse(force, out var parsedForce)
                        ? parsedForce
                        : fallback.ForceSingleLanguage);
            }
            catch (Exception e)
            {
                // The first request of a fresh install can arrive before migrations finish, and a
                // settings lookup is not worth failing it over.
                logger.LogDebug("Falling back to configured instance settings: {Reason}", e.GetType().Name);
                _cached = fallback;
            }

            return _cached;
        }
    }

    private sealed record Values(
        string DefaultLanguage,
        PermissionLevel DefaultAccess,
        string InstanceName,
        bool ForceSingleLanguage);
}
