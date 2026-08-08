using System.Reflection;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Admin;

public sealed record GetStatusQuery : IQuery<StatusDto>;

public sealed class GetStatusHandler(
    ICompendioDbContext db,
    ISearchIndex index,
    IContentCrypto crypto,
    DataDirectory dataDirectory,
    StartupGuards guards,
    IPathPolicy paths) : IRequestHandler<GetStatusQuery, StatusDto>
{
    public async Task<StatusDto> Handle(GetStatusQuery request, CancellationToken cancellationToken = default)
    {
        var status = await index.StatusAsync(cancellationToken);

        var lastBackup = await db.Settings
            .Where(s => s.Key == SettingKeys.LastBackupAt)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return new StatusDto(
            Version: BuildVersion,
            InstallMode: InstallMode.Detect(),
            ContentRoot: paths.ContentRoot,
            PageCount: await db.Pages.CountAsync(cancellationToken),
            FolderCount: await db.Folders.CountAsync(cancellationToken),
            WatcherMode: guards.ShouldUsePolling() ? "poll" : "native",
            IndexState: status.State,
            IndexQueueDepth: status.QueueDepth,
            SecureAvailability: crypto.Availability.ToString(),
            DatabaseBytes: FileSize(dataDirectory.DatabaseFile),
            ContentBytes: DirectorySize(dataDirectory.Content),
            LastBackupAt: DateTimeOffset.TryParse(lastBackup, out var parsed) ? parsed : null);
    }

    internal static string BuildVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }
}

/// <summary>Where this instance is running. Reported by <c>doctor</c> and the status screen.</summary>
public static class InstallMode
{
    public static string Detect()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return "container";
        }

        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            return "windows-service";
        }

        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("INVOCATION_ID") is not null)
        {
            return "systemd";
        }

        return "console";
    }
}

public sealed record GetAuditLogQuery(int Page = 1, int PageSize = 50, string? Action = null) : IQuery<PagedResult<AuditEntryDto>>;

public sealed class GetAuditLogHandler(ICompendioDbContext db, IUserDirectory users)
    : IRequestHandler<GetAuditLogQuery, PagedResult<AuditEntryDto>>
{
    public async Task<PagedResult<AuditEntryDto>> Handle(GetAuditLogQuery request, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var page = Math.Max(request.Page, 1);

        var query = db.AuditLog.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            query = query.Where(a => a.Action == request.Action);
        }

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(a => a.At)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var actors = await users.SubjectNamesAsync(cancellationToken);

        var items = rows.Select(a => new AuditEntryDto(
            a.Id, a.At, a.ActorUserId,
            a.ActorUserId is { } id && actors.TryGetValue(id, out var name) ? name : null,
            a.Action, a.TargetType, a.TargetPath, a.BeforeJson, a.AfterJson)).ToArray();

        return new PagedResult<AuditEntryDto>(items, total, page, pageSize);
    }
}

public sealed record GetAboutQuery : IQuery<AboutDto>;

/// <summary>
/// The notice the AGPL requires of a running program (§5d), plus the version.
/// </summary>
/// <remarks>
/// Served from the API and shown in the footer, which is how the licence obligation is discharged
/// for a network-accessible program.
/// </remarks>
public sealed class GetAboutHandler(ICompendioDbContext db, IOptions<CompendioOptions> options)
    : IRequestHandler<GetAboutQuery, AboutDto>
{
    public async Task<AboutDto> Handle(GetAboutQuery request, CancellationToken cancellationToken = default)
    {
        var name = await db.Settings
            .Where(s => s.Key == SettingKeys.InstanceName)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return new AboutDto(
            CompendioConstants.ProductName,
            GetStatusHandler.BuildVersion,
            CompendioConstants.LicenseExpression,
            CompendioConstants.SourceUrl,
            $"{CompendioConstants.ProductName} is free software licensed under the GNU Affero General Public License, " +
            $"version 3 or later. The complete corresponding source code is available at {CompendioConstants.SourceUrl}.",
            name ?? options.Value.Instance.Name);
    }
}

public sealed record GetTemplatesQuery : IQuery<IReadOnlyList<TemplateDto>>;

/// <summary>
/// Page templates: a small bundled set, overridable from a <c>_templates/</c> content folder.
/// </summary>
/// <remarks>
/// Overriding by dropping a Markdown file into a folder is the files-first answer to "can we
/// customize the templates" — no settings screen, no database table, and the customization is
/// itself a file the organization can version and copy.
/// </remarks>
public sealed class GetTemplatesHandler(IContentStore store, IPathPolicy paths)
    : IRequestHandler<GetTemplatesQuery, IReadOnlyList<TemplateDto>>
{
    private static readonly TemplateDto[] Bundled =
    [
        new("blank", "template.blank", null, ""),
        new("procedure", "template.procedure", null,
            "## Purpose\n\n## Scope\n\n## Steps\n\n1. \n2. \n3. \n\n## Related pages\n\n"),
        new("runbook", "template.runbook", null,
            "## When to use this\n\n## Prerequisites\n\n- [ ] \n- [ ] \n\n## Steps\n\n1. \n\n## Rollback\n\n## Contacts\n\n"),
        new("policy", "template.policy", null,
            "## Summary\n\n## Who this applies to\n\n## The policy\n\n## Questions\n\n"),
        new("meeting", "template.meeting", null,
            "## Attendees\n\n## Decisions\n\n## Actions\n\n- [ ] \n\n"),
    ];

    public async Task<IReadOnlyList<TemplateDto>> Handle(GetTemplatesQuery request, CancellationToken cancellationToken = default)
    {
        var templates = new List<TemplateDto>(Bundled);

        var folder = paths.Require(CompendioConstants.TemplatesFolderName, PathKind.Folder);
        if (!store.FolderExists(folder))
        {
            return templates;
        }

        await foreach (var entry in store.EnumerateAsync(folder, cancellationToken))
        {
            if (entry.IsFolder || entry.Path.Extension != CompendioConstants.MarkdownExtension)
            {
                continue;
            }

            var file = await store.ReadAsync(entry.Path, cancellationToken);
            if (file is null)
            {
                continue;
            }

            var document = MarkdownParser.Parse(file.Text);
            var id = entry.Path.NameWithoutExtension;

            // A file with the same name as a bundled template replaces it.
            templates.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            templates.Add(new TemplateDto(id, document.ResolveTitle(entry.Path), document.FrontMatter.Owner, document.Body));
        }

        return templates;
    }
}
