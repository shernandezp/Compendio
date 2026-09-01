using System.Globalization;
using System.Text;

namespace Compendio.Domain.Content;

/// <summary>
/// ASCII slugification for file and folder names.
/// </summary>
/// <remarks>
/// <para>
/// This is a path-safety and portability decision, not an aesthetic one: accented and non-ASCII
/// file names survive a local disk but not reliably an SMB share, a zip round-trip or a git client
/// on a different platform. The accented title lives in front matter and is what the UI shows;
/// renaming a title therefore does not rename the file unless the user asks for it.
/// </para>
/// <para>
/// Letter case is <em>kept</em>. A page titled "Index" is the file <c>Index.md</c> and a folder
/// named "Infrastructure" is the directory <c>Infrastructure</c> — which is what the tree shows,
/// because folder names come from the disk. Lower-casing everything turned "IT" into "it" and every
/// name a person typed into something they had not, for no portability gain: the real risk with
/// case is two names that differ only by it, and that is handled where collisions are checked
/// (<see cref="Disambiguate"/>), not by flattening every name. Heading anchors are the exception
/// (<see cref="Anchor"/>): a URL fragment is conventionally lower-case and existing links rely on it.
/// </para>
/// </remarks>
public static class Slug
{
    public const int MaxLength = 80;

    private const string Fallback = "untitled";

    /// <summary>Spanish and Latin-1 letters that decomposition alone does not reduce to ASCII.</summary>
    private static readonly Dictionary<char, string> Transliterations = new()
    {
        ['ñ'] = "n", ['Ñ'] = "n",
        ['ç'] = "c", ['Ç'] = "c",
        ['ø'] = "o", ['Ø'] = "o",
        ['æ'] = "ae", ['Æ'] = "ae",
        ['œ'] = "oe", ['Œ'] = "oe",
        ['ß'] = "ss",
        ['đ'] = "d", ['Đ'] = "d",
        ['ł'] = "l", ['Ł'] = "l",
        ['&'] = "-and-",
    };

    public static string Create(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fallback;
        }

        var expanded = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (Transliterations.TryGetValue(c, out var replacement))
            {
                expanded.Append(replacement);
            }
            else
            {
                expanded.Append(c);
            }
        }

        var decomposed = expanded.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(c);
            }
            else if (c is '-' or '_' or '.')
            {
                // Kept because IT content depends on them: VPN-Site-A, snake_case, 192.168.1.1.
                if (builder.Length > 0)
                {
                    pendingSeparator = false;
                    builder.Append(c);
                }
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var slug = CollapseDots(builder.ToString()).Trim('-', '.', '_');

        if (slug.Length > MaxLength)
        {
            slug = CollapseDots(slug[..MaxLength]).TrimEnd('-', '.', '_');
        }

        return slug.Length == 0 ? Fallback : MakeSafe(slug);
    }

    /// <summary>
    /// Collapses runs of dots. <c>PathPolicy</c> rejects <c>..</c> anywhere in a segment rather than
    /// reasoning about every encoding of it, so a title like "Versión 1..2" would otherwise
    /// slugify into a name the store refuses to write.
    /// </summary>
    private static string CollapseDots(string value)
    {
        if (!value.Contains("..", StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '.' && builder.Length > 0 && builder[^1] == '.')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Keeps the slug clear of the Windows device names.
    /// </summary>
    /// <remarks>
    /// <c>CON</c>, <c>NUL</c>, <c>COM1</c> and friends are unopenable on Windows with or without an
    /// extension, and <c>PathPolicy</c> rejects them on Linux too so that content stays portable. A
    /// page legitimately titled "Con" or "Aux" is not an error the user can act on, so the slug is
    /// nudged instead of the write being refused.
    /// </remarks>
    private static string MakeSafe(string slug) =>
        PathPolicy.IsReservedName(slug) ? "_" + slug : slug;

    /// <summary>Slug plus the <c>.md</c> extension.</summary>
    public static string CreateFileName(string? title) =>
        Create(title) + CompendioConstants.MarkdownExtension;

    /// <summary>
    /// A heading anchor: the slug, lower-cased.
    /// </summary>
    /// <remarks>
    /// File names keep their case; anchors do not. <c>#configuration</c> is what every Markdown
    /// renderer produces for "## Configuration", it is what people have already typed into links,
    /// and a fragment that changed case with the heading would break them all.
    /// </remarks>
    public static string Anchor(string? text) => Create(text).ToLowerInvariant();

    /// <summary>
    /// Resolves a collision by appending a numeric suffix: <c>Politica</c>, <c>Politica-2</c>, …
    /// </summary>
    /// <param name="exists">
    /// Whether a name is taken. Callers pass a <em>case-insensitive</em> check: <c>Index.md</c> and
    /// <c>index.md</c> are one file on Windows and two on Linux, and content written on one has to
    /// survive being copied to the other.
    /// </param>
    public static string Disambiguate(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName))
        {
            return baseName;
        }

        // Last dot, not the first. Dots survive slugification because IT content depends on them, so
        // "192.168.1.1.md" splitting at the first dot would produce "192-2.168.1.1.md" instead of
        // "192.168.1.1-2.md".
        var dot = baseName.LastIndexOf('.');
        var stem = dot <= 0 ? baseName : baseName[..dot];
        var extension = dot <= 0 ? string.Empty : baseName[dot..];

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem}-{i}{extension}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free name for '{baseName}' after 1000 attempts.");
    }
}
