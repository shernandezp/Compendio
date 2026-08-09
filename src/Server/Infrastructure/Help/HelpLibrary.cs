using System.Collections.Frozen;
using System.Reflection;
using Compendio.Application.Abstractions;
using Compendio.Application.Help;
using Compendio.Domain.Localization;

namespace Compendio.Infrastructure.Help;

/// <summary>
/// Reads the guide out of the assembly once, at startup.
/// </summary>
/// <remarks>
/// <para>
/// The whole guide is a few dozen kilobytes of text that never changes between deployments, so it
/// is loaded eagerly into a frozen dictionary and served from memory. No file watching, no cache
/// invalidation, no disk read per request.
/// </para>
/// <para>
/// Resource names come from the manifest rather than being constructed from the catalog, so a
/// translator who adds <c>Resources/Help/ca/*.md</c> gets a Catalan guide with no code change —
/// which is the same promise <c>docs/translating.md</c> makes about every other localized surface.
/// </para>
/// </remarks>
public sealed class HelpLibrary : IHelpLibrary
{
    private const string Prefix = "Compendio.Resources.Help.";
    private const string Suffix = ".md";

    /// <summary>Keyed by <c>language/slug</c>, both lowercase.</summary>
    private readonly FrozenDictionary<string, HelpDocument> _documents;

    public HelpLibrary(ILogger<HelpLibrary> logger)
    {
        var assembly = typeof(HelpLibrary).Assembly;
        var loaded = new Dictionary<string, HelpDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal) ||
                !resource.EndsWith(Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var middle = resource[Prefix.Length..^Suffix.Length];
            var separator = middle.IndexOf('.');
            if (separator <= 0)
            {
                continue;
            }

            var language = middle[..separator];
            var slug = middle[(separator + 1)..];

            if (HelpCatalog.Find(slug) is null)
            {
                // A file with no catalog entry is unreachable rather than silently appended: the
                // order and the audience are decisions, not a side effect of what is on disk.
                logger.LogWarning("Help file '{Slug}' ({Language}) is not in the catalog and will not be shown.",
                    slug, language);
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var raw = reader.ReadToEnd();
            var (title, body) = SplitTitle(raw, slug);

            loaded[Key(language, slug)] = new HelpDocument(slug, title, body, language, IsFallback: false);
        }

        _documents = loaded.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        logger.LogInformation("Loaded {Count} help documents.", _documents.Count);
    }

    public IReadOnlyList<HelpDocument> List(string language) =>
        HelpCatalog.Topics
            .Select(topic => Resolve(topic.Slug, language))
            .OfType<HelpDocument>()
            .ToArray();

    public HelpDocument? Find(string slug, string language) =>
        HelpCatalog.Find(slug) is { } topic ? Resolve(topic.Slug, language) : null;

    /// <summary>
    /// The requested language, then English.
    /// </summary>
    /// <remarks>
    /// A half-finished translation shows the translated topics in the reader's language and the
    /// rest in English, flagged. The alternative — hiding untranslated topics — turns a partial
    /// translation into missing documentation, which is the worse failure.
    /// </remarks>
    private HelpDocument? Resolve(string slug, string language)
    {
        if (_documents.TryGetValue(Key(language, slug), out var exact))
        {
            return exact;
        }

        if (!string.Equals(language, SupportedLanguages.Fallback, StringComparison.OrdinalIgnoreCase) &&
            _documents.TryGetValue(Key(SupportedLanguages.Fallback, slug), out var fallback))
        {
            return fallback with { IsFallback = true };
        }

        return null;
    }

    /// <summary>
    /// Lifts the leading <c>#</c> heading out of the body.
    /// </summary>
    /// <remarks>
    /// The client renders the title as the page heading, so leaving it in the Markdown too would
    /// show it twice. A file with no leading heading falls back to its slug, which is ugly enough
    /// to get noticed and fixed without being a build break in somebody's translation PR.
    /// </remarks>
    private static (string Title, string Body) SplitTitle(string markdown, string slug)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿', '\n', ' ');

        if (!text.StartsWith("# ", StringComparison.Ordinal))
        {
            return (slug, text);
        }

        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return (text[2..].Trim(), string.Empty);
        }

        return (text[2..newline].Trim(), text[(newline + 1)..].TrimStart('\n'));
    }

    private static string Key(string language, string slug) => $"{language}/{slug}";
}
