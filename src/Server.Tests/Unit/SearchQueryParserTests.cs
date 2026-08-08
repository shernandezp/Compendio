using Compendio.Infrastructure.Search;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// The query parser: small, predictable, and the reason user input never reaches <c>MATCH</c> raw.
/// </summary>
public sealed class SearchQueryParserTests
{
    private static readonly SearchQueryParser Parser = new(["servidor=servidores=server"]);

    [Fact]
    public void QuotesEveryTerm()
    {
        var parsed = Parser.Parse("vpn cisco");

        parsed.Match.ShouldBe("\"vpn\" AND \"cisco\"*");
    }

    /// <summary>
    /// Prefix-matching the last term is the cheap half of the no-stemming decision: it covers most
    /// of what a Spanish stemmer would have bought, and it is what a search box does anyway while
    /// somebody is still typing.
    /// </summary>
    [Fact]
    public void PrefixMatchesTheLastTerm() =>
        Parser.Parse("servid").Match.ShouldEndWith("*");

    [Fact]
    public void HandlesPhrases() =>
        Parser.Parse("\"site to site\"").Match.ShouldBe("\"site to site\"");

    [Fact]
    public void HandlesExclusion() =>
        Parser.Parse("vpn -obsolete").Match.ShouldBe("\"vpn\"* NOT \"obsolete\"");

    [Fact]
    public void DropsAnExclusionOnlyQueryRatherThanProducingInvalidSql() =>
        Parser.Parse("-obsolete").Match.ShouldBeEmpty();

    [Theory]
    [InlineData("tag:seguridad", "seguridad")]
    [InlineData("TAG:Seguridad", "seguridad")]
    public void ExtractsTagFilters(string query, string expected) =>
        Parser.Parse(query).Tag.ShouldBe(expected);

    [Theory]
    [InlineData("space:IT", "IT")]
    [InlineData("in:IT/VPN", "IT/VPN")]
    [InlineData("in:/IT/VPN/", "IT/VPN")]
    public void ExtractsPathFilters(string query, string expected) =>
        Parser.Parse(query).PathPrefix.ShouldBe(expected);

    [Fact]
    public void ExtractsOwnerAndLanguageFilters()
    {
        var parsed = Parser.Parse("owner:ana lang:es vpn");

        parsed.Owner.ShouldBe("ana");
        parsed.Lang.ShouldBe("es");
        parsed.Match.ShouldBe("\"vpn\"*");
    }

    [Fact]
    public void ExtractsDateFilters()
    {
        Parser.Parse("updated:>2026-01-01").UpdatedAfter!.Value.Year.ShouldBe(2026);
        Parser.Parse("updated:<2026-01-01").UpdatedBefore!.Value.Year.ShouldBe(2026);
    }

    /// <summary>
    /// An unknown prefix is literal text, not an error: somebody typing <c>ratio:2</c> means to
    /// search for it.
    /// </summary>
    [Fact]
    public void TreatsUnknownPrefixesAsLiteralText()
    {
        var parsed = Parser.Parse("ratio:2");

        parsed.Match.ShouldBe("\"ratio:2\"*");
        parsed.Tag.ShouldBeNull();
    }

    [Fact]
    public void ExpandsSynonyms()
    {
        var parsed = Parser.Parse("servidor caido");

        parsed.Match.ShouldContain("\"servidores\"");
        parsed.Match.ShouldContain("\"server\"");
        parsed.Match.ShouldContain(" OR ");
    }

    /// <summary>
    /// The whole point of parsing rather than passing through: nothing a user types can change the
    /// shape of the expression, so an unbalanced quote is a search, not a 500.
    /// </summary>
    [Theory]
    [InlineData("unbalanced \"quote")]
    [InlineData("\"\"\"\"")]
    [InlineData("a\" OR \"b")]
    [InlineData("* * *")]
    [InlineData("NEAR(a b)")]
    [InlineData("^caret")]
    [InlineData("a AND NOT b OR")]
    public void NeverProducesAnUnbalancedExpression(string hostile)
    {
        var match = Parser.Parse(hostile).Match;

        // Every double-quote we emit is part of a pair.
        match.Count(c => c == '"').ShouldBe(match.Count(c => c == '"') / 2 * 2);
    }

    [Fact]
    public void AnEmptyQueryIsEmptyRatherThanAMatchAll()
    {
        Parser.Parse("").IsEmpty.ShouldBeTrue();
        Parser.Parse("   ").IsEmpty.ShouldBeTrue();
        Parser.Parse(null).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AFilterOnlyQueryIsNotEmpty()
    {
        var parsed = Parser.Parse("tag:seguridad");

        parsed.IsEmpty.ShouldBeFalse();
        parsed.HasText.ShouldBeFalse();
    }
}
