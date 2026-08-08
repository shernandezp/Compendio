using System.Diagnostics.CodeAnalysis;
using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

/// <summary>
/// The content root, plus path validation bound to it.
/// </summary>
/// <remarks>
/// The rules themselves live in <see cref="PathPolicy"/> and exist exactly once; this interface is
/// the seam that gives handlers the configured root without reaching for configuration, and gives
/// tests a temp folder without a running host.
/// </remarks>
public interface IPathPolicy
{
    /// <summary>Absolute path of the content root, with no trailing separator.</summary>
    string ContentRoot { get; }

    /// <summary>Validates shape and throws <c>path.invalid</c> naming the failed rule.</summary>
    ContentPath Require(string? candidate, PathKind kind);

    bool TryValidate(string? candidate, PathKind kind, [NotNullWhen(true)] out ContentPath? path, out PathRule violated);

    /// <summary>
    /// Maps to an absolute path, proving the result stays inside the root after symlinks are
    /// resolved. Returns false rather than throwing so callers can treat it as "not found".
    /// </summary>
    bool TryResolve(ContentPath path, [NotNullWhen(true)] out string? absolutePath);

    /// <summary>Absolute → content path. How the watcher turns an OS event into something usable.</summary>
    bool TryMap(string absolutePath, PathKind kind, [NotNullWhen(true)] out ContentPath? path);
}
