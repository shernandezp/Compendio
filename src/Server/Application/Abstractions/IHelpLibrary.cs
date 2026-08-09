namespace Compendio.Application.Abstractions;

/// <param name="Title">The document's first heading, lifted out so it is not rendered twice.</param>
/// <param name="Markdown">The body, with that heading removed.</param>
/// <param name="Language">The language actually served, which is not always the one asked for.</param>
/// <param name="IsFallback">True when the requested language had no version of this topic.</param>
public sealed record HelpDocument(
    string Slug,
    string Title,
    string Markdown,
    string Language,
    bool IsFallback);

/// <summary>
/// The built-in user guide, as Markdown shipped inside the binary.
/// </summary>
/// <remarks>
/// Markdown files rather than resource strings: a guide is prose with headings, tables and code
/// blocks, and a translator should be able to open the file and read it in context. They are
/// embedded resources so that a single-file deployment still has its documentation — a help button
/// that 404s because somebody copied only the executable is worse than no help button.
/// </remarks>
public interface IHelpLibrary
{
    /// <summary>Every topic that exists for this language, English-filled and in catalog order.</summary>
    IReadOnlyList<HelpDocument> List(string language);

    /// <summary>Null when the slug is not in the catalog or has no file in any language.</summary>
    HelpDocument? Find(string slug, string language);
}
