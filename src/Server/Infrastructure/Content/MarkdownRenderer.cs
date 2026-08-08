using System.Text;
using System.Text.RegularExpressions;
using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Ganss.Xss;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Compendio.Infrastructure.Content;

/// <summary>
/// Markdown → sanitized HTML.
/// </summary>
/// <remarks>
/// <para>
/// Rendering only. Markdig never writes Markdown here and there is no serializer to accidentally
/// reach for: the canonical form is produced by remark in the client, and the server's job is to
/// turn bytes into safe HTML and into search text.
/// </para>
/// <para>
/// The output is sanitized before it leaves this class rather than at each call site. Pages contain
/// content pasted from Word, Confluence and web pages, and a wiki where an editor can inject script
/// into a reader's session is a stored-XSS machine. The CSP is defence in depth on top of this, not
/// instead of it.
/// </para>
/// </remarks>
public sealed partial class MarkdownRenderer : Application.Abstractions.IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseFootnotes()
            .UseFooters()
            .UseCitations()
            .UseAutoLinks()
            .UseEmphasisExtras()
            .UseDefinitionLists()
            .UseCustomContainers()
            .UseAbbreviations()
            .UseListExtras()
            .UseGenericAttributes()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .DisableHtml() // Raw HTML in a page is escaped, not rendered. The sanitizer is the
                           // second line; this is the first, and it keeps pasted Word markup inert.
            .Build();

        _sanitizer = BuildSanitizer();
    }

    public RenderedPage Render(string markdown, ContentPath pagePath, Func<string, string?> linkResolver)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(linkResolver);

        var document = MarkdownParser.Parse(markdown);
        var links = new List<WikiLink>();
        var body = ExpandWikiLinks(document.Body, linkResolver, links);

        var ast = Markdig.Markdown.Parse(body, _pipeline);
        var headings = CollectHeadings(ast);

        var builder = new StringBuilder(body.Length * 2);
        using (var writer = new StringWriter(builder))
        {
            var renderer = new HtmlRenderer(writer);
            _pipeline.Setup(renderer);
            renderer.Render(ast);
            writer.Flush();
        }

        var html = builder.ToString();
        var containsMermaid = html.Contains("language-mermaid", StringComparison.Ordinal);

        if (containsMermaid)
        {
            html = PromoteMermaidBlocks(html);
        }

        return new RenderedPage(Sanitize(html), headings, links, containsMermaid);
    }

    public string Sanitize(string html) => _sanitizer.Sanitize(html);

    /// <summary>
    /// Rewrites <c>[[Target]]</c> and <c>[[Target|Label]]</c> into ordinary links.
    /// </summary>
    /// <remarks>
    /// The resolver is permission-aware, so a link to a page the reader cannot see comes back
    /// unresolved and renders as a "page does not exist" marker. Rendering it as a working link
    /// would turn every page into a page-name oracle, which is the same leak the tree and search
    /// are careful about.
    /// </remarks>
    private static string ExpandWikiLinks(string body, Func<string, string?> resolver, List<WikiLink> links)
    {
        return WikiLinks.ReplaceOutsideCode(body, match =>
        {
            var inner = match.Groups[1].Value;
            var pipe = inner.IndexOf('|');
            var target = (pipe >= 0 ? inner[..pipe] : inner).Trim();
            var label = (pipe >= 0 ? inner[(pipe + 1)..] : inner).Trim();

            var resolved = resolver(target);
            links.Add(new WikiLink(target, resolved, label));

            var escapedLabel = EscapeMarkdownText(label);

            return resolved is null
                ? $"[{escapedLabel}](# \"compendio-unresolved\"){{.compendio-link-unresolved}}"
                : $"[{escapedLabel}](/p/{Uri.EscapeDataString(resolved).Replace("%2F", "/", StringComparison.Ordinal)})";
        });
    }

    private static string EscapeMarkdownText(string text) =>
        text.Replace("[", @"\[", StringComparison.Ordinal)
            .Replace("]", @"\]", StringComparison.Ordinal);

    /// <summary>
    /// Turns a <c>mermaid</c> code fence into <c>&lt;pre class="mermaid"&gt;</c> for the client.
    /// </summary>
    /// <remarks>
    /// The diagram source stays escaped text until the client renders it, and the client renders it
    /// with <c>securityLevel: 'strict'</c> — diagram source is user-authored content and Mermaid has
    /// had a CSS-injection advisory, so it is untrusted input like any other page content.
    /// </remarks>
    private static string PromoteMermaidBlocks(string html) =>
        MermaidBlockPattern().Replace(html, m => $"<pre class=\"mermaid\">{m.Groups[1].Value}</pre>");

    private static List<HeadingAnchor> CollectHeadings(Markdig.Syntax.MarkdownDocument ast)
    {
        var headings = new List<HeadingAnchor>();

        foreach (var block in ast.Descendants<HeadingBlock>())
        {
            var text = block.Inline is null ? string.Empty : InlineText(block.Inline);
            var id = block.GetAttributes().Id ?? Slug.Create(text);
            headings.Add(new HeadingAnchor(block.Level, text, id));
        }

        return headings;
    }

    private static string InlineText(Markdig.Syntax.Inlines.ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container.Descendants())
        {
            switch (inline)
            {
                case Markdig.Syntax.Inlines.LiteralInline literal:
                    builder.Append(literal.Content.AsSpan());
                    break;
                case Markdig.Syntax.Inlines.CodeInline code:
                    builder.Append(code.Content);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// The allowlist. Everything not named here is removed, including every event-handler attribute
    /// and every scheme that is not http, https, mailto or a site-relative path.
    /// </summary>
    private static HtmlSanitizer BuildSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
                 {
                     "p", "br", "hr", "div", "span", "blockquote", "pre", "code",
                     "h1", "h2", "h3", "h4", "h5", "h6",
                     "ul", "ol", "li", "dl", "dt", "dd",
                     "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
                     "a", "img", "figure", "figcaption",
                     "strong", "em", "b", "i", "u", "s", "del", "ins", "mark", "sub", "sup",
                     "abbr", "kbd", "samp", "var", "small", "cite", "q",
                     "input", "section", "aside", "details", "summary",
                 })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[]
                 {
                     "href", "src", "alt", "title", "class", "id", "colspan", "rowspan",
                     "start", "type", "checked", "disabled", "width", "height", "loading", "dir", "lang",
                 })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        // No inline style at all: it is the vector behind the Mermaid CSS-injection advisory, and
        // nothing in a Markdown page legitimately needs it.
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowDataAttributes = false;
        sanitizer.KeepChildNodes = true;

        // data: is allowed for images only — a pasted inline image is normal; a data: URL in an
        // <a href> is a navigation primitive.
        sanitizer.AllowedSchemes.Add("data");
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !e.OriginalUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;
            }
        };

        return sanitizer;
    }

    [GeneratedRegex(@"<pre><code class=""language-mermaid"">(.*?)</code></pre>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex MermaidBlockPattern();
}

/// <summary>
/// Finding <c>[[wiki links]]</c> in a body, everywhere except inside fenced code.
/// </summary>
/// <remarks>
/// Shared by the renderer and the text extractor so a link is a link, or is not one, in both. Code
/// fences are excluded because an IT wiki documents its own syntax: a page explaining how to write
/// <c>[[Runbook]]</c> shows it inside a fence, and rewriting it there turns the example into a link
/// and the code block into something that no longer says what the author wrote.
/// </remarks>
public static partial class WikiLinks
{
    [GeneratedRegex(@"\[\[([^\]\r\n]+)\]\]", RegexOptions.Compiled)]
    public static partial Regex Pattern();

    public static string ReplaceOutsideCode(string body, MatchEvaluator evaluator)
    {
        var builder = new StringBuilder(body.Length);

        foreach (var (line, isCode, ending) in EnumerateLines(body))
        {
            builder.Append(isCode ? line : Pattern().Replace(line, evaluator)).Append(ending);
        }

        return builder.ToString();
    }

    public static IEnumerable<Match> MatchesOutsideCode(string body)
    {
        foreach (var (line, isCode, _) in EnumerateLines(body))
        {
            if (isCode)
            {
                continue;
            }

            foreach (Match match in Pattern().Matches(line))
            {
                yield return match;
            }
        }
    }

    /// <summary>Lines with their original endings, flagged as fence or fenced content.</summary>
    private static IEnumerable<(string Line, bool IsCode, string Ending)> EnumerateLines(string body)
    {
        string? fence = null;
        var start = 0;

        while (start <= body.Length)
        {
            var newline = body.IndexOf('\n', start);
            var end = newline < 0 ? body.Length : newline;
            var lineEnd = end > start && body[end - 1] == '\r' ? end - 1 : end;

            var line = body[start..lineEnd];
            var ending = body[lineEnd..end];
            var trimmed = line.TrimStart();

            var isCode = fence is not null;

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
            }
            else if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[..3];
                isCode = true;
            }

            if (newline < 0)
            {
                if (line.Length > 0)
                {
                    yield return (line, isCode, string.Empty);
                }

                yield break;
            }

            yield return (line, isCode, ending + "\n");
            start = newline + 1;
        }
    }
}
