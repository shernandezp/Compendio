using System.Text;

namespace Compendio.Domain.Content;

/// <summary>
/// A parsed page: its front matter, its Markdown body, and enough of the original text to write it
/// back unchanged.
/// </summary>
/// <remarks>
/// The server never re-serializes Markdown. It parses to read metadata and to extract search text;
/// the client's remark instance is the only writer. <see cref="RawText"/> is therefore the truth
/// and the split below is a view over it.
/// </remarks>
public sealed record MarkdownDocument
{
    public required string RawText { get; init; }

    public required FrontMatter FrontMatter { get; init; }

    /// <summary>The document with its front-matter block removed.</summary>
    public required string Body { get; init; }

    /// <summary>Offset in <see cref="RawText"/> where <see cref="Body"/> starts.</summary>
    public required int BodyOffset { get; init; }

    /// <summary>The line ending the file uses. Preserved by every write.</summary>
    public required string LineEnding { get; init; }

    public bool HasFrontMatter => BodyOffset > 0;

    /// <summary>
    /// Title, in the order the spec fixes: front matter, then the first ATX heading, then the file
    /// name. Never empty — a page with no title is a page nobody can find in a tree.
    /// </summary>
    public string ResolveTitle(ContentPath path)
    {
        if (!string.IsNullOrWhiteSpace(FrontMatter.Title))
        {
            return FrontMatter.Title.Trim();
        }

        var heading = FirstHeading();
        if (heading is not null)
        {
            return heading;
        }

        return Humanize(path.NameWithoutExtension);
    }

    /// <summary>All ATX headings, in document order. Feeds the search index's headings column.</summary>
    public IReadOnlyList<string> Headings()
    {
        var result = new List<string>();
        var inFence = false;
        string? fence = null;

        foreach (var line in EnumerateLines(Body))
        {
            var trimmed = line.TrimStart();

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    inFence = false;
                    fence = null;
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = true;
                fence = trimmed[..3];
                continue;
            }

            if (inFence || !trimmed.StartsWith('#'))
            {
                continue;
            }

            var level = 0;
            while (level < trimmed.Length && trimmed[level] == '#')
            {
                level++;
            }

            if (level is < 1 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            {
                continue;
            }

            var text = trimmed[(level + 1)..].TrimEnd('#', ' ').Trim();
            if (text.Length > 0)
            {
                result.Add(MarkdownText.StripInline(text));
            }
        }

        return result;
    }

    private string? FirstHeading()
    {
        var headings = Headings();
        return headings.Count > 0 ? headings[0] : null;
    }

    private static string Humanize(string fileStem)
    {
        var builder = new StringBuilder(fileStem.Length);
        var capitalize = true;

        foreach (var c in fileStem)
        {
            if (c is '-' or '_')
            {
                builder.Append(' ');
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(c) : c);
            capitalize = false;
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? fileStem : result;
    }

    internal static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            yield return text[start..end];
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
