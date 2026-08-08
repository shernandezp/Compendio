using System.Globalization;
using System.Resources;
using Compendio.Domain.Localization;

namespace Compendio.Api.Common;

/// <summary>
/// Server-side strings, resolved in the caller's language.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <see cref="ResourceManager"/> rather than <c>IStringLocalizer</c>: the set of
/// server strings is deliberately small, and the pseudo-locale needs a hook that
/// <c>IStringLocalizer</c> does not give cleanly.
/// </para>
/// <para>
/// Logs stay in English, always — ops greppability and a pasteable GitHub issue beat localized log
/// lines. Only text that reaches a person goes through here.
/// </para>
/// </remarks>
public static class LocalizedText
{
    private static readonly ResourceManager Manager =
        new("Compendio.Resources.Strings", typeof(LocalizedText).Assembly);

    public static string Get(string key, string language, params object[] arguments)
    {
        var culture = ToCulture(language);
        var template = Manager.GetString(key, culture) ?? Manager.GetString(key, CultureInfo.InvariantCulture) ?? key;

        var text = arguments.Length == 0
            ? template
            : SafeFormat(template, arguments);

        return language == SupportedLanguages.Pseudo ? Pseudo(text) : text;
    }

    /// <summary>
    /// Formatting must never throw: a resource string whose placeholders drift from its call site
    /// would turn a helpful error into a 500.
    /// </summary>
    private static string SafeFormat(string template, object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static CultureInfo ToCulture(string language)
    {
        if (language == SupportedLanguages.Pseudo)
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Lengthens and brackets a string by roughly the 35 % Spanish text expansion, so a control that
    /// will overflow in Spanish overflows visibly during development instead of after release.
    /// </summary>
    private static string Pseudo(string text)
    {
        var padding = new string('·', Math.Max(2, text.Length / 3));
        return $"⟦{text}{padding}⟧";
    }
}
