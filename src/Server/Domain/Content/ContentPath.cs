using System.Diagnostics.CodeAnalysis;

namespace Compendio.Domain.Content;

/// <summary>
/// A content-relative path that has passed <see cref="PathPolicy"/>. Forward slashes on every
/// platform, no leading or trailing slash, never empty except for <see cref="Root"/>.
/// </summary>
/// <remarks>
/// The type exists so that "this string was validated" is expressible in a signature. A method
/// taking a <c>string</c> path has to re-validate or trust its caller; a method taking a
/// <see cref="ContentPath"/> does neither.
/// </remarks>
public readonly record struct ContentPath : IComparable<ContentPath>
{
    private readonly string? _value;

    private ContentPath(string value) => _value = value;

    /// <summary>The content root itself, represented as the empty path.</summary>
    public static ContentPath Root => new(string.Empty);

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    /// <summary>Last segment — the file name for a page, the folder name for a folder.</summary>
    public string Name
    {
        get
        {
            var value = Value;
            var slash = value.LastIndexOf('/');
            return slash < 0 ? value : value[(slash + 1)..];
        }
    }

    /// <summary>Last segment without its extension.</summary>
    public string NameWithoutExtension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name[..dot];
        }
    }

    /// <summary>Lower-cased extension including the dot, or empty.</summary>
    public string Extension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
        }
    }

    /// <summary>The containing folder. The parent of a depth-1 path is <see cref="Root"/>.</summary>
    public ContentPath Parent
    {
        get
        {
            var value = Value;
            var slash = value.LastIndexOf('/');
            return slash < 0 ? Root : new ContentPath(value[..slash]);
        }
    }

    public IReadOnlyList<string> Segments =>
        IsRoot ? [] : Value.Split('/');

    /// <summary>Root → this path inclusive, in order. What ACL evaluation walks.</summary>
    public IReadOnlyList<ContentPath> SelfAndAncestors()
    {
        var result = new List<ContentPath> { Root };
        if (IsRoot)
        {
            return result;
        }

        var value = Value;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '/')
            {
                result.Add(new ContentPath(value[..i]));
            }
        }

        result.Add(this);
        return result;
    }

    public bool IsUnder(ContentPath ancestor) =>
        ancestor.IsRoot || (Value.Length > ancestor.Value.Length
                            && Value.StartsWith(ancestor.Value, StringComparison.Ordinal)
                            && Value[ancestor.Value.Length] == '/');

    public bool IsSelfOrUnder(ContentPath ancestor) => Value == ancestor.Value || IsUnder(ancestor);

    /// <summary>
    /// Whether this path and <paramref name="other"/> spell the same name in different letter case.
    /// </summary>
    /// <remarks>
    /// The rename-by-case case: <c>it</c> → <c>IT</c>, <c>index.md</c> → <c>Index.md</c>. On a
    /// case-insensitive file system the destination "exists" because it <em>is</em> the source, and
    /// a naive exists-check refuses the one rename the user most obviously meant.
    /// </remarks>
    public bool IsCaseVariantOf(ContentPath other) =>
        Value != other.Value && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends a single already-validated segment.</summary>
    public ContentPath Append(string segment) =>
        IsRoot ? new ContentPath(segment) : new ContentPath($"{Value}/{segment}");

    /// <summary>Re-roots this path under <paramref name="newAncestor"/>. Used by move.</summary>
    public ContentPath Rebase(ContentPath oldAncestor, ContentPath newAncestor)
    {
        if (Value == oldAncestor.Value)
        {
            return newAncestor;
        }

        if (!IsUnder(oldAncestor))
        {
            throw new InvalidOperationException($"'{Value}' is not under '{oldAncestor.Value}'.");
        }

        var tail = oldAncestor.IsRoot ? Value : Value[(oldAncestor.Value.Length + 1)..];
        return newAncestor.IsRoot ? new ContentPath(tail) : new ContentPath($"{newAncestor.Value}/{tail}");
    }

    /// <summary>
    /// Wraps a string that is already known to be valid. Only <see cref="PathPolicy"/> and code
    /// reading a path back out of the database should call this.
    /// </summary>
    public static ContentPath FromTrusted(string value) => new(value ?? string.Empty);

    /// <summary>Validates through <see cref="PathPolicy"/>. Prefer this everywhere else.</summary>
    public static bool TryParse(string? candidate, PathKind kind, [NotNullWhen(true)] out ContentPath? path, out PathRule violated)
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

    public int CompareTo(ContentPath other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static implicit operator string(ContentPath path) => path.Value;
}
