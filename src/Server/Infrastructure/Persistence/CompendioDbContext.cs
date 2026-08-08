using System.Globalization;
using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Compendio.Infrastructure.Persistence;

/// <summary>
/// The database. SQLite only — no provider abstraction, no dual-provider migrations.
/// </summary>
/// <remarks>
/// Reconstructible from the content folder except for users, permissions, history, configuration
/// and the audit log. That is the whole point of principle #1, and it is what makes
/// <c>compendio reindex</c> and full reconciliation safe operations rather than gambles.
/// </remarks>
public sealed class CompendioDbContext(DbContextOptions<CompendioDbContext> options)
    : IdentityDbContext<CompendioUser, CompendioIdentityRole, Guid>(options), ICompendioDbContext
{
    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<Page> Pages => Set<Page>();

    public DbSet<PageText> PageTexts => Set<PageText>();

    public DbSet<PageVersion> PageVersions => Set<PageVersion>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<AclNode> AclNodes => Set<AclNode>();

    public DbSet<AclEntry> AclEntries => Set<AclEntry>();

    public DbSet<SecureScope> SecureScopes => Set<SecureScope>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<IndexQueueItem> IndexQueue => Set<IndexQueueItem>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<Acknowledgment> Acknowledgments => Set<Acknowledgment>();

    public DbSet<AcknowledgmentRound> AcknowledgmentRounds => Set<AcknowledgmentRound>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AiUsageEntry> AiUsage => Set<AiUsageEntry>();

    Task<int> ICompendioDbContext.SaveChangesAsync(CancellationToken cancellationToken) =>
        base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<CompendioUser>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(u => u.Role).HasConversion<int>();
            entity.Property(u => u.DisplayName).HasMaxLength(200);
            entity.Property(u => u.PreferredLanguage).HasMaxLength(16);
            entity.Property(u => u.ExternalProvider).HasMaxLength(64);
            entity.Property(u => u.ExternalSubject).HasMaxLength(256);
            entity.HasIndex(u => u.Role);
            entity.HasIndex(u => new { u.ExternalProvider, u.ExternalSubject });
        });

        builder.Entity<CompendioIdentityRole>().ToTable("IdentityRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("UserClaims");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("UserLogins");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("UserTokens");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().ToTable("UserIdentityRoles");
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<Guid>>().ToTable("RoleClaims");

        builder.Entity<Folder>(entity =>
        {
            entity.ToTable("Folders");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Path).HasMaxLength(512).IsRequired();
            entity.Property(f => f.Name).HasMaxLength(256).IsRequired();
            entity.HasIndex(f => f.Path).IsUnique();
            entity.HasIndex(f => f.ParentId);
            entity.HasOne(f => f.Parent)
                .WithMany(f => f.Children)
                .HasForeignKey(f => f.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Page>(entity =>
        {
            entity.ToTable("Pages");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Path).HasMaxLength(512).IsRequired();
            entity.Property(p => p.Slug).HasMaxLength(256).IsRequired();
            entity.Property(p => p.Title).HasMaxLength(512).IsRequired();
            entity.Property(p => p.Lang).HasMaxLength(16);
            entity.Property(p => p.TranslationKey).HasMaxLength(256);
            entity.Property(p => p.Tags).HasMaxLength(1024);
            entity.Property(p => p.Owner).HasMaxLength(200);
            entity.Property(p => p.ContentHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(p => p.Path).IsUnique();
            entity.HasIndex(p => p.FolderId);
            entity.HasIndex(p => p.TranslationKey);
            entity.HasIndex(p => p.UpdatedAt);
            // The stale report sorts on the first and filters on the second.
            entity.HasIndex(p => p.NextReviewDate);
            entity.HasIndex(p => p.Owner);
            entity.HasOne(p => p.Folder)
                .WithMany(f => f.Pages)
                .HasForeignKey(p => p.FolderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PageText>(entity =>
        {
            entity.ToTable("PageText");
            // Keyed on the integer rowid, because that is what the FTS5 external-content table
            // joins on. PageId is the logical key and carries the unique index.
            entity.HasKey(t => t.RowId);
            entity.Property(t => t.RowId).ValueGeneratedOnAdd();
            entity.HasIndex(t => t.PageId).IsUnique();
            entity.HasOne(t => t.Page)
                .WithOne(p => p.Text)
                .HasForeignKey<PageText>(t => t.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Attachment>(entity =>
        {
            entity.ToTable("Attachments");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Path).HasMaxLength(512).IsRequired();
            entity.Property(a => a.ContentType).HasMaxLength(200).IsRequired();
            entity.HasIndex(a => a.Path).IsUnique();
            entity.HasIndex(a => a.PageId);
            entity.HasOne(a => a.Page)
                .WithMany(p => p.Attachments)
                .HasForeignKey(a => a.PageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Group>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(g => g.Name).IsUnique();
        });

        builder.Entity<GroupMember>(entity =>
        {
            entity.ToTable("GroupMembers");
            entity.HasKey(m => m.Id);
            entity.HasIndex(m => new { m.GroupId, m.UserId }).IsUnique();
            entity.HasIndex(m => m.UserId);
            entity.HasOne(m => m.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AclNode>(entity =>
        {
            entity.ToTable("AclNodes");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.FolderPath).HasMaxLength(512).IsRequired();
            entity.HasIndex(n => n.FolderPath).IsUnique();
            entity.HasIndex(n => n.TombstonedAt);
        });

        builder.Entity<AclEntry>(entity =>
        {
            entity.ToTable("AclEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SubjectType).HasConversion<int>();
            entity.Property(e => e.Level).HasConversion<int>();
            entity.HasIndex(e => e.AclNodeId);
            entity.HasOne(e => e.AclNode)
                .WithMany(n => n.Entries)
                .HasForeignKey(e => e.AclNodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SecureScope>(entity =>
        {
            entity.ToTable("SecureScopes");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.FolderPath).HasMaxLength(512).IsRequired();
            entity.HasIndex(s => s.FolderPath);
            entity.HasIndex(s => s.KeyId);
        });

        builder.Entity<PageVersion>(entity =>
        {
            entity.ToTable("PageVersions");
            entity.HasKey(v => v.Id);
            entity.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(v => v.Path).HasMaxLength(512).IsRequired();
            entity.Property(v => v.Note).HasMaxLength(1000);
            entity.Property(v => v.Source).HasConversion<int>();
            entity.HasIndex(v => new { v.PageId, v.Sequence }).IsUnique();
            entity.HasIndex(v => v.TombstonedAt);
            // No foreign key to Pages. History outlives its page: a deleted page's versions are
            // tombstoned for the retention window, and a constraint here would force the choice
            // between refusing the delete and destroying the history.
        });

        builder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("AuditLog");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Action).HasMaxLength(100).IsRequired();
            entity.Property(a => a.TargetType).HasMaxLength(64).IsRequired();
            entity.Property(a => a.TargetPath).HasMaxLength(512).IsRequired();
            entity.HasIndex(a => a.At);
            entity.HasIndex(a => a.ActorUserId);
        });

        builder.Entity<AiUsageEntry>(entity =>
        {
            entity.ToTable("AiUsage");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Feature).HasMaxLength(32).IsRequired();
            // Both counting queries are a range over At, one of them narrowed by user. The composite
            // covers the per-person count and the bare one covers the instance total and the prune.
            entity.HasIndex(u => new { u.UserId, u.At });
            entity.HasIndex(u => u.At);
        });

        builder.Entity<IndexQueueItem>(entity =>
        {
            entity.ToTable("IndexQueue");
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Path).HasMaxLength(512).IsRequired();
            entity.Property(q => q.FromPath).HasMaxLength(512);
            entity.Property(q => q.Operation).HasConversion<int>();
            entity.Property(q => q.LastError).HasMaxLength(2000);
            entity.HasIndex(q => q.EnqueuedAt);
            entity.HasIndex(q => q.Path);
            entity.HasIndex(q => q.PageId);
        });

        builder.Entity<Setting>(entity =>
        {
            entity.ToTable("Settings");
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(128);
            entity.Property(s => s.Value).HasMaxLength(8000).IsRequired();
        });

        builder.Entity<Acknowledgment>(entity =>
        {
            entity.ToTable("Acknowledgments");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Path).HasMaxLength(512).IsRequired();
            // One row per person per version. Acknowledging twice is idempotent, not a second row.
            entity.HasIndex(a => new { a.PageId, a.UserId, a.PageVersionId }).IsUnique();
            entity.HasIndex(a => a.UserId);
            entity.HasIndex(a => a.PageId);
            // No foreign keys, following PageVersions: an acknowledgment outlives its page, because
            // "who signed off on the policy we deleted" is the question it exists to answer.
        });

        builder.Entity<AcknowledgmentRound>(entity =>
        {
            entity.ToTable("AcknowledgmentRounds");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Reason).HasConversion<int>();
            entity.Property(r => r.Path).HasMaxLength(512).IsRequired();
            // The report reads the newest round per page, which is what this index serves.
            entity.HasIndex(r => new { r.PageId, r.OpenedAt });
            // No foreign keys, for the same reason as Acknowledgments: compliance data outlives the
            // page, and the page table is rebuildable from the content folder while this is not.
        });

        builder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Kind).HasConversion<int>();
            entity.Property(n => n.TargetPath).HasMaxLength(512).IsRequired();
            entity.Property(n => n.PayloadJson).HasMaxLength(4000);
            entity.HasIndex(n => new { n.UserId, n.CreatedAt });

            // The deduplication rule, in the schema rather than in a handler: at most one *unread*
            // row per (user, kind, target). A page stale for three months is one notification, and
            // the same condition recurring after it has been read produces a fresh one.
            entity.HasIndex(n => new { n.UserId, n.Kind, n.TargetPath })
                .IsUnique()
                .HasFilter("\"ReadAt\" IS NULL");
        });

        ApplyTimestampConversion(builder);
    }

    /// <summary>
    /// Stores every timestamp as sortable ISO-8601 UTC text.
    /// </summary>
    /// <remarks>
    /// The SQLite provider refuses to <c>ORDER BY</c> or aggregate a <see cref="DateTimeOffset"/>,
    /// so any query that sorts by date fails at runtime without this — and it fails only once there
    /// is enough data to notice. Round-tripping through a fixed-width UTC string makes ordinary text
    /// comparison correct.
    /// </remarks>
    private static void ApplyTimestampConversion(ModelBuilder builder)
    {
        var converter = new ValueConverter<DateTimeOffset, string>(
            value => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            value => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        var nullableConverter = new ValueConverter<DateTimeOffset?, string?>(
            value => value == null
                ? null
                : value.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            value => value == null
                ? null
                : DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
                    property.SetMaxLength(28);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                    property.SetMaxLength(28);
                }
            }
        }
    }
}
