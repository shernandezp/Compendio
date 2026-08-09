namespace Compendio.Application.Help;

/// <param name="Admin">Shown only to administrators. Not a secret — just noise for everyone else.</param>
public enum HelpAudience
{
    Everyone,
    Admin,
}

public sealed record HelpTopic(string Slug, HelpAudience Audience);

/// <summary>
/// The built-in user guide, in order.
/// </summary>
/// <remarks>
/// <para>
/// The order and the audience live here rather than in the file names, so a translator renaming a
/// file cannot reorder the guide or promote a topic to the administrator section. A slug maps to
/// <c>Resources/Help/&lt;language&gt;/&lt;slug&gt;.md</c>, and a language with no file for a slug
/// falls back to English rather than losing the topic.
/// </para>
/// <para>
/// This is deliberately not wiki content. Seeding these as pages would put them in the customer's
/// content folder, where they can be edited into something that no longer describes the product,
/// and every instance would carry a copy that goes stale at its own pace.
/// </para>
/// </remarks>
public static class HelpCatalog
{
    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        new("getting-started", HelpAudience.Everyone),
        new("finding-pages", HelpAudience.Everyone),
        new("reading-pages", HelpAudience.Everyone),
        new("writing-pages", HelpAudience.Everyone),
        new("organizing-pages", HelpAudience.Everyone),
        new("history-and-versions", HelpAudience.Everyone),
        new("reviews-and-acknowledgments", HelpAudience.Everyone),
        new("your-account", HelpAudience.Everyone),
        new("ai-assistant", HelpAudience.Everyone),
        new("admin-people-and-groups", HelpAudience.Admin),
        new("admin-access", HelpAudience.Admin),
        new("admin-encrypted-folders", HelpAudience.Admin),
        new("admin-ai", HelpAudience.Admin),
        new("admin-maintenance", HelpAudience.Admin),
    ];

    public static HelpTopic? Find(string? slug) =>
        Topics.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));
}
