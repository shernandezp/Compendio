using Compendio.Domain.Content;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// Taking an image out of a page when its file is deleted.
/// </summary>
/// <remarks>
/// This edits somebody's page, so what it leaves alone matters as much as what it removes. Every
/// case below is a way a careless implementation damages a document: reformatting a file written in
/// VS Code, eating the sentence around the picture, deleting an example out of a code fence,
/// changing every line of a CRLF file, or missing the image because the URL was spelled the other
/// way and leaving a broken one behind.
/// </remarks>
public sealed class MarkdownImagesTests
{
    private const string Url = "/api/v1/attachments/Runbooks/assets/rack.png";

    [Fact]
    public void TakesTheImageAndOneOfItsBlankLines()
    {
        var page = "# Rack 3\n\nBefore.\n\n![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\n\nAfter.\n";

        MarkdownImages.RemoveReferencesTo(page, Url)
            .ShouldBe("# Rack 3\n\nBefore.\n\nAfter.\n");
    }

    [Fact]
    public void TakesAnImageThatOpensThePageWithoutLeavingABlankFirstLine()
    {
        var page = "![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\n\nAfter.\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe("After.\n");
    }

    [Fact]
    public void LeavesTheSentenceWhenTheImageWasInsideOne()
    {
        var page = "The rear panel ![1.00](/api/v1/attachments/Runbooks/assets/rack.png) has two ports.\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe("The rear panel has two ports.\n");
    }

    [Fact]
    public void RemovesTheCaptionCarryingFormToo()
    {
        var page = "![1.00](/api/v1/attachments/Runbooks/assets/rack.png \"Rack 3, rear\")\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe(string.Empty);
    }

    [Fact]
    public void LeavesEveryOtherImageAlone()
    {
        var page = "![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\n\n![1.00](/api/v1/attachments/Runbooks/assets/front.png)\n";

        MarkdownImages.RemoveReferencesTo(page, Url)
            .ShouldBe("![1.00](/api/v1/attachments/Runbooks/assets/front.png)\n");
    }

    /// <summary>
    /// The editor percent-encodes; a page written in VS Code has the characters themselves. Both
    /// point at the same file, and recognizing only one of them leaves the other as a broken image.
    /// </summary>
    [Theory]
    [InlineData("![](/api/v1/attachments/Gu%C3%ADas/assets/rack.png)\n")]
    [InlineData("![](/api/v1/attachments/Guías/assets/rack.png)\n")]
    [InlineData("![](</api/v1/attachments/Guías/assets/rack.png>)\n")]
    public void MatchesAUrlHoweverItIsSpelled(string page) =>
        MarkdownImages.RemoveReferencesTo(page, "/api/v1/attachments/Guías/assets/rack.png")
            .ShouldBe(string.Empty);

    /// <summary>A page documenting how to embed an image contains one inside a fence. That is an example.</summary>
    [Fact]
    public void DoesNotReachInsideFencedCode()
    {
        var page = "Here is how:\n\n```markdown\n![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\n```\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe(page);
    }

    [Fact]
    public void PassesFrontMatterThroughUntouched()
    {
        var page = "---\ntitle: Rack 3\nmachineTranslated: true\n---\n\n![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\n\nTexto.\n";

        MarkdownImages.RemoveReferencesTo(page, Url)
            .ShouldBe("---\ntitle: Rack 3\nmachineTranslated: true\n---\n\nTexto.\n");
    }

    /// <summary>
    /// Indentation is structure, and this must not touch it.
    /// </summary>
    /// <remarks>
    /// Collapsing the leading spaces of a nested list item moves it up a level; adding one to a
    /// three-space indent makes the line an indented code block. Both are silent, and both are a
    /// larger edit than the one that was asked for.
    /// </remarks>
    [Theory]
    [InlineData(
        "- Outer\n  - Inner ![1.00](/api/v1/attachments/Runbooks/assets/rack.png) here\n",
        "- Outer\n  - Inner here\n")]
    [InlineData(
        "   ![1.00](/api/v1/attachments/Runbooks/assets/rack.png) ![](other.png)\n",
        "   ![](other.png)\n")]
    public void LeavesIndentationExactlyAsItWas(string page, string expected) =>
        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe(expected);

    /// <summary>A file written on Windows must not come back with every line changed.</summary>
    [Fact]
    public void PreservesCrlfLineEndings()
    {
        var page = "# Rack 3\r\n\r\nBefore.\r\n\r\n![1.00](/api/v1/attachments/Runbooks/assets/rack.png)\r\n\r\nAfter.\r\n";

        MarkdownImages.RemoveReferencesTo(page, Url)
            .ShouldBe("# Rack 3\r\n\r\nBefore.\r\n\r\nAfter.\r\n");
    }

    [Fact]
    public void ReturnsThePageUnchangedWhenItNeverShowedTheImage()
    {
        var page = "# Rack 3\n\nNo pictures here.\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBeSameAs(page);
    }

    /// <summary>A link is somebody's sentence. Deleting the file is not a reason to delete their words.</summary>
    [Fact]
    public void LeavesALinkToTheSameFileInPlace()
    {
        var page = "See [the diagram](/api/v1/attachments/Runbooks/assets/rack.png) for the layout.\n";

        MarkdownImages.RemoveReferencesTo(page, Url).ShouldBe(page);
    }
}
