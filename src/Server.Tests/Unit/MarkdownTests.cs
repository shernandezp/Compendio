using System.Text.RegularExpressions;
using Compendio.Domain.Content;
using Compendio.Infrastructure.Content;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// Front matter, text extraction and rendering.
/// </summary>
/// <remarks>
/// The idempotence and semantic-preservation batteries (criteria 3–5) belong to the client, because
/// remark is the only Markdown serializer in the product. What is testable here is the half the
/// server owns: reading metadata without losing it, extracting text, and rendering safely.
/// </remarks>
public sealed class MarkdownTests
{
    [Fact]
    public void ParsesFrontMatter()
    {
        var document = MarkdownParser.Parse("""
            ---
            title: Política de teletrabajo
            lang: es
            translationKey: hr-remote-work-policy
            tags: [rrhh, política]
            owner: ana
            ---

            # Política

            Contenido.
            """.ReplaceLineEndings("\n"));

        document.FrontMatter.Title.ShouldBe("Política de teletrabajo");
        document.FrontMatter.Lang.ShouldBe("es");
        document.FrontMatter.TranslationKey.ShouldBe("hr-remote-work-policy");
        document.FrontMatter.Tags.ShouldBe(["rrhh", "política"]);
        document.FrontMatter.Owner.ShouldBe("ana");
        document.Body.ShouldStartWith("\n# Política");
    }

    /// <summary>
    /// Unknown keys survive a round trip. Users and other tools put things in front matter, and
    /// eating them would break the no-lock-in promise.
    /// </summary>
    [Fact]
    public void PreservesUnknownFrontMatterKeys()
    {
        var document = MarkdownParser.Parse("""
            ---
            title: Runbook
            confluenceId: 12345
            customField: keep me
            ---

            Body.
            """.ReplaceLineEndings("\n"));

        document.FrontMatter.Extra.Select(e => e.Key).ShouldBe(["confluenceId", "customField"], ignoreOrder: true);

        var emitted = MarkdownParser.Emit(document.FrontMatter, "\n");

        emitted.ShouldContain("confluenceId");
        emitted.ShouldContain("keep me");
    }

