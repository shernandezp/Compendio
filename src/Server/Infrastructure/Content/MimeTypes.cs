namespace Compendio.Infrastructure.Content;

/// <summary>
/// Extension → content type, plus magic-number sniffing.
/// </summary>
/// <remarks>
/// Both are checked on upload, not either. An extension allowlist alone accepts a renamed
/// executable; sniffing alone accepts a real PNG named <c>.html</c>, which a browser will happily
/// treat as a document. Attachments are also never served from a static file provider, which is the
/// third leg of the same decision.
/// </remarks>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".zip"] = "application/zip",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
    };

    /// <summary>
    /// The first four bytes of a PNG: <c>0x89 'P' 'N' 'G'</c>.
    /// </summary>
    /// <remarks>
    /// A plain byte array rather than a <c>u8</c> literal. <c>"PNG"u8</c> looks like the right
    /// four bytes and is five: <c>u8</c> encodes the string as UTF-8, and U+0089 encodes as
    /// <c>0xC2 0x89</c>. It therefore matches no real PNG, and every pasted screenshot — the
    /// editor's headline feature — came back as "that file type is not allowed".
    /// </remarks>
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G'];

    public static string ForExtension(string extension) =>
        ByExtension.GetValueOrDefault(extension, "application/octet-stream");

    /// <summary>Whether the extension is an image we are willing to render inline.</summary>
    public static bool IsInlineImage(string extension) =>
        extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" or ".bmp";

    /// <summary>
    /// Whether the first bytes are consistent with the declared extension.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: it recognizes the formats worth recognizing and returns <c>true</c> for
    /// everything it has no signature for, because the extension allowlist has already run and this
    /// is the second check, not the only one.
    /// </remarks>
    public static bool MatchesExtension(string extension, ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            // Too short to identify. A four-byte file is not an attack surface worth a false reject.
            return true;
        }

        return extension switch
        {
            ".png" => head.StartsWith(PngSignature),
            ".jpg" or ".jpeg" => head[0] == 0xFF && head[1] == 0xD8,
            ".gif" => head.StartsWith("GIF8"u8),
            ".pdf" => head.StartsWith("%PDF"u8),
            ".webp" => head.StartsWith("RIFF"u8),
            ".zip" or ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" =>
                head[0] == 0x50 && head[1] == 0x4B,
            ".bmp" => head.StartsWith("BM"u8),
            _ => true,
        };
    }

    /// <summary>
    /// Rejects an SVG carrying script. SVG is an XML document that browsers execute, so it is the
    /// one image format that is also an XSS vector.
    /// </summary>
    public static bool IsSafeSvg(ReadOnlySpan<byte> content)
    {
        var text = System.Text.Encoding.UTF8.GetString(content);
        return !text.Contains("<script", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("onload=", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase);
    }
}
