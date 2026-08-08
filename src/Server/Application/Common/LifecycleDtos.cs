using Compendio.Domain.Entities;

namespace Compendio.Application.Common;

/// <summary>
/// The shapes the lifecycle, notification and acknowledgment endpoints return.
/// </summary>
/// <remarks>
/// A separate file from <c>Dtos.cs</c> rather than more rows on the end of it: these belong to one
/// feature and one screen area, and a single growing bag of every DTO in the product is how a file
/// gets to a thousand lines nobody reads.
/// </remarks>
/// <param name="OwnerUserId">
/// Null when the <c>owner</c> string matches no active account. Reported, never rewritten.
/// </param>
public sealed record PageLifecycleDto(
    string Path,
    string Title,
    string? Owner,
    Guid? OwnerUserId,
    string? OwnerDisplayName,
    int? ReviewIntervalDays,
    DateTimeOffset? NextReviewDate,
    bool RequiresAcknowledgment,
    bool IsStale);

/// <param name="Unassigned">
/// The page names an owner no active account matches, or names none at all. Surfaced rather than
/// hidden: an SOP nobody owns is the case the whole feature exists to make visible.
/// </param>
public sealed record StalePageDto(
    string Path,
    string Title,
    string? Owner,
    string? OwnerDisplayName,
    bool Unassigned,
    DateTimeOffset? NextReviewDate,
    int? DaysOverdue,
    DateTimeOffset UpdatedAt);

/// <param name="OutstandingAcknowledgments">Pages the caller must confirm they have read.</param>
public sealed record DashboardDto(
    IReadOnlyList<StalePageDto> MyStalePages,
    int MyPageCount,
    int UnreadNotificationCount,
    IReadOnlyList<NotificationDto> RecentNotifications,
    IReadOnlyList<AcknowledgmentTaskDto> OutstandingAcknowledgments);

public sealed record NotificationDto(
    Guid Id,
    NotificationKind Kind,
    string TargetPath,
    string? PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record AcknowledgmentTaskDto(string Path, string Title, DateTimeOffset SinceVersionAt, bool Overdue);

/// <param name="AcknowledgedVersionId">
/// Null when this person has not acknowledged the <em>current</em> version. An acknowledgment of an
/// earlier version does not count once a material revision has re-opened it.
/// </param>
public sealed record AcknowledgmentStatusDto(
    Guid UserId,
    string DisplayName,
    bool HasAcknowledged,
    Guid? AcknowledgedVersionId,
    DateTimeOffset? AcknowledgedAt);

public sealed record AcknowledgmentReportDto(
    string Path,
    string Title,
    Guid CurrentVersionId,
    int CurrentVersionSequence,
    int RequiredCount,
    int AcknowledgedCount,
    IReadOnlyList<AcknowledgmentStatusDto> People);
