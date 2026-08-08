using System.Reflection;
using Compendio.Api.Common;
using Compendio.Application.Common;
using Compendio.Domain.Localization;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// Every stable problem code has a localized title and detail, in every shipping language.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LocalizedText"/> falls back to returning the key when a resource is missing, which is
/// the right behaviour at runtime — an error with an ugly title still tells you the code — and a
/// silent failure at build time. Without this test, adding a problem code and forgetting its two
/// resource entries ships a dialog that says <c>ai.disabled.title</c> to a user.
/// </para>
/// <para>
/// Driven by reflection over <see cref="ProblemCodes"/> rather than a hand-kept list, so a new code
/// is covered the moment it is declared.
/// </para>
/// </remarks>
public sealed class ProblemCodeLocalizationTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();

        var codes = typeof(ProblemCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        foreach (var code in codes)
        {
            foreach (var language in SupportedLanguages.Shipping.Select(l => l.Code))
            {
                data.Add(code, language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryCodeHasATitleAndADetail(string code, string language)
    {
        var titleKey = $"{code}.title";
        var detailKey = $"{code}.detail";

        // The key coming back unchanged *is* the missing-resource signal.
        LocalizedText.Get(titleKey, language)
            .ShouldNotBe(titleKey, $"'{titleKey}' is missing from Strings{Suffix(language)}.resx");

        LocalizedText.Get(detailKey, language)
            .ShouldNotBe(detailKey, $"'{detailKey}' is missing from Strings{Suffix(language)}.resx");
    }

    private static string Suffix(string language) =>
        language == SupportedLanguages.English ? string.Empty : $".{language}";
}
