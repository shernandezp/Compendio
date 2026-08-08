using System.Globalization;
using Common.Mediator;
using Compendio.Application.Common;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// The stale report as a CSV file.
/// </summary>
/// <remarks>
/// Runs the same query as the screen, so the file and the page cannot disagree — including the
/// permission predicate, which is the part that matters. An export that widened the result set would
/// be a leak with a filename.
/// </remarks>
public sealed record ExportStaleReportCsvQuery(string? Owner = null, string? Space = null) : IQuery<CsvFile>;

public sealed record CsvFile(string FileName, byte[] Content);

public sealed class ExportStaleReportCsvHandler(ISender sender) : IRequestHandler<ExportStaleReportCsvQuery, CsvFile>
{
    /// <summary>A guard, not a product limit: a report past this is a reindex problem, not a report.</summary>
    private const int MaxRows = 5000;

    public async Task<CsvFile> Handle(ExportStaleReportCsvQuery request, CancellationToken cancellationToken = default)
    {
        var csv = new CsvWriter("path", "title", "owner", "ownerDisplayName", "unassigned", "nextReviewDate", "daysOverdue", "updatedAt");

        var page = 1;
        var written = 0;

        while (written < MaxRows)
        {
            var batch = await sender.Send(new GetStaleReportQuery(page, 100, request.Owner, request.Space), cancellationToken);
            if (batch.Items.Count == 0)
            {
                break;
            }

            foreach (var row in batch.Items)
            {
                csv.Row(
                    row.Path,
                    row.Title,
                    row.Owner,
                    row.OwnerDisplayName,
                    row.Unassigned ? "true" : "false",
                    row.NextReviewDate?.ToString("O", CultureInfo.InvariantCulture),
                    row.DaysOverdue?.ToString(CultureInfo.InvariantCulture),
                    row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

                written++;
            }

            if (written >= batch.TotalCount)
            {
                break;
            }

            page++;
        }

        return new CsvFile("stale-pages.csv", csv.ToBytes());
    }
}
