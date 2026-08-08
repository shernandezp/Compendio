using System.Text;

namespace Compendio.Domain.Content;

/// <summary>
/// Markdown → plain text, for the search index.
/// </summary>
/// <remarks>
/// <para>
/// This is not a Markdown parser and deliberately not one: it strips syntax so FTS5 indexes words
/// rather than punctuation. Getting it slightly wrong costs a slightly worse snippet; pulling in a
/// full AST walk here would put a second Markdown implementation in the server, which the canonical
/// -Markdown decision exists to avoid.
/// </para>
/// <para>
/// What survives, per <c>design/search.md</c> §2: heading text, body prose, code-fence contents as
/// text, link <em>text</em> (targets dropped), and image alt text.
/// </para>
/// </remarks>
public static class MarkdownText
{
    /// <summary>Plain text of a whole document body, one block per line.</summary>
    public static string Extract(string body)
    {
        var builder = new StringBuilder(body.Length);
        string? fence = null;

        foreach (var raw in MarkdownDocument.EnumerateLines(body))
        {
            var line = raw;
            var trimmed = line.TrimStart();

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
                else
                {
                    // Code is content in an IT wiki: a config snippet is exactly what people search for.
                    builder.AppendLine(line);
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[..3];
                continue;
            }

            // Block markers: headings, quotes, list bullets, task boxes, table pipes, rules.
            var span = trimmed.AsSpan();
            span = span.TrimStart('#');
            span = span.TrimStart('>');
            span = span.TrimStart();

            if (span.Length > 0 && (span[0] is '-' or '*' or '+') && span.Length > 1 && span[1] == ' ')
            {
                span = span[2..];
            }

            if (span.StartsWith("[ ] ") || span.StartsWith("[x] ") || span.StartsWith("[X] "))
            {
                span = span[4..];
            }

            var text = span.ToString();

            if (IsThematicBreak(text) || IsTableDelimiter(text))
            {
                continue;
            }

            text = text.Replace('|', ' ');

            var stripped = StripInline(text);
            if (stripped.Length > 0)
            {
                builder.AppendLine(stripped);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes inline syntax from a single line: emphasis, code spans, links, images, wiki links,
    /// HTML tags and footnote markers.
    /// </summary>
    public static string StripInline(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            switch (c)
            {
                case '\\' when i + 1 < text.Length:
                    builder.Append(text[i + 1]);
                    i += 2;
                    continue;

                case '`':
                    // Code span: keep the contents, drop the backticks.
                    var ticks = 0;
                    while (i + ticks < text.Length && text[i + ticks] == '`')
                    {
                        ticks++;
                    }

                    var closing = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                    if (closing < 0)
                    {
                        i += ticks;
                        continue;
                    }

                    builder.Append(text.AsSpan(i + ticks, closing - i - ticks));
                    i = closing + ticks;
                    continue;

                case '!' when i + 1 < text.Length && text[i + 1] == '[':
                    // Image: keep the alt text.
                    i++;
                    continue;

                case '[':
                    if (text.AsSpan(i).StartsWith("[["))
                    {
                        // Wiki link: [[Target|Label]] keeps Label, [[Target]] keeps Target.
                        var end = text.IndexOf("]]", i, StringComparison.Ordinal);
                        if (end < 0)
                        {
                            i += 2;
                            continue;
                        }

                        var inner = text[(i + 2)..end];
                        var pipe = inner.IndexOf('|');
                        builder.Append(pipe >= 0 ? inner[(pipe + 1)..] : inner);
                        i = end + 2;
                        continue;
                    }

                    if (text.AsSpan(i).StartsWith("[^"))
                    {
                        // Footnote reference — a marker, not words.
                        var close = text.IndexOf(']', i);
                        i = close < 0 ? text.Length : close + 1;
                        continue;
                    }

                    i++;
                    continue;

                case ']':
                    // Drop the URL that follows a link label.
                    if (i + 1 < text.Length && text[i + 1] == '(')
                    {
                        var depth = 0;
                        var j = i + 1;
                        for (; j < text.Length; j++)
                        {
                            if (text[j] == '(')
                            {
                                depth++;
                            }
                            else if (text[j] == ')')
                            {
                                depth--;
                                if (depth == 0)
                                {
                                    break;
                                }
                            }
                        }

                        i = j < text.Length ? j + 1 : text.Length;
                        continue;
                    }

                    i++;
                    continue;

                case '<':
                    // HTML tag or autolink. An autolink's URL is worth indexing; a tag is not.
                    var gt = text.IndexOf('>', i);
                    if (gt < 0)
                    {
                        builder.Append(c);
                        i++;
                        continue;
                    }

                    var tag = text[(i + 1)..gt];
                    if (tag.Contains("://", StringComparison.Ordinal) || tag.Contains('@'))
                    {
                        builder.Append(tag);
                    }

                    i = gt + 1;
                    continue;

                case '*':
                case '_':
                case '~':
                case '=':
                    i++;
                    continue;

                default:
                    builder.Append(c);
                    i++;
                    continue;
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(c);
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsThematicBreak(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        var first = trimmed[0];
        if (first is not ('-' or '*' or '_'))
        {
            return false;
        }

        return trimmed.All(c => c == first || c == ' ');
    }

    private static bool IsTableDelimiter(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || !trimmed.Contains('-'))
        {
            return false;
        }

        return trimmed.All(c => c is '-' or ':' or '|' or ' ');
    }
}
