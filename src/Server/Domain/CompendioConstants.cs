namespace Compendio.Domain;

/// <summary>
/// The product's own names and magic values, kept in one file so renaming the product is cheap.
/// </summary>
public static class CompendioConstants
{
    public const string ProductName = "Compendio";
    public const string CommandName = "compendio";

    /// <summary>Windows service name and systemd unit name.</summary>
    public const string ServiceName = "Compendio";

    /// <summary>Virtual service account the Windows installer uses. Never LocalSystem.</summary>
    public const string WindowsServiceAccount = @"NT SERVICE\Compendio";

    /// <summary>Cookie carrying the resolved UI language (localization.md §2).</summary>
    public const string LanguageCookieName = "compendio_lang";

    public const string AuthenticationCookieName = "compendio_auth";

    /// <summary>SPDX identifier. The AGPL §5d notice is served from GET /api/v1/about.</summary>
    public const string LicenseExpression = "AGPL-3.0-or-later";

    public const string SourceUrl = "https://github.com/shernandezp/Compendio";

    /// <summary>Folder inside the content root whose pages are offered as page templates.</summary>
    public const string TemplatesFolderName = "_templates";

    /// <summary>Folder beside a page holding its attachments.</summary>
    public const string AssetsFolderName = "assets";

    public const string MarkdownExtension = ".md";

    /// <summary>Suffix appended inside a secure scope. Visible and greppable by decision.</summary>
    public const string EncryptedExtension = ".enc";
}
