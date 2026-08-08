using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

/// <param name="Level">1–6.</param>
public sealed record HeadingAnchor(int Level, string Text, string Anchor);

/// <param name="Target">Resolved content path, or null when the link does not resolve.</param>
public sealed record WikiLink(string RawTarget, string? Target, string Label);

/// <param name="Html">Sanitized. Safe to inject.</param>
public sealed record RenderedPage(
    string Html,
    IReadOnlyList<HeadingAnchor> Headings,
    IReadOnlyList<WikiLink> Links,
    bool ContainsMermaid);

/// <summary>
/// Markdown → HTML, server-side.
/// </summary>
/// <remarks>
/// <para>
/// Rendering only. There is deliberately no Markdown <em>serializer</em> on the server: remark in
/// the client is the only writer, which removes an entire class of client/server disagreement about
/// what a document means.
/// </para>
/// <para>
/// The output is sanitized before it leaves here. Pages contain pasted content, and a wiki where an
/// editor can inject script into a reader's session is a stored-XSS machine. The CSP is defence in
/// depth on top, not the primary control.
/// </para>
/// </remarks>
public interface IMarkdownRenderer
{
    /// <param name="linkResolver">
    /// Resolves a <c>[[wiki link]]</c> target to a content path, or returns null so the link is
    /// rendered as unresolved. The resolver is permission-aware: a link to a page the reader cannot
    /// see resolves to null, so the rendered page does not become a page-name oracle.
    /// </param>
    RenderedPage Render(string markdown, ContentPath pagePath, Func<string, string?> linkResolver);

    /// <summary>Sanitizes already-rendered HTML. Used by the rendered-diff view.</summary>
    string Sanitize(string html);
}

/// <summary>Markdown → plain text for the search index.</summary>
public interface ITextExtractor
{
    /// <param name="markdown">The whole file, front matter included.</param>
    ExtractedText Extract(string markdown, ContentPath path);
}

public sealed record ExtractedText(
    string Title,
    string Headings,
    string Body,
    string Tags,
    string Path,
    IReadOnlyList<string> OutboundLinks);
