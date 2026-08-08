using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Compendio.Domain.Content;

/// <summary>What a path is allowed to be.</summary>
public enum PathKind
{
    /// <summary>A folder. No extension requirement.</summary>
    Folder,

    /// <summary>A page. Must end in <c>.md</c>.</summary>
    Page,

    /// <summary>An attachment. Any extension; the allowlist is enforced separately on upload.</summary>
    Attachment,

    /// <summary>Shape rules only, no extension requirement.</summary>
    Any,
}

/// <summary>
/// The specific rule a path violated. Surfaced in the <c>path.invalid</c> ProblemDetails detail so
/// the message names which rule failed instead of saying "invalid path".
/// </summary>
public enum PathRule
{
    None = 0,
    Empty,
    TooLong,
    SegmentTooLong,
    AbsolutePath,
    UncPrefix,
    ParentTraversal,
    CurrentDirectorySegment,
    EmptySegment,
    NullByte,
    ControlCharacter,
    IllegalCharacter,
    AlternateDataStream,
    ReservedName,
    TrailingDotOrSpace,
    LeadingSpace,
    WrongExtension,
    HiddenOrSystem,
    EscapesRoot,
}

public readonly record struct PathValidation(bool IsValid, ContentPath Path, PathRule Violated)
{
    public static PathValidation Ok(ContentPath path) => new(true, path, PathRule.None);

    public static PathValidation Fail(PathRule rule) => new(false, ContentPath.Root, rule);
}

/// <summary>
/// The only path validator in the product.
/// </summary>
/// <remarks>
/// <para>
/// A second implementation of these rules is a security bug waiting to happen, not duplication —
/// every component that touches a caller-supplied path calls in here. The property test asserts
/// that no input to any content-store method can produce a file operation outside the content root.
/// </para>
/// <para>
/// NTFS-illegal characters and Windows reserved device names are rejected <em>on Linux too</em>.
/// That is deliberate: content written on a Linux instance must survive being copied to a Windows
/// share, which is a thing files-first users do.
/// </para>
/// </remarks>
public static class PathPolicy
{
    /// <summary>
    /// Budget for the content-relative portion of a path.
    /// </summary>
    /// <remarks>
    /// Windows' classic limit is 260 characters for the whole path. The remainder is headroom for
    /// the data-directory prefix (<c>C:\ProgramData\Compendio\data\content\</c> and friends), for
    /// the <c>.enc</c> suffix a secure scope adds, and for the temp-file suffix an atomic write
    /// adds. Long-path support on Windows is opt-in per machine, so we do not rely on it.
    /// </remarks>
    public const int MaxRelativePathLength = 180;

    public const int MaxSegmentLength = 100;

    private static readonly SearchValues<char> IllegalCharacters =
        SearchValues.Create("<>:\"|?*");

    /// <summary>
    /// Windows device names, reserved with or without an extension: <c>CON.md</c> is as unopenable
    /// as <c>CON</c>.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Whether a file or folder name's stem is a Windows device name.
    /// </summary>
    /// <remarks>
    /// Exposed so <see cref="Slug"/> can avoid generating one rather than having a second copy of
    /// the list to drift from this one. Same rule, stated once.
    /// </remarks>
    public static bool IsReservedName(string segment)
    {
        var dot = segment.IndexOf('.');
        return ReservedNames.Contains(dot > 0 ? segment[..dot] : segment);
    }

