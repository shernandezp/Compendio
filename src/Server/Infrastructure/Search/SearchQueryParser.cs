using System.Globalization;
using System.Text;

namespace Compendio.Infrastructure.Search;

/// <param name="Match">
/// The FTS5 <c>MATCH</c> expression, already quoted. Empty when the query was only filters.
/// </param>
public sealed record ParsedQuery
{
    public string Match { get; init; } = string.Empty;

    public string? Tag { get; init; }

    public string? PathPrefix { get; init; }

    public string? Owner { get; init; }

    public string? Lang { get; init; }

    public DateTimeOffset? UpdatedAfter { get; init; }

    public DateTimeOffset? UpdatedBefore { get; init; }

    public bool HasText => Match.Length > 0;

    public bool IsEmpty => !HasText && Tag is null && PathPrefix is null && Owner is null && Lang is null
                           && UpdatedAfter is null && UpdatedBefore is null;
}

/// <summary>
/// Turns what a person typed into a safe <c>MATCH</c> expression plus structured filters.
/// </summary>
/// <remarks>
/// <para>
/// User input never reaches <c>MATCH</c> unquoted. An unbalanced quote returning a 500 is a bad
/// look, FTS5's own syntax errors are unhelpful, and a search box that can be made to error is a
/// search box that can be probed. Every term here is emitted double-quoted with internal quotes
/// doubled, so there is no expression a user can type that changes the query's shape.
/// </para>
/// <para>
/// Forgiving by design: an unknown <c>foo:bar</c> prefix is treated as literal text rather than an
/// error, because a person who types <c>ratio:2</c> means to search for it.
/// </para>
/// </remarks>
public sealed class SearchQueryParser(IReadOnlyList<string> synonymGroups)
{
    private static readonly string[] KnownFilters = ["tag", "space", "in", "owner", "lang", "updated"];

    private readonly Dictionary<string, string[]> _synonyms = BuildSynonyms(synonymGroups);

    public ParsedQuery Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ParsedQuery();
        }

        var tokens = Tokenize(input);
        var include = new List<string>();
        var exclude = new List<string>();

        string? tag = null, pathPrefix = null, owner = null, lang = null;
        DateTimeOffset? after = null, before = null;

        foreach (var token in tokens)
        {
            var text = token.Text;

            if (token.IsPhrase)
            {
                (token.IsNegated ? exclude : include).Add(Quote(text));
                continue;
            }

            var colon = text.IndexOf(':');
            if (colon > 0 && colon < text.Length - 1)
            {
                var name = text[..colon].ToLowerInvariant();
                var value = text[(colon + 1)..];

                if (KnownFilters.Contains(name, StringComparer.Ordinal))
                {
                    switch (name)
                    {
                        case "tag":
                            tag = value.Trim().ToLowerInvariant();
                            continue;
                        case "space":
                        case "in":
                            pathPrefix = value.Trim().Trim('/');
                            continue;
                        case "owner":
                            owner = value.Trim();
                            continue;
                        case "lang":
                            lang = value.Trim().ToLowerInvariant();
                            continue;
                        case "updated":
                            ApplyUpdatedFilter(value, ref after, ref before);
                            continue;
                    }
                }
                // Unknown prefix: falls through and is searched for literally.
            }

            (token.IsNegated ? exclude : include).Add(text);
        }

        return new ParsedQuery
        {
            Match = BuildMatch(include, exclude),
            Tag = tag,
            PathPrefix = pathPrefix,
            Owner = owner,
            Lang = lang,
            UpdatedAfter = after,
            UpdatedBefore = before,
        };
    }

    /// <summary>
    /// Prefix-matches the last term.
    /// </summary>
    /// <remarks>
    /// This is the cheap half of the no-stemming decision: <c>servidor*</c> matches
    /// <em>servidores</em>, which covers most of what a Spanish stemmer would have bought, and it is
    /// what a search box does anyway while someone is still typing.
    /// </remarks>
    private string BuildMatch(List<string> include, List<string> exclude)
    {
        if (include.Count == 0 && exclude.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (var i = 0; i < include.Count; i++)
        {
            var term = include[i];
            var isAlreadyQuoted = term.StartsWith('"');
            var isLast = i == include.Count - 1;

            if (builder.Length > 0)
            {
                builder.Append(" AND ");
            }

            if (isAlreadyQuoted)
            {
                builder.Append(term);
                continue;
            }

            if (_synonyms.TryGetValue(term.ToLowerInvariant(), out var group))
            {
                builder.Append('(');
                for (var j = 0; j < group.Length; j++)
                {
                    if (j > 0)
                    {
                        builder.Append(" OR ");
                    }

                    builder.Append(Quote(group[j]));
                }

                builder.Append(')');
                continue;
            }

            builder.Append(Quote(term));
            if (isLast)
            {
                builder.Append('*');
            }
        }

        foreach (var term in exclude)
        {
            if (builder.Length == 0)
            {
                // FTS5 has no bare NOT: an exclusion-only query needs something to subtract from.
                continue;
            }

            builder.Append(" NOT ").Append(term.StartsWith('"') ? term : Quote(term));
        }

        return builder.ToString();
    }

    private static void ApplyUpdatedFilter(string value, ref DateTimeOffset? after, ref DateTimeOffset? before)
    {
        var comparison = '>';
        var text = value;

        if (text.Length > 0 && (text[0] == '>' || text[0] == '<'))
        {
            comparison = text[0];
            text = text[1..];
        }

        if (text.StartsWith('='))
        {
            text = text[1..];
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return;
        }

        if (comparison == '>')
        {
            after = parsed;
        }
        else
        {
            before = parsed;
        }
    }

    /// <summary>Double-quoted, internal quotes doubled. The only way a term reaches SQLite.</summary>
    private static string Quote(string term) =>
        $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var buffer = new StringBuilder();
        var inPhrase = false;
        var negated = false;

        void Flush(bool isPhrase)
        {
            if (buffer.Length > 0)
            {
                tokens.Add(new Token(buffer.ToString(), isPhrase, negated));
                buffer.Clear();
            }

            negated = false;
        }

        foreach (var c in input)
        {
            if (c == '"')
            {
                if (inPhrase)
                {
                    Flush(isPhrase: true);
                    inPhrase = false;
                }
                else
                {
                    Flush(isPhrase: false);
                    inPhrase = true;
                }

                continue;
            }

            if (!inPhrase && char.IsWhiteSpace(c))
            {
                Flush(isPhrase: false);
                continue;
            }

            if (!inPhrase && c == '-' && buffer.Length == 0)
            {
                negated = true;
                continue;
            }

            buffer.Append(c);
        }

        Flush(inPhrase);
        return tokens;
    }

    /// <summary>
    /// Builds the lookup from config lines such as <c>servidor=servidores=server</c>. Every member
    /// maps to the whole group, so any of them finds all of them.
    /// </summary>
    private static Dictionary<string, string[]> BuildSynonyms(IReadOnlyList<string> groups)
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var members = group
                .Split(['=', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => m.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (members.Length < 2)
            {
                continue;
            }

            foreach (var member in members)
            {
                map[member] = members;
            }
        }

        return map;
    }

    private readonly record struct Token(string Text, bool IsPhrase, bool IsNegated);
}
