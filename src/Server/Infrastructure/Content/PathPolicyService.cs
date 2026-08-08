using System.Diagnostics.CodeAnalysis;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;

namespace Compendio.Infrastructure.Content;

/// <summary>
/// Binds <see cref="PathPolicy"/> to the configured content root.
/// </summary>
/// <remarks>
/// Deliberately thin: every rule lives in <see cref="PathPolicy"/> and this type adds only the
/// root. If validation logic ever appears in here, it has been implemented twice.
/// </remarks>
public sealed class PathPolicyService(string contentRoot) : IPathPolicy
{
    public string ContentRoot { get; } = Path.GetFullPath(contentRoot)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public ContentPath Require(string? candidate, PathKind kind)
    {
        var result = PathPolicy.Validate(candidate, kind);
        if (!result.IsValid)
        {
            throw CompendioException.InvalidPath(result.Violated);
        }

        return result.Path;
    }

    public bool TryValidate(string? candidate, PathKind kind, [NotNullWhen(true)] out ContentPath? path, out PathRule violated)
    {
        var result = PathPolicy.Validate(candidate, kind);
        if (result.IsValid)
        {
            path = result.Path;
            violated = PathRule.None;
            return true;
        }

        path = null;
        violated = result.Violated;
        return false;
    }

    public bool TryResolve(ContentPath path, [NotNullWhen(true)] out string? absolutePath) =>
        PathPolicy.TryResolveAbsolute(ContentRoot, path, out absolutePath);

    public bool TryMap(string absolutePath, PathKind kind, [NotNullWhen(true)] out ContentPath? path) =>
        PathPolicy.TryMapToContentPath(ContentRoot, absolutePath, kind, out path);
}
