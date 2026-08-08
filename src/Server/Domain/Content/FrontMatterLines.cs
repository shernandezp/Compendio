namespace Compendio.Domain.Content;

/// <summary>
/// Removes a single top-level key from a front-matter block, by line.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a line edit rather than a parse-and-re-emit. Re-emitting would replace remark's
/// serialization of the whole block with YamlDotNet's, which can differ in quoting and ordering —
/// so removing one key would produce a diff across every line of the front matter, on a page the
/// user had just saved in canonical form.
/// </para>
/// <para>
/// The same reasoning as the checkbox substitution: the server may edit bytes it can identify
/// exactly, and must not re-serialize a document it did not write.
/// </para>
/// </remarks>
public static class FrontMatterLines
{
    private const string Delimiter = "---";

    /// <summary>
    /// Returns <paramref name="rawText"/> without the given key's line, or unchanged if it is absent.
    /// </summary>
    /// <remarks>
    /// Only a simple <c>key: value</c> on one line is removed. A key whose value spans lines — a
    /// block scalar or a nested map — is left alone rather than half-removed, because taking the
    /// first line of a multi-line value would leave orphaned YAML that no longer parses.
    /// </remarks>
    public static string RemoveKey(string rawText, string key)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var document = MarkdownParser.Parse(rawText);
        if (!document.HasFrontMatter)
        {
            return rawText;
        }

        var header = rawText[..document.BodyOffset];
        var lineEnding = document.LineEnding;
        var prefix = key + ":";

        var kept = new List<string>();
        var removed = false;

        foreach (var line in MarkdownDocument.EnumerateLines(header))
        {
            if (!removed &&
                line.StartsWith(prefix, StringComparison.Ordinal) &&
                IsSingleLineValue(line, prefix.Length))
            {
                removed = true;
                continue;
            }

            kept.Add(line);
        }

        if (!removed)
        {
            return rawText;
        }

        // A front-matter block reduced to its two delimiters carries nothing, and leaving an empty
        // `---\n---` at the top of a page is noise a human would delete by hand.
        if (kept.Count(l => l.Trim().Length > 0) <= 2)
        {
            return rawText[document.BodyOffset..];
        }

        return string.Join(lineEnding, kept) + lineEnding + rawText[document.BodyOffset..];
    }

    /// <summary>
    /// A value is single-line unless it opens a block scalar or is empty, which in YAML means the
    /// value is whatever is indented beneath it.
    /// </summary>
    private static bool IsSingleLineValue(string line, int afterKey)
    {
        var value = line[afterKey..].Trim();
        return value.Length > 0 && value is not ("|" or ">" or "|-" or ">-" or "|+" or ">+");
    }

    /// <summary>Whether a line is a front-matter delimiter, for callers checking a block's shape.</summary>
    public static bool IsDelimiter(string line) => line.TrimEnd() == Delimiter;
}
