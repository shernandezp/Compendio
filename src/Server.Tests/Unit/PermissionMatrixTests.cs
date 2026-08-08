using Compendio.Domain.Content;
using Compendio.Domain.Security;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// The permission model, driven from a literal table.
/// </summary>
/// <remarks>
/// Runs against <see cref="PermissionRules"/> directly rather than through the caching evaluator,
/// because these are the rules and the evaluator is a memoizing wrapper. A behaviour asserted here
/// and broken there is a caching bug; a behaviour that is wrong here is a model bug, and the two
/// are worth being able to tell apart.
/// </remarks>
public sealed class PermissionMatrixTests
{
    private static readonly Guid Ana = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bruno = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid InfraTeam = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private static PermissionSubject Reader(params Guid[] groups) => new(Ana, UserRole.Reader, groups.ToHashSet());

    private static PermissionSubject Editor(params Guid[] groups) => new(Ana, UserRole.Editor, groups.ToHashSet());

    private static PermissionSubject Admin() => new(Ana, UserRole.Admin, new HashSet<Guid>());

    private static PermissionSubject Other() => new(Bruno, UserRole.Editor, new HashSet<Guid>());

    private static Dictionary<string, AclNodeSnapshot> Acl(params AclNodeSnapshot[] nodes) =>
        nodes.ToDictionary(n => n.Path.Value, n => n, StringComparer.Ordinal);

    private static AclNodeSnapshot Node(string path, bool inherit, params AclEntrySnapshot[] entries) =>
        new(ContentPath.FromTrusted(path), inherit, entries);

    private static PermissionLevel Evaluate(
        PermissionSubject subject,
        string path,
        Dictionary<string, AclNodeSnapshot> acl,
        PermissionLevel instanceDefault = PermissionLevel.Read,
        bool secure = false) =>
        PermissionRules.Effective(subject, ContentPath.FromTrusted(path), acl, instanceDefault, secure);

    [Fact]
    public void AFreshInstanceIsReadableByEveryAuthenticatedUser() =>
        Evaluate(Reader(), "IT/VPN", Acl()).ShouldBe(PermissionLevel.Read);

    [Fact]
    public void ALockedDownInstanceGrantsNothingByDefault() =>
        Evaluate(Editor(), "IT/VPN", Acl(), PermissionLevel.None).ShouldBe(PermissionLevel.None);

    [Fact]
    public void InheritingFoldersCanOnlyAddAccess()
    {
        var acl = Acl(Node("IT", inherit: true,
            new AclEntrySnapshot(AclSubjectType.Group, InfraTeam, PermissionLevel.Write)));

        // The group gets more…
        Evaluate(Editor(InfraTeam), "IT/VPN", acl).ShouldBe(PermissionLevel.Write);

        // …and everybody else keeps what the instance default gave them.
        Evaluate(Other(), "IT/VPN", acl).ShouldBe(PermissionLevel.Read);
    }

    [Fact]
    public void RestrictedFoldersAreExactlyTheirOwnEntryList()
    {
        var acl = Acl(Node("HR", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Write)));

        Evaluate(Editor(), "HR/Policies", acl).ShouldBe(PermissionLevel.Write);