    /// <summary>
    /// Validates the <em>shape</em> of a content-relative path and normalizes separators.
    /// Containment inside the content root is a separate step — see <see cref="TryResolveAbsolute"/>.
    /// </summary>
    public static PathValidation Validate(string? candidate, PathKind kind)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return kind == PathKind.Folder
                ? PathValidation.Ok(ContentPath.Root)
                : PathValidation.Fail(PathRule.Empty);
        }

        if (candidate.Contains('\0'))
        {
            return PathValidation.Fail(PathRule.NullByte);
        }

        // A UNC prefix must be caught before separator normalization turns it into a plain path.
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return PathValidation.Fail(PathRule.UncPrefix);
        }

        var normalized = candidate.Replace('\\', '/').Trim('/');

        if (normalized.Length == 0)
        {
            return kind == PathKind.Folder
                ? PathValidation.Ok(ContentPath.Root)
                : PathValidation.Fail(PathRule.Empty);
        }

        // Drive-rooted ("C:/x") is caught here; the colon check below would also catch it, but this
        // rule name is the one a user can act on.
        if (normalized.Length >= 2 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0]))
        {
            return PathValidation.Fail(PathRule.AbsolutePath);
        }

        if (normalized.Length > MaxRelativePathLength)
        {
            return PathValidation.Fail(PathRule.TooLong);
        }

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            var rule = ValidateSegment(segment);
            if (rule != PathRule.None)
            {
                return PathValidation.Fail(rule);
            }
        }

        if (kind == PathKind.Page &&
            !normalized.EndsWith(CompendioConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase))
        {
            return PathValidation.Fail(PathRule.WrongExtension);
        }

        var path = ContentPath.FromTrusted(normalized);

        // A page or folder whose name the watcher and the reconciler are built to ignore cannot be
        // created through the API either. Accepting one produces a page that exists until the next
        // reconciliation pass walks the folder, does not see it, and deletes the row — content that
        // disappears on a timer with nothing to explain it.
        //
        // PathKind.Any stays permissive on purpose: that is what the watcher maps raw file-system
        // events through, and what a backup archive's entries are checked against, and both have to
        // be able to name a dotfile in order to decide to skip it.
        if (kind is PathKind.Page or PathKind.Folder && IsIgnored(path))
        {
            return PathValidation.Fail(PathRule.HiddenOrSystem);
        }

        return PathValidation.Ok(path);
    }

    private static PathRule ValidateSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return PathRule.EmptySegment;
        }

        if (segment.Length > MaxSegmentLength)
        {
            return PathRule.SegmentTooLong;
        }

        if (segment == "..")
        {
            return PathRule.ParentTraversal;
        }

        if (segment == ".")
        {
            return PathRule.CurrentDirectorySegment;
        }

        // ".." anywhere inside a segment is legal in a file name ("notes..md") but we reject it
        // rather than reason about every encoding of it.
        if (segment.Contains("..", StringComparison.Ordinal))
        {
            return PathRule.ParentTraversal;
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                return PathRule.ControlCharacter;
            }

            if (c == ':')
            {
                // "file.md:stream" — an NTFS alternate data stream, which is a way to hide content
                // beside a file that every other layer believes it has read.
                return PathRule.AlternateDataStream;
            }
        }

        if (segment.AsSpan().ContainsAny(IllegalCharacters))
        {
            return PathRule.IllegalCharacter;
        }

        // Windows silently strips these, so "report .md" and "report.md " become the same file and
        // a rename loop follows.
        if (segment[^1] is '.' or ' ')
        {
            return PathRule.TrailingDotOrSpace;
        }

        if (segment[0] == ' ')
        {
            return PathRule.LeadingSpace;
        }

        var stem = segment;
        var dot = stem.IndexOf('.');
        if (dot > 0)
        {
            stem = stem[..dot];
        }

        return ReservedNames.Contains(stem) ? PathRule.ReservedName : PathRule.None;
    }

    /// <summary>
    /// Maps a validated content path to an absolute path and proves the result is inside
    /// <paramref name="contentRoot"/>, resolving symlinks and re-checking afterwards.
    /// </summary>
    /// <remarks>
    /// The symlink step is the one that is easy to leave out: a validated relative path can still
    /// point at a link whose target is <c>/etc</c>, and the check has to happen against the
    /// <em>resolved</em> location, not the requested one.
    /// </remarks>
    public static bool TryResolveAbsolute(string contentRoot, ContentPath path, [NotNullWhen(true)] out string? absolute)
    {
        absolute = null;

        var rootFull = NormalizeExisting(contentRoot);
        var combined = path.IsRoot
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootFull, path.Value.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsInside(rootFull, combined))
        {
            return false;
        }

        // Resolve links on the deepest existing ancestor as well as on the target itself: a link in
        // the middle of the path relocates everything below it.
        var resolved = ResolveLinks(combined);
        if (resolved is not null && !IsInside(rootFull, resolved))
        {
            return false;
        }

        absolute = combined;
        return true;
    }

    /// <summary>
    /// Maps an absolute path reported by the file watcher back to a content path. Returns false for
    /// anything outside the root or failing the shape rules, which is how watcher noise is dropped.
    /// </summary>
    public static bool TryMapToContentPath(
        string contentRoot,
        string absolutePath,
        PathKind kind,
        [NotNullWhen(true)] out ContentPath? path)
    {
        path = null;

        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return false;
        }

        string rootFull;
        string full;
        try
        {
            rootFull = NormalizeExisting(contentRoot);
            full = Path.GetFullPath(absolutePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!IsInside(rootFull, full))
        {
            return false;
        }

        var relative = Path.GetRelativePath(rootFull, full).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".")
        {
            path = ContentPath.Root;
            return true;
        }

        var validation = Validate(relative, kind);
        if (!validation.IsValid)
        {
            return false;
        }

        path = validation.Path;
        return true;
    }

    /// <summary>
    /// Names the watcher and the reconciler ignore: version-control metadata, dotfiles, editor swap
    /// and lock files, and Office's <c>~$</c> temporaries. Shared by both so they cannot disagree.
    /// </summary>
    public static bool IsIgnored(ContentPath path)
    {
        foreach (var segment in path.Segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment[0] is '.' or '~')
            {
                return true;
            }

            if (segment.StartsWith("~$", StringComparison.Ordinal))
            {
                return true;
            }

            if (segment.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(".swx", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith('~'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the path is, or sits under, a page's <c>assets/</c> folder.
    /// </summary>
    /// <remarks>
    /// <c>assets/</c> is storage, not structure: it holds the images and files belonging to the
    /// pages beside it. It is not a place in the wiki, so it is not a node in the navigation tree,
    /// and a Markdown file dropped into it is an attachment rather than a page. Both callers ask
    /// here so they cannot disagree about what counts.
    /// </remarks>
    public static bool IsAssets(ContentPath path)
    {
        foreach (var segment in path.Segments)
        {
            if (segment.Equals(CompendioConstants.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeExisting(string root)
    {
        var full = Path.GetFullPath(root);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInside(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static string? ResolveLinks(string candidate)
    {
        try
        {
            var info = File.Exists(candidate)
                ? new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)
                : Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)
                    : null;

            if (info is not null)
            {
                return Path.GetFullPath(info.FullName);
            }

            // The target may not exist yet (a create is in flight); check the deepest ancestor that
            // does, because a linked parent directory relocates the child too.
            var parent = Path.GetDirectoryName(candidate);
            while (!string.IsNullOrEmpty(parent))
            {
                if (Directory.Exists(parent))
                {
                    var link = new DirectoryInfo(parent).ResolveLinkTarget(returnFinalTarget: true);
                    return link is null ? null : Path.GetFullPath(link.FullName);
                }

                parent = Path.GetDirectoryName(parent);
            }

            return null;
        }
        catch (IOException)
        {
            // A broken or cyclic link. Treat as unresolvable rather than trusting the raw path.
            return candidate + Path.DirectorySeparatorChar + "unresolvable";
        }
        catch (UnauthorizedAccessException)
        {
            return candidate + Path.DirectorySeparatorChar + "unresolvable";
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Human-readable rule name for the localized <c>path.invalid</c> detail.</summary>
    public static string RuleKey(PathRule rule) =>
        string.Create(CultureInfo.InvariantCulture, $"path.rule.{char.ToLowerInvariant(rule.ToString()[0])}{rule.ToString()[1..]}");
}
