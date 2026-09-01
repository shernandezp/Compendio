using Compendio.Domain.Content;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// The test that matters.
/// </summary>
/// <remarks>
/// Not sampled and not optional: no input to any content-store method may produce a file operation
/// outside the content root. Everything else in the product can be wrong in a way that is
/// embarrassing; this being wrong is a way to read <c>/etc/shadow</c>.
/// </remarks>
public sealed class PathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"compendio-paths-{Guid.CreateVersion7():N}");

    public PathPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("IT/VPN/site-to-site.md")]
    [InlineData("politica-de-teletrabajo.md")]
    [InlineData("IT/192.168.1.1.md")]
    [InlineData("IT/VPN-Site-A.md")]
    [InlineData("IT/snake_case_name.md")]
    public void AcceptsOrdinaryPaths(string candidate) =>
        PathPolicy.Validate(candidate, PathKind.Page).IsValid.ShouldBeTrue();

    [Theory]
    // Traversal, in the spellings people actually try.
    [InlineData("../secrets.md", PathRule.ParentTraversal)]
    [InlineData("IT/../../secrets.md", PathRule.ParentTraversal)]
    [InlineData("IT/..\\..\\secrets.md", PathRule.ParentTraversal)]
    // Absolute and UNC.
    [InlineData("C:/Windows/system32/config.md", PathRule.AbsolutePath)]
    [InlineData(@"\\server\share\page.md", PathRule.UncPrefix)]
    [InlineData(@"\\?\C:\page.md", PathRule.UncPrefix)]
    // NTFS alternate data stream — a way to hide content beside a file every other layer thinks it read.
    [InlineData("page.md:hidden", PathRule.AlternateDataStream)]
    // Windows device names, rejected on Linux too so content stays portable.
    [InlineData("CON.md", PathRule.ReservedName)]
    [InlineData("IT/PRN.md", PathRule.ReservedName)]
    [InlineData("COM1.md", PathRule.ReservedName)]
    // Windows silently strips a trailing dot or space, so two names become one file and a rename
    // loop follows. (A space *inside* a name is fine, and is not rejected.)
    [InlineData("report.md ", PathRule.TrailingDotOrSpace)]
    [InlineData("IT/folder./page.md", PathRule.TrailingDotOrSpace)]
    // NTFS-illegal characters, rejected everywhere.
    [InlineData("what?.md", PathRule.IllegalCharacter)]
    [InlineData("a<b>.md", PathRule.IllegalCharacter)]
    [InlineData("pipe|name.md", PathRule.IllegalCharacter)]
    public void RejectsUnsafePaths(string candidate, PathRule expected)
    {
        var result = PathPolicy.Validate(candidate, PathKind.Page);

        result.IsValid.ShouldBeFalse($"'{candidate}' should not validate");
        result.Violated.ShouldBe(expected);
    }

    [Fact]
    public void RejectsNullBytes() =>
        PathPolicy.Validate("page\0.md", PathKind.Page).Violated.ShouldBe(PathRule.NullByte);

    [Fact]
    public void EnforcesThePathBudget()
    {
        var tooLong = string.Join('/', Enumerable.Repeat("folder", 40)) + "/page.md";
        PathPolicy.Validate(tooLong, PathKind.Page).Violated.ShouldBe(PathRule.TooLong);
    }

    [Fact]
    public void RequiresTheMarkdownExtensionForPages() =>
        PathPolicy.Validate("notes.txt", PathKind.Page).Violated.ShouldBe(PathRule.WrongExtension);

    /// <summary>
    /// The property: for a large generated corpus of hostile inputs, either validation fails, or the
    /// resolved absolute path is inside the root. There is no third outcome.
    /// </summary>
    [Fact]
    public void NoInputEscapesTheContentRoot()
    {
        var fragments = new[]
        {
            "..", ".", "", "/", "\\", "IT", "a b", "página", "CON", "x:y", "%2e%2e", "....", "~",
            "\0", "a\"b", "z*", "?", "|", "\n", "COM9", "lpt1", " leading", "trailing ", "very" + new string('x', 200),
        };

        var random = new Random(20260730);
        var escapes = 0;
        var checkedCount = 0;

        for (var i = 0; i < 20_000; i++)
        {
            var depth = random.Next(1, 5);
            var candidate = string.Join('/', Enumerable.Range(0, depth).Select(_ => fragments[random.Next(fragments.Length)]));

            var validation = PathPolicy.Validate(candidate, PathKind.Any);
            if (!validation.IsValid)
            {
                continue;
            }

            checkedCount++;

            if (!PathPolicy.TryResolveAbsolute(_root, validation.Path, out var absolute))
            {
                continue;
            }

            var rootFull = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar);
            if (!absolute.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                escapes++;
            }
        }

        escapes.ShouldBe(0);
        checkedCount.ShouldBeGreaterThan(0, "the generator produced nothing valid, so the property proved nothing");
    }

    [Fact]
    public void ResolvedPathsStayInsideTheRoot()
    {
        var validation = PathPolicy.Validate("IT/VPN/notes.md", PathKind.Page);
        PathPolicy.TryResolveAbsolute(_root, validation.Path, out var absolute).ShouldBeTrue();

        absolute!.ShouldStartWith(Path.GetFullPath(_root));
    }

    /// <summary>
    /// A symlink out of the root is caught after resolution, not before — the check has to happen
    /// against where the path actually lands.
    /// </summary>
    [Fact]
    public void RejectsASymlinkPointingOutsideTheRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"compendio-outside-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(outside);

        try
        {
            var link = Path.Combine(_root, "escape");

            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to create symlinks. Skipping is honest;
                // pretending the assertion ran would not be.
                Assert.Skip("This environment does not permit creating symbolic links.");
                return;
            }

            var validation = PathPolicy.Validate("escape/page.md", PathKind.Page);
            validation.IsValid.ShouldBeTrue();

            PathPolicy.TryResolveAbsolute(_root, validation.Path, out _).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData("IT/.DS_Store", true)]
    [InlineData("IT/~$report.docx", true)]
    [InlineData("IT/page.md.tmp", true)]
    [InlineData("IT/page.md", false)]
    public void IgnoresEditorAndVersionControlNoise(string path, bool ignored) =>
        PathPolicy.IsIgnored(ContentPath.FromTrusted(path)).ShouldBe(ignored);

    /// <summary>
    /// Accents go, case stays. "Index" is <c>Index.md</c>, not <c>index.md</c>: the file name is
    /// what the person typed, made portable, and folder names are shown from the disk.
    /// </summary>
    [Theory]
    [InlineData("Política de teletrabajo", "Politica-de-teletrabajo")]
    [InlineData("Configuración de sesión", "Configuracion-de-sesion")]
    [InlineData("Años y ñoños", "Anos-y-nonos")]
    [InlineData("VPN-Site-A", "VPN-Site-A")]
    [InlineData("Index", "Index")]
    [InlineData("IT", "IT")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("snake_case_name", "snake_case_name")]
    [InlineData("   ", "untitled")]
    public void SlugifiesTitlesToAsciiFileNames(string title, string expected) =>
        Slug.Create(title).ShouldBe(expected);

    /// <summary>Anchors are the exception: a URL fragment is lower-case, and existing links rely on it.</summary>
    [Theory]
    [InlineData("Configuration", "configuration")]
    [InlineData("Paso 2: Configuración", "paso-2-configuracion")]
    public void HeadingAnchorsStayLowerCase(string heading, string expected) =>
        Slug.Anchor(heading).ShouldBe(expected);

    /// <summary>
    /// Collisions are resolved against a case-insensitive check, so <c>Index.md</c> beside an existing
    /// <c>index.md</c> becomes <c>Index-2.md</c> on every platform — not a second file on Linux that
    /// turns into a clash the day the folder is copied to a Windows share.
    /// </summary>
    [Fact]
    public void DisambiguatesAgainstCaseInsensitiveNames()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.md" };

        Slug.Disambiguate("Index.md", taken.Contains).ShouldBe("Index-2.md");
        Slug.Disambiguate("Other.md", taken.Contains).ShouldBe("Other.md");
    }
}
