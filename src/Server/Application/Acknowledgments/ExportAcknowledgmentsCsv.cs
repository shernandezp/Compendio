using System.Globalization;
using Common.Mediator;
using Compendio.Application.Lifecycle;

namespace Compendio.Application.Acknowledgments;

/// <summary>
/// One page's acknowledgment report as a CSV file.
/// </summary>
/// <remarks>
/// Dispatches the report query rather than re-querying, so the export inherits its authorization —
/// <c>manage</c> on the folder — and its definition of who was required. The most likely way for a
/// compliance export to be wrong is for it to have been written twice.
/// </remarks>
public sealed record ExportAcknowledgmentsCsvQuery(string Path) : IQuery<CsvFile>;

public sealed class ExportAcknowledgmentsCsvHandler(ISender sender) : IRequestHandler<ExportAcknowledgmentsCsvQuery, CsvFile>
{
    public async Task<CsvFile> Handle(ExportAcknowledgmentsCsvQuery request, CancellationToken cancellationToken = default)
    {
        var report = await sender.Send(new GetAcknowledgmentReportQuery(request.Path), cancellationToken);

        var csv = new Application.Common.CsvWriter("page", "title", "version", "person", "acknowledged", "acknowledgedAt");

        foreach (var person in report.People)
        {
            csv.Row(
                report.Path,
                report.Title,
                report.CurrentVersionSequence.ToString(CultureInfo.InvariantCulture),
                person.DisplayName,
                person.HasAcknowledged ? "true" : "false",
                person.AcknowledgedAt?.ToString("O", CultureInfo.InvariantCulture));
        }

        var name = report.Path.Replace('/', '-').Replace(".md", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new CsvFile($"acknowledgments-{name}.csv", csv.ToBytes());
    }
}
