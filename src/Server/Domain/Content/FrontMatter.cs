using System.Globalization;

namespace Compendio.Domain.Content;

/// <summary>
/// The YAML block at the top of a page.
/// </summary>
/// <remarks>
/// <see cref="Extra"/> exists because users and other tools put their own keys here, and eating
/// them on write would break the no-lock-in promise. A page with no front matter at all is valid:
/// the title then falls back to the first <c>#</c> heading, then to the file name.
/// </remarks>
public sealed record FrontMatter
{
    public static readonly FrontMatter Empty = new();

    public string? Title { get; init; }

    /// <summary>BCP-47. Absent means the instance default language.</summary>
    public string? Lang { get; init; }

    /// <summary>
    /// Stable identifier shared by every language version of the same document. Authoritative —
    /// the <c>name.&lt;lang&gt;.md</c> sibling convention is only a fallback.
    /// </summary>
    public string? TranslationKey { get; init; }

    /// <summary>Informative pointer to the source page. Never used for linking.</summary>
    public string? TranslationOf { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Owner { get; init; }

    // ---- Lifecycle: written to the schema in v0, given behaviour in v1. -------------------------
    public int? ReviewIntervalDays { get; init; }

    public DateTimeOffset? NextReviewDate { get; init; }

    public bool? RequiresAcknowledgment { get; init; }

    /// <summary>
    /// Set by AI translation, cleared by a human save from the editor.
    /// </summary>
    /// <remarks>
    /// Front matter rather than a database column on purpose: the badge has to survive the file
    /// being copied, exported or committed. A wrong Spanish HR policy that has lost its
    /// "unreviewed" mark somewhere along the way is worse than no Spanish HR policy.
    /// </remarks>
    public bool? MachineTranslated { get; init; }

    /// <summary>Keys we do not know about, in their original order, preserved on write.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Extra { get; init; } = [];

    /// <summary>Keys this type maps to properties. Everything else lands in <see cref="Extra"/>.</summary>
    public static readonly string[] KnownKeys =
    [
        "title", "lang", "translationKey", "translationOf", "tags", "owner",
        "reviewIntervalDays", "nextReviewDate", "requiresAcknowledgment", "machineTranslated",
    ];

    /// <summary>
    /// Tags normalized for storage and search: lower-cased, deduplicated, ordered, and with internal
    /// whitespace folded to a hyphen.
    /// </summary>
    /// <remarks>
    /// The hyphen is not cosmetic. Tags are stored space-separated on the page row and the tag
    /// filter and the counts both split on that space, so a tag written <c>manual de usuario</c>
    /// would otherwise arrive as three tags nobody wrote. A hyphen survives the FTS5 tokenizer whole
    /// because <c>-</c> is one of its <c>tokenchars</c>.
    /// </remarks>
    public IReadOnlyList<string> NormalizedTags() =>
        Tags.Select(t => CollapseWhitespace(t).ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string CollapseWhitespace(string tag)
    {
        var builder = new System.Text.StringBuilder(tag.Length);

        foreach (var c in tag.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim('-');
    }

    public bool IsEmpty =>
        Title is null && Lang is null && TranslationKey is null && TranslationOf is null &&
        Owner is null && Tags.Count == 0 && Extra.Count == 0 &&
        ReviewIntervalDays is null && NextReviewDate is null && RequiresAcknowledgment is null &&
        MachineTranslated is null;

    internal static string? FormatDate(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
