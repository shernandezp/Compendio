using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Abstractions;

/// <summary>
/// The only way the application layer reaches persistence.
/// </summary>
/// <remarks>
/// EF Core types appear in this signature and nowhere else above <c>Infrastructure</c>. That is the
/// deliberate compromise: an ORM-shaped seam that handlers can use directly, rather than a
/// repository per entity that would double the file count for no gain at this size.
/// </remarks>
public interface ICompendioDbContext
{
    DbSet<Folder> Folders { get; }

    DbSet<Page> Pages { get; }

    DbSet<PageText> PageTexts { get; }

    DbSet<PageVersion> PageVersions { get; }

    DbSet<Attachment> Attachments { get; }

    DbSet<Group> Groups { get; }

    DbSet<GroupMember> GroupMembers { get; }

    DbSet<AclNode> AclNodes { get; }

    DbSet<AclEntry> AclEntries { get; }

    DbSet<SecureScope> SecureScopes { get; }

    DbSet<AuditEntry> AuditLog { get; }

    DbSet<IndexQueueItem> IndexQueue { get; }

    DbSet<Setting> Settings { get; }

    DbSet<Acknowledgment> Acknowledgments { get; }

    DbSet<AcknowledgmentRound> AcknowledgmentRounds { get; }

    DbSet<Notification> Notifications { get; }

    DbSet<AiUsageEntry> AiUsage { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