        // Cutting inheritance is the only way to take access away, and it takes it away completely.
        Evaluate(Other(), "HR/Policies", acl).ShouldBe(PermissionLevel.None);
    }

    [Fact]
    public void RestrictionAppliesToTheWholeSubtree()
    {
        var acl = Acl(Node("HR", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Read)));

        Evaluate(Other(), "HR/Policies/Disciplinary/2026", acl).ShouldBe(PermissionLevel.None);
    }

    [Fact]
    public void ADeeperGrantCanReopenPartOfARestrictedSubtree()
    {
        var acl = Acl(
            Node("HR", inherit: false, new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Read)),
            Node("HR/Public", inherit: true, new AclEntrySnapshot(AclSubjectType.Everyone, null, PermissionLevel.Read)));

        Evaluate(Other(), "HR/Policies", acl).ShouldBe(PermissionLevel.None);
        Evaluate(Other(), "HR/Public", acl).ShouldBe(PermissionLevel.Read);
    }

    /// <summary>Criterion 8, first half.</summary>
    [Fact]
    public void AReaderGrantedManageCanStillOnlyRead()
    {
        var acl = Acl(Node("IT", inherit: true,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Manage)));

        Evaluate(Reader(), "IT", acl).ShouldBe(PermissionLevel.Read);
    }

    [Fact]
    public void AnEditorGrantedManageIsCappedAtWrite()
    {
        var acl = Acl(Node("IT", inherit: true,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Manage)));

        Evaluate(Editor(), "IT", acl).ShouldBe(PermissionLevel.Write);
    }

    [Fact]
    public void AnAdministratorReachesEverythingIncludingRestrictedFolders()
    {
        var acl = Acl(Node("HR", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Bruno, PermissionLevel.Read)));

        Evaluate(Admin(), "HR/Policies", acl).ShouldBe(PermissionLevel.Manage);
    }

    [Fact]
    public void GroupAndUserGrantsCombineToTheHighest()
    {
        var acl = Acl(Node("IT", inherit: true,
            new AclEntrySnapshot(AclSubjectType.Group, InfraTeam, PermissionLevel.Read),
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Write)));

        Evaluate(Editor(InfraTeam), "IT", acl).ShouldBe(PermissionLevel.Write);
    }

    [Fact]
    public void EveryoneMatchesEveryAuthenticatedUser()
    {
        var acl = Acl(Node("Wiki", inherit: false,
            new AclEntrySnapshot(AclSubjectType.Everyone, null, PermissionLevel.Write)));

        Evaluate(Other(), "Wiki/anything", acl).ShouldBe(PermissionLevel.Write);
    }

    /// <summary>
    /// The "only administrators can edit" rule, enforced in the model rather than in the UI.
    /// </summary>
    [Fact]
    public void InsideASecureScopeNonAdminsAreCappedAtRead()
    {
        var acl = Acl(Node("Secrets", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Manage)));

        Evaluate(Editor(), "Secrets", acl, secure: true).ShouldBe(PermissionLevel.Read);
        Evaluate(Admin(), "Secrets", acl, secure: true).ShouldBe(PermissionLevel.Manage);
    }

    [Fact]
    public void ANonAdminWithNoEntryInASecureScopeGetsNothing()
    {
        var acl = Acl(Node("Secrets", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Read)));

        Evaluate(Other(), "Secrets/passwords.md", acl, secure: true).ShouldBe(PermissionLevel.None);
    }

    [Fact]
    public void DeepNestingKeepsTheNearestRestrictionAuthoritative()
    {
        var acl = Acl(
            Node("A", inherit: true, new AclEntrySnapshot(AclSubjectType.Everyone, null, PermissionLevel.Write)),
            Node("A/B/C", inherit: false, new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Read)),
            Node("A/B/C/D", inherit: true, new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Write)));

        Evaluate(Editor(), "A/B", acl).ShouldBe(PermissionLevel.Write);
        Evaluate(Editor(), "A/B/C", acl).ShouldBe(PermissionLevel.Read);
        Evaluate(Editor(), "A/B/C/D", acl).ShouldBe(PermissionLevel.Write);
        Evaluate(Other(), "A/B/C/D", acl).ShouldBe(PermissionLevel.None);
    }

    [Fact]
    public void ReadableFoldersMatchesWhatEffectiveWouldSayForEachFolder()
    {
        var folders = new[] { "", "IT", "IT/VPN", "HR", "HR/Policies" }
            .Select(ContentPath.FromTrusted)
            .ToArray();

        var acl = Acl(Node("HR", inherit: false,
            new AclEntrySnapshot(AclSubjectType.User, Ana, PermissionLevel.Read)));

        var readable = PermissionRules.ReadableFolders(Other(), folders, acl, PermissionLevel.Read, []);

        readable.Select(f => f.Value).ShouldBe(["", "IT", "IT/VPN"], ignoreOrder: true);
    }

    [Fact]
    public void SecureScopeDetectionCoversTheWholeSubtree()
    {
        var scopes = new[] { ContentPath.FromTrusted("IT/Secrets") };

        PermissionRules.IsInsideSecureScope(ContentPath.FromTrusted("IT/Secrets"), scopes).ShouldBeTrue();
        PermissionRules.IsInsideSecureScope(ContentPath.FromTrusted("IT/Secrets/deep/page.md"), scopes).ShouldBeTrue();
        PermissionRules.IsInsideSecureScope(ContentPath.FromTrusted("IT/SecretsOther"), scopes).ShouldBeFalse();
        PermissionRules.IsInsideSecureScope(ContentPath.FromTrusted("IT"), scopes).ShouldBeFalse();
    }
}
