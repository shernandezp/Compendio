using System.Text.RegularExpressions;
using Compendio.Application.Abstractions;
using Compendio.Domain.Content;

namespace Compendio.Infrastructure.Content;

/// <summary>
/// Markdown → the five columns FTS5 indexes, plus the outbound links backlinks are built from.
/// </summary>
/// <remarks>
/// Keeping extraction in its own step, written to its own table, makes <c>snippet()</c> cheap and
/// makes reindexing a pure function of the file — which is what lets <c>compendio reindex</c> be a
/// safe operation rather than a gamble.
/// </remarks>
public sealed partial class TextExtractor : ITextExtractor
{
    public ExtractedText Extract(string markdown, ContentPath path)
    {
        var document = MarkdownParser.Parse(markdown);
        var title = document.ResolveTitle(path);
        var headings = string.Join('\n', document.Headings());
        var body = MarkdownText.Extract(document.Body);
        var tags = string.Join(' ', document.FrontMatter.NormalizedTags());

        // The path is indexed with its separators turned into spaces so "IT VPN" finds IT/VPN,
        // while the tokenizer's tokenchars keep VPN-Site-A and 192.168.1.1 whole.
        var searchablePath = path.Value.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ');

        return new ExtractedText(title, headings, body, tags, searchablePath, OutboundLinks(document.Body));
    }

    /// <summary>
    /// Wiki links and relative Markdown links, normalized to bare targets.
    /// </summary>
    /// <remarks>
    /// Absolute URLs are skipped: a backlinks panel is about this wiki, and listing every page that
    /// links to <c>example.com</c> is noise.
    /// </remarks>
    private static IReadOnlyList<string> OutboundLinks(string body)
    {
        var links = new List<string>();

        foreach (var match in WikiLinks.MatchesOutsideCode(body))
        {
            var inner = match.Groups[1].Value;
            var pipe = inner.IndexOf('|');
            var target = (pipe >= 0 ? inner[..pipe] : inner).Trim();
            if (target.Length > 0)
            {
                links.Add(Normalize(target));
            }
        }

        foreach (Match match in InlineLinkPattern().Matches(body))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.Length == 0 ||
                target.StartsWith('#') ||
                target.Contains("://", StringComparison.Ordinal) ||
                target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Normalize(target));
        }

        return links.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Normalize(string target)
    {
        var anchor = target.IndexOf('#');
        if (anchor >= 0)
        {
            target = target[..anchor];
        }

        target = target.Trim().TrimStart('/');

        if (target.StartsWith("p/", StringComparison.OrdinalIgnoreCase))
        {
            target = target[2..];
        }

        return target;
    }

    [GeneratedRegex(@"(?<!!)\[[^\]\r\n]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled)]
    private static partial Regex InlineLinkPattern();
}
