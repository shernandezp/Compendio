using System.Text;
using System.Text.RegularExpressions;

namespace Compendio.Domain.Content;

/// <summary>
/// Taking an image out of a page, without rewriting the page.
/// </summary>
/// <remarks>
/// <para>
/// Deleting an attachment that a page embeds has to remove both, or one of two bad things is left
/// behind: a page showing a broken image, or a file the folder carries forever. This is the first
/// half.
/// </para>
/// <para>
/// A surgical text edit, in the same spirit as the checkbox substitution: the server is not writing
/// Markdown, it is removing a span it can point at, so the rule that remark in the client is the
/// only serializer survives. Parsing and re-emitting would reformat every line of a page written in
/// VS Code, and "somebody deleted a picture" is not a defensible reason for that diff.
/// </para>
/// <para>
/// Line endings are preserved byte for byte, including a file that uses CRLF, because a page whose
/// every line changed is a page whose history stopped being readable.
/// </para>
/// </remarks>
public static partial class MarkdownImages
{
    /// <summary>
    /// An inline image: <c>![alt](url)</c>, with an optional title after the URL.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A bare URL runs to the first whitespace or <c>)</c>, which is what an
    /// attachment URL is; anything more permissive starts matching across a line and taking
    /// neighbouring text with it. The <c>&lt;…&gt;</c> form is matched too, because that is how
    /// CommonMark spells a URL containing a space and a folder named <c>Router 2</c> produces one.
    /// </remarks>
    [GeneratedRegex(
        """!\[[^\]\n]*\]\(\s*(<[^>\n]*>|[^)\s]+)(?:\s+(?:"[^"\n]*"|'[^'\n]*'|\([^)\n]*\)))?\s*\)""",
        RegexOptions.Compiled)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.Compiled)]
    private static partial Regex RunOfSpaces();

    /// <summary>
    /// Removes every image pointing at <paramref name="url"/>, and returns the page unchanged when
    /// there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Front matter is not scanned: it is somebody's metadata block, and an image is not written
    /// there. Fenced code is not scanned either, for the reason it never is here — a page that
    /// documents how Compendio embeds an image contains that Markdown inside a fence, and editing
    /// it there deletes the example rather than an image.
    /// </para>
    /// <para>
    /// Links are left alone. <c>[the diagram](…)</c> is a sentence somebody wrote, and taking their
    /// words along with the file would be a larger edit than the one that was asked for; a link to
    /// a file that is gone says what happened, while a missing paragraph does not.
    /// </para>
    /// </remarks>
    public static string RemoveReferencesTo(string markdown, string url)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(url);

        var document = MarkdownParser.Parse(markdown);
        var prefix = document.RawText[..document.BodyOffset];

        // Split on '\n' only, so a '\r' stays part of the line it belongs to and comes back out
        // with it. Splitting on the detected line ending would rewrite a file with mixed endings.
        var lines = document.Body.Split('\n');
        var kept = new List<string>(lines.Length);
        var target = Decode(url);
        var changed = false;

        string? fence = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }

                kept.Add(line);
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[..3];
                kept.Add(line);
                continue;
            }

            var stripped = ImagePattern().Replace(line, match => Decode(match.Groups[1].Value) == target ? string.Empty : match.Value);

            if (stripped == line)
            {
                kept.Add(line);
                continue;
            }

            changed = true;

            // What is left of an edited line is tidied rather than left with the hole in it: two
            // spaces at the end of a line are a hard break in Markdown, and the gap the image left
            // behind would become one.
            //
            // Indentation is held out of that, and only the text after it is tidied. Collapsing
            // leading spaces would renumber a nested list item, and adding one to a three-space
            // indent would turn the line into an indented code block — both of them silent, and
            // both of them a bigger change than removing a picture.
            var indent = Math.Min(line.Length - line.TrimStart().Length, stripped.Length);
            var carriageReturn = line.EndsWith('\r') ? "\r" : string.Empty;

            var tidied = stripped[..indent] + RunOfSpaces().Replace(stripped[indent..], " ").Trim() + carriageReturn;

            if (tidied.Trim().Length > 0)
            {
                kept.Add(tidied);
                continue;
            }

            // The image was the whole line. Being its own paragraph it had a blank line on each
            // side, and leaving both would open a two-line gap where the picture used to be. The
            // top of the body counts as one of those sides.
            var before = kept.Count == 0 ? string.Empty : kept[^1].Trim();

            if (before.Length == 0 && index + 1 < lines.Length && lines[index + 1].Trim().Length == 0)
            {
                index++;
            }
        }

        return changed ? prefix + string.Join('\n', kept) : markdown;
    }

    /// <summary>
    /// Compares URLs by what they mean rather than by how they are spelled: the editor writes them
    /// percent-encoded, and a page written in VS Code has the characters themselves.
    /// </summary>
    private static string Decode(string url)
    {
        var raw = url.Trim();

        if (raw.StartsWith('<') && raw.EndsWith('>'))
        {
            raw = raw[1..^1];
        }

        return Uri.UnescapeDataString(raw);
    }

    /// <summary>The URL a page uses to show an attachment, in its unencoded form.</summary>
    public static string UrlFor(ContentPath attachment) =>
        $"/api/v1/attachments/{attachment.Value}";

    /// <summary>UTF-8 without a BOM, matching every other writer of page bytes.</summary>
    public static byte[] ToBytes(string markdown) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(markdown);
}