    [Fact]
    public void EmitsKnownKeysInAStableOrder()
    {
        var frontMatter = new FrontMatter
        {
            Owner = "ana",
            Title = "Runbook",
            Lang = "en",
            Tags = ["it", "network"],
        };

        var emitted = MarkdownParser.Emit(frontMatter, "\n");

        // Stable order is what keeps a metadata-only change from producing a whole-block diff.
        emitted.IndexOf("title", StringComparison.Ordinal)
            .ShouldBeLessThan(emitted.IndexOf("lang", StringComparison.Ordinal));
        emitted.IndexOf("lang", StringComparison.Ordinal)
            .ShouldBeLessThan(emitted.IndexOf("owner", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no front matter at all")]
    [InlineData("---\nthis: is: not: valid: yaml:\n---\nbody")]
    [InlineData("---\nunterminated\n\nbody")]
    public void SurvivesFilesWithoutUsableFrontMatter(string text)
    {
        var document = MarkdownParser.Parse(text.ReplaceLineEndings("\n"));
        document.RawText.ShouldBe(text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void FallsBackToTheFirstHeadingThenTheFileName()
    {
        var withHeading = MarkdownParser.Parse("# Configuración de sesión\n\nBody.");
        withHeading.ResolveTitle(ContentPath.FromTrusted("IT/session.md")).ShouldBe("Configuración de sesión");

        var withNothing = MarkdownParser.Parse("Just a paragraph.");
        withNothing.ResolveTitle(ContentPath.FromTrusted("IT/vpn-site-a.md")).ShouldBe("Vpn Site A");
    }

    [Fact]
    public void DetectsAndReportsLineEndings()
    {
        MarkdownParser.Parse("a\r\nb").LineEnding.ShouldBe("\r\n");
        MarkdownParser.Parse("a\nb").LineEnding.ShouldBe("\n");
    }

    [Fact]
    public void ExtractsSearchableTextWithoutSyntax()
    {
        var extractor = new TextExtractor();
        var extracted = extractor.Extract("""
            ---
            title: VPN
            tags: [red, vpn]
            ---

            # Configuración de sesión

            Conecta a **192.168.1.1** usando el enlace [manual](https://example.com/manual).

            ```bash
            ip route add 10.0.0.0/8 via 192.168.1.1
            ```

            - [ ] Revisar el certificado
            """.ReplaceLineEndings("\n"), ContentPath.FromTrusted("IT/VPN/session.md"));

        extracted.Title.ShouldBe("VPN");
        extracted.Headings.ShouldContain("Configuración de sesión");
        extracted.Tags.ShouldBe("red vpn");

        // Link text kept, target dropped; code kept as text; markers gone.
        extracted.Body.ShouldContain("192.168.1.1");
        extracted.Body.ShouldContain("manual");
        extracted.Body.ShouldNotContain("example.com");
        extracted.Body.ShouldContain("ip route add");
        extracted.Body.ShouldNotContain("**");
        extracted.Body.ShouldContain("Revisar el certificado");
    }

    [Fact]
    public void CollectsOutboundLinksForBacklinks()
    {
        var extractor = new TextExtractor();
        var extracted = extractor.Extract(
            "See [[IT/VPN/session.md]] and [[Runbook|the runbook]] and [notes](../notes.md) and [ext](https://x.com).",
            ContentPath.FromTrusted("IT/index.md"));

        extracted.OutboundLinks.ShouldContain("IT/VPN/session.md");
        extracted.OutboundLinks.ShouldContain("Runbook");
        extracted.OutboundLinks.ShouldNotContain("https://x.com");
    }

    /// <summary>
    /// The stored-XSS corpus. Pages contain content pasted from Word, Confluence and web pages, so
    /// a wiki that renders it as-is is a machine for injecting script into readers' sessions.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    [InlineData("<object data=\"data:text/html,<script>alert(1)</script>\"></object>")]
    [InlineData("[link](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    public void RendersHostileContentInert(string markdown)
    {
        var renderer = new MarkdownRenderer();
        var html = renderer.Render(markdown, ContentPath.FromTrusted("page.md"), _ => null).Html;

        // The assertions are about *live* markup, not about characters appearing anywhere. Raw HTML
        // in a page is escaped rather than rendered, so "&lt;script&gt;" showing up as visible text
        // is the feature working — a bare substring check would fail on output that is already
        // inert. So: look only inside real tags.
        var tags = LiveTags().Matches(html).Select(m => m.Value).ToArray();

        tags.ShouldAllBe(t => !DangerousElement().IsMatch(t), $"live dangerous element in: {html}");
        tags.ShouldAllBe(t => !EventHandlerAttribute().IsMatch(t), $"live event handler in: {html}");
        tags.ShouldAllBe(t => !ScriptUrl().IsMatch(t), $"live script URL in: {html}");
    }

    /// <summary>Real tags — an unescaped <c>&lt;</c> followed by an element name.</summary>
    private static Regex LiveTags() => new(@"<[a-zA-Z][^>]*>", RegexOptions.None);

    private static Regex DangerousElement() =>
        new(@"^<\s*(script|iframe|object|embed|form|svg|link|meta|base|style)\b", RegexOptions.IgnoreCase);

    private static Regex EventHandlerAttribute() => new(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase);

    private static Regex ScriptUrl() =>
        new(@"(href|src|action|data)\s*=\s*[""']?\s*(javascript:|data:text/html)", RegexOptions.IgnoreCase);

    [Fact]
    public void RendersMermaidAsAPreBlockForTheClient()
    {
        var renderer = new MarkdownRenderer();
        var rendered = renderer.Render("```mermaid\ngraph TD;\nA-->B;\n```", ContentPath.FromTrusted("page.md"), _ => null);

        rendered.ContainsMermaid.ShouldBeTrue();
        rendered.Html.ShouldContain("class=\"mermaid\"");

        // The diagram source stays escaped text until the client renders it with securityLevel:'strict'.
        rendered.Html.ShouldContain("graph TD");
    }

    [Fact]
    public void MarksUnresolvedWikiLinks()
    {
        var renderer = new MarkdownRenderer();
        var rendered = renderer.Render(
            "See [[Known]] and [[Missing]].",
            ContentPath.FromTrusted("page.md"),
            target => target == "Known" ? "IT/known.md" : null);

        rendered.Links.Count.ShouldBe(2);
        rendered.Links.Single(l => l.RawTarget == "Known").Target.ShouldBe("IT/known.md");
        rendered.Links.Single(l => l.RawTarget == "Missing").Target.ShouldBeNull();
    }

    /// <summary>
    /// A wiki link inside a code fence is code, not a link.
    /// </summary>
    /// <remarks>
    /// An IT wiki documents its own syntax: the page explaining how to write <c>[[Runbook]]</c>
    /// shows it inside a fence. Rewriting it there turns the example into a link and the code block
    /// into something other than what the author typed — and the same text then shows up as an
    /// outbound link in the backlinks panel of a page that never linked anywhere.
    /// </remarks>
    [Fact]
    public void WikiLinksInsideACodeFenceAreLeftAlone()
    {
        const string markdown = """
            Link to [[Runbook]] like this:

            ```markdown
            [[Runbook]]
            ```

            And [[Otra]] afterwards.
            """;

        var renderer = new MarkdownRenderer();
        var rendered = renderer.Render(markdown.ReplaceLineEndings("\n"), ContentPath.FromTrusted("page.md"), _ => null);

        // The fenced example is still the text the author wrote.
        rendered.Html.ShouldContain("[[Runbook]]");

        // Only the two prose occurrences became links.
        rendered.Links.Select(l => l.RawTarget).ShouldBe(["Runbook", "Otra"]);

        var extracted = new TextExtractor().Extract(markdown.ReplaceLineEndings("\n"), ContentPath.FromTrusted("page.md"));
        extracted.OutboundLinks.ShouldBe(["Runbook", "Otra"], ignoreOrder: true);
    }

    [Fact]
    public void CollectsHeadingAnchors()
    {
        var renderer = new MarkdownRenderer();
        var rendered = renderer.Render("# One\n\n## Two\n\n### Three", ContentPath.FromTrusted("page.md"), _ => null);

        rendered.Headings.Select(h => h.Level).ShouldBe([1, 2, 3]);
        rendered.Headings.Select(h => h.Text).ShouldBe(["One", "Two", "Three"]);
        rendered.Headings.ShouldAllBe(h => h.Anchor.Length > 0);
    }
}
