using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Compendio.Domain.Content;

/// <summary>
/// Splits a page into front matter and body, and emits front matter back.
/// </summary>
/// <remarks>
/// The emitter is used for pages the <em>server</em> creates — templates, the seeded first page,
/// the CLI import path. Pages a human edits are written by remark in the client, which is the only
/// Markdown serializer in the product.
/// </remarks>
public static class MarkdownParser
{
    private const string Delimiter = "---";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .WithIndentedSequences()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
        .Build();

    public static MarkdownDocument Parse(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var lineEnding = DetectLineEnding(rawText);
        var text = rawText.Length > 0 && rawText[0] == '﻿' ? rawText[1..] : rawText;
        var bomOffset = rawText.Length - text.Length;

        if (!TryFindFrontMatter(text, out var yaml, out var bodyStart))
        {
            return new MarkdownDocument
            {
                RawText = rawText,
                FrontMatter = FrontMatter.Empty,
                Body = text,
                BodyOffset = bomOffset,
                LineEnding = lineEnding,
            };
        }

        return new MarkdownDocument
        {
            RawText = rawText,
            FrontMatter = ParseFrontMatter(yaml),
            Body = text[bodyStart..],
            BodyOffset = bomOffset + bodyStart,
            LineEnding = lineEnding,
        };
    }

    /// <summary>
    /// Parses a YAML block. Malformed YAML yields <see cref="FrontMatter.Empty"/> rather than an
    /// exception: a page with a broken front-matter block is still a page, and refusing to read it
    /// would take content offline over a stray colon. <c>doctor</c> reports the parse failure.
    /// </summary>
    public static FrontMatter ParseFrontMatter(string yaml)
    {
        Dictionary<object, object?>? map;
        try
        {
            map = Deserializer.Deserialize<Dictionary<object, object?>>(yaml);
        }
        catch (YamlException)
        {
            return FrontMatter.Empty;
        }

        if (map is null || map.Count == 0)
        {
            return FrontMatter.Empty;
        }

        var known = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<KeyValuePair<string, object?>>();

        foreach (var (rawKey, value) in map)
        {
            var key = rawKey?.ToString() ?? string.Empty;
            if (FrontMatter.KnownKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                known[key] = value;
            }
            else
            {
                extra.Add(new KeyValuePair<string, object?>(key, value));
            }
        }

        return new FrontMatter
        {
            Title = AsString(known, "title"),
            Lang = NormalizeLang(AsString(known, "lang")),
            TranslationKey = AsString(known, "translationKey"),
            TranslationOf = AsString(known, "translationOf"),
            Owner = AsString(known, "owner"),
            Tags = AsStringList(known, "tags"),
            ReviewIntervalDays = AsInt(known, "reviewIntervalDays"),
            NextReviewDate = AsDate(known, "nextReviewDate"),
            RequiresAcknowledgment = AsBool(known, "requiresAcknowledgment"),
            MachineTranslated = AsBool(known, "machineTranslated"),
            Extra = extra,
        };
    }

    /// <summary>
    /// Emits a front-matter block with a stable key order — known keys first, in the order
    /// <see cref="FrontMatter.KnownKeys"/> declares, then unknown keys in the order they arrived.
    /// A stable order is what keeps a metadata-only change from producing a whole-block diff.
    /// </summary>
    public static string Emit(FrontMatter frontMatter, string lineEnding)
    {
        var ordered = new Dictionary<string, object?>(StringComparer.Ordinal);

        void Add(string key, object? value)
        {
            if (value is not null)
            {
                ordered[key] = value;
            }
        }

        Add("title", frontMatter.Title);
        Add("lang", frontMatter.Lang);
        Add("translationKey", frontMatter.TranslationKey);
        Add("translationOf", frontMatter.TranslationOf);
        if (frontMatter.Tags.Count > 0)
        {
            ordered["tags"] = frontMatter.Tags.ToList();
        }

        Add("owner", frontMatter.Owner);
        Add("reviewIntervalDays", frontMatter.ReviewIntervalDays);
        Add("nextReviewDate", FrontMatter.FormatDate(frontMatter.NextReviewDate));
        Add("requiresAcknowledgment", frontMatter.RequiresAcknowledgment);
        Add("machineTranslated", frontMatter.MachineTranslated);

        foreach (var (key, value) in frontMatter.Extra)
        {
            ordered.TryAdd(key, value);
        }

        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var yaml = Serializer.Serialize(ordered).TrimEnd('\r', '\n');

        var builder = new StringBuilder();
        builder.Append(Delimiter).Append(lineEnding);
        foreach (var line in MarkdownDocument.EnumerateLines(yaml))
        {
            builder.Append(line).Append(lineEnding);
        }

        builder.Append(Delimiter).Append(lineEnding);
        return builder.ToString();
    }

    /// <summary>Composes a complete page from front matter and a body.</summary>
    public static string Compose(FrontMatter frontMatter, string body, string lineEnding)
    {
        var header = Emit(frontMatter, lineEnding);
        if (header.Length == 0)
        {
            return body;
        }

        var separator = body.StartsWith(lineEnding, StringComparison.Ordinal) ? string.Empty : lineEnding;
        return header + separator + body;
    }

    internal static string DetectLineEnding(string text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return "\n";
        }

        return newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
    }

    private static bool TryFindFrontMatter(string text, out string yaml, out int bodyStart)
    {
        yaml = string.Empty;
        bodyStart = 0;

        if (!text.StartsWith(Delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var afterOpen = Delimiter.Length;
        // The opening delimiter must be alone on its line.
        while (afterOpen < text.Length && (text[afterOpen] == ' ' || text[afterOpen] == '\t'))
        {
            afterOpen++;
        }

        if (afterOpen >= text.Length || text[afterOpen] is not ('\r' or '\n'))
        {
            return false;
        }

        if (text[afterOpen] == '\r')
        {
            afterOpen++;
        }

        if (afterOpen >= text.Length || text[afterOpen] != '\n')
        {
            return false;
        }

        afterOpen++;

        var index = afterOpen;
        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            var hasNewline = lineEnd >= 0;
            var end = hasNewline ? lineEnd : text.Length;
            var trimEnd = end > index && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[index..trimEnd];

            if (line.TrimEnd() == Delimiter)
            {
                yaml = text[afterOpen..index];
                bodyStart = hasNewline ? lineEnd + 1 : text.Length;
                return true;
            }

            if (!hasNewline)
            {
                break;
            }

            index = lineEnd + 1;
        }

        // An unterminated block is not front matter; treat the whole file as body.
        return false;
    }

    private static string? AsString(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null
            ? value.ToString() is { Length: > 0 } s ? s : null
            : null;

    private static int? AsInt(Dictionary<string, object?> map, string key) =>
        AsString(map, key) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : null;

    private static bool? AsBool(Dictionary<string, object?> map, string key) =>
        AsString(map, key) is { } s && bool.TryParse(s, out var b) ? b : null;

    private static DateTimeOffset? AsDate(Dictionary<string, object?> map, string key) =>
        AsString(map, key) is { } s &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    /// <summary>Accepts both <c>tags: [a, b]</c> and <c>tags: "a, b"</c> — real files have both.</summary>
    private static IReadOnlyList<string> AsStringList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is IEnumerable<object?> sequence)
        {
            return sequence.Select(v => v?.ToString()?.Trim() ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToArray();
        }

        return (value.ToString() ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NormalizeLang(string? lang) =>
        string.IsNullOrWhiteSpace(lang) ? null : lang.Trim().ToLowerInvariant();
}
