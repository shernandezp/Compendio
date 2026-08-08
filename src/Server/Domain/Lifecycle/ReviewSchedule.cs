namespace Compendio.Domain.Lifecycle;

/// <summary>
/// When a page is next due for review, and whether it is overdue.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately small. Staleness is one boolean — <c>nextReviewDate &lt; now</c> — with no
/// "due soon" band and no severity ladder, because the banner has to be unambiguous: either this
/// page is past its review date or it is not.
/// </para>
/// <para>
/// The rule that matters most is <see cref="Next"/>: an explicit <c>nextReviewDate</c> is
/// authoritative and is never silently recomputed. A human who typed a date meant that date, and
/// quietly moving it on the next save would make the whole feature untrustworthy.
/// </para>
/// </remarks>
public static class ReviewSchedule
{
    /// <summary>An interval below this is almost certainly a typo, and a daily banner is noise.</summary>
    public const int MinimumIntervalDays = 1;

    /// <summary>Ten years. Past this the page is not on a review cycle, it is on a whim.</summary>
    public const int MaximumIntervalDays = 3650;

    /// <summary>
    /// The next review date to store, given what the page already declares.
    /// </summary>
    /// <param name="explicitDate">
    /// <c>nextReviewDate</c> from front matter. Authoritative when present: returned unchanged.
    /// </param>
    /// <param name="intervalDays">
    /// <c>reviewIntervalDays</c> from front matter. Used only to derive a date when none is set.
    /// </param>
    /// <returns>Null when the page is on no review cycle at all, which is the default.</returns>
    public static DateTimeOffset? Next(DateTimeOffset? explicitDate, int? intervalDays, DateTimeOffset savedAt)
    {
        if (explicitDate is { } date)
        {
            return date;
        }

        return intervalDays is { } days && IsValidInterval(days)
            ? savedAt.AddDays(days)
            : null;
    }

    /// <summary>
    /// The date a review confirmation moves the page to.
    /// </summary>
    /// <remarks>
    /// Measured from <em>now</em> rather than from the old due date. A page reviewed three months
    /// late is due again a full interval from the review, not from the date it was already missing.
    /// </remarks>
    public static DateTimeOffset? AfterReview(int? intervalDays, DateTimeOffset reviewedAt) =>
        intervalDays is { } days && IsValidInterval(days) ? reviewedAt.AddDays(days) : null;

    public static bool IsStale(DateTimeOffset? nextReviewDate, DateTimeOffset now) =>
        nextReviewDate is { } due && due < now;

    public static bool IsValidInterval(int days) => days is >= MinimumIntervalDays and <= MaximumIntervalDays;
}
