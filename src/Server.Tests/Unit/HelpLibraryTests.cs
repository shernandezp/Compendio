using Compendio.Application.Help;
using Compendio.Domain.Localization;
using Compendio.Infrastructure.Help;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// Every guide topic exists, in every shipping language, with a title.
/// </summary>
/// <remarks>
/// <para>
/// The library falls back to English for a topic a language has not translated, which is the right
/// behaviour for a community translation in progress and a silent failure for the languages we
/// ship ourselves. Without this test, adding a topic to <see cref="HelpCatalog"/> and forgetting
/// the Spanish file ships a Spanish instance whose guide is quietly half English.
/// </para>
/// <para>
/// Driven by the catalog and the shipping-language list rather than a hand-kept list of files, so a
/// new topic and a new language are both covered the moment they are declared.
/// </para>
/// </remarks>
public sealed class HelpLibraryTests
{
    private static readonly HelpLibrary Library = new(NullLogger<HelpLibrary>.Instance);

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();

        foreach (var topic in HelpCatalog.Topics)
        {
            foreach (var language in SupportedLanguages.Shipping.Select(l => l.Code))
            {
                data.Add(topic.Slug, language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryTopicIsWrittenInEveryShippingLanguage(string slug, string language)
    {
        var document = Library.Find(slug, language);

        document.ShouldNotBeNull($"'{slug}' has no help file in any language");

        // Falling back is legitimate for a community locale and a bug for one we ship.
        document.IsFallback.ShouldBeFalse(
            $"'Resources/Help/{language}/{slug}.md' is missing, so the topic falls back to English");

        // The title is lifted from the leading '# ' heading; the slug coming back is the signal
        // that the file does not start with one.
        document.Title.ShouldNotBe(slug, $"'Resources/Help/{language}/{slug}.md' has no leading '# ' heading");
        document.Markdown.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ListIsInCatalogOrder()
    {
        Library.List(SupportedLanguages.English)
            .Select(d => d.Slug)
            .ShouldBe(HelpCatalog.Topics.Select(t => t.Slug));
    }

    [Fact]
    public void AnUnknownSlugIsNotFound() =>
        Library.Find("no-such-topic", SupportedLanguages.English).ShouldBeNull();
}
