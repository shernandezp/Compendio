using Compendio.Domain.Security;

namespace Compendio.Application.Abstractions;

/// <summary>
/// Instance-level answers the setup wizard and the admin screens give, which outlive a config file.
/// </summary>
/// <remarks>
/// Settings stored in the database win over <c>appsettings.json</c>, because the wizard asks these
/// questions and an answer that quietly had no effect would be worse than not asking. Configuration
/// remains the fallback for a fresh install and for an operator who wants to pin a value.
/// </remarks>
public interface IInstanceSettings
{
    /// <summary>The default UI language for people who have not chosen one.</summary>
    string DefaultLanguage { get; }

    /// <summary><see cref="PermissionLevel.Read"/> normally, <see cref="PermissionLevel.None"/> locked down.</summary>
    PermissionLevel DefaultAccess { get; }

    string InstanceName { get; }

    /// <summary>Forces one UI language for everyone. Some organizations want uniformity.</summary>
    bool ForceSingleLanguage { get; }

    /// <summary>Drops the cache. Called by setup and by any settings change.</summary>
    void Invalidate();
}
