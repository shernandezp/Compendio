using System.Text.RegularExpressions;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Ai;

/// <param name="Citations">Paths, each one re-checked against the evaluator before this was sent.</param>
public sealed record AiAnswerDto(
    string Answer,
    IReadOnlyList<AiCitationDto> Citations,
    string Model,
    string EndpointLabel);

public sealed record AiCitationDto(string Path, string Title);

/// <summary>
/// Question answering over the wiki, with linked sources.
/// </summary>
/// <remarks>
/// <para>
/// The most demoed AI feature and the only one with a retrieval-leak risk, so the security story is
/// spelled out rather than assumed. Retrieval filters by the caller's readable folders in the SQL
/// before any passage is read from disk; the AI guard drops anything in a secure scope that has not
/// opted in; and every path the model cites is checked <em>again</em> here before the response is
/// sent, with the citation dropped if it fails.
/// </para>
/// <para>
/// The model has no tools and cannot fetch anything, so prompt injection inside a page can produce a
/// wrong answer and nothing else — there is no channel to exfiltrate down.
/// </para>
/// </remarks>
public sealed partial record AskWikiQuery(string Question) : IQuery<AiAnswerDto>;

public sealed partial class AskWikiHandler(
    AiGuard guard,
    IAiProvider provider,
    IAiRetrieval retrieval,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IOptions<CompendioOptions> options,
    ILogger<AskWikiHandler> logger) : IRequestHandler<AskWikiQuery, AiAnswerDto>
{
    public async Task<AiAnswerDto> Handle(AskWikiQuery request, CancellationToken cancellationToken = default)
    {
        var configuration = await guard.RequireEnabledAsync(AiFeatures.Ask, cancellationToken);

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new ValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["question"] = ["required"],
            });
        }

        // Charged before the expansion call, which is the first thing here that reaches the provider
        // — a question that then retrieves nothing has still cost money, and pretending otherwise
        // would leave an unanswerable question as a free way to spend somebody's endpoint.
        //
        // One question is one charge even though it is two model calls. The budget is a promise to
        // the person spending it ("fifty AI actions a day"), and a counter that ticks twice for one
        // button is a promise nobody can check.
        await guard.ChargeAsync(configuration, AiFeatures.Ask, request.Question.Length, cancellationToken);

        // A question is a bad BM25 query. Asking the model for the words a document answering it
        // would contain is what makes FTS retrieval usable at all.
        var queries = await ExpandAsync(request.Question, cancellationToken);

        var passages = await retrieval.FindAsync(queries, options.Value.Ai.MaxContextPassages, cancellationToken);

        if (passages.Count == 0)
        {
            // No sources, so no answer. Letting the model answer from general knowledge here is
            // exactly how a retrieval miss becomes a confident invention about somebody's VPN.
            return new AiAnswerDto(string.Empty, [], configuration.Model, configuration.EndpointLabel);
        }

        var completion = await provider.CompleteAsync(PromptTemplates.Ask(request.Question, passages), cancellationToken);

        var (answer, cited) = SplitCitations(completion.Text);
        var citations = await VerifyAsync(cited, passages, cancellationToken);

        return new AiAnswerDto(answer, citations, completion.Model, configuration.EndpointLabel);
    }

    private async Task<IReadOnlyList<string>> ExpandAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            var completion = await provider.CompleteAsync(PromptTemplates.ExpandQuery(question), cancellationToken);

            var expanded = completion.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimStart('-', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ' '))
                .Where(line => line.Length > 1)
                .Take(4)
                .ToList();

            // The original question always rides along: expansion is a heuristic, and a model that
            // rewrites "VPN" into something clever should not be able to lose the obvious query.
            expanded.Add(question);
            return expanded;
        }
        catch (CompendioException e)
        {
            logger.LogDebug("Query expansion failed ({Code}); falling back to the raw question.", e.Code);
            return [question];
        }
    }

    /// <summary>
    /// Splits the trailing <c>Sources:</c> line off the answer.
    /// </summary>
    /// <remarks>
    /// Parsed out rather than shown, because the paths become links the client renders — and because
    /// a path that fails the re-check below must disappear from the answer, which is only possible
    /// if it is not embedded in the prose.
    /// </remarks>
    private static (string Answer, IReadOnlyList<string> Cited) SplitCitations(string text)
    {
        var match = SourcesLine().Match(text);
        if (!match.Success)
        {
            return (text.Trim(), []);
        }

        var cited = match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('`', '"', '\'', '<', '>', '[', ']', '(', ')'))
            .Where(p => p.Length > 0)
            .ToArray();

        return (text[..match.Index].Trim(), cited);
    }

    /// <summary>
    /// Keeps only citations that came from the retrieved set <em>and</em> still pass the evaluator.
    /// </summary>
    /// <remarks>
    /// Two independent conditions, on purpose. The first stops a model from citing a path it
    /// invented or remembered from training. The second re-asks the permission question at render
    /// time, which is what the design note promises and what an ACL change between retrieval and
    /// response would otherwise defeat.
    /// </remarks>
    private async Task<IReadOnlyList<AiCitationDto>> VerifyAsync(
        IReadOnlyList<string> cited,
        IReadOnlyList<RetrievedPassage> passages,
        CancellationToken cancellationToken)
    {
        var offered = passages.ToDictionary(p => p.Path, p => p.Title, StringComparer.OrdinalIgnoreCase);
        var verified = new List<AiCitationDto>();

        foreach (var path in cited.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!offered.TryGetValue(path, out var title))
            {
                logger.LogDebug("Dropped a citation the model produced that was not among the sources given to it.");
                continue;
            }

            var content = ContentPath.FromTrusted(path);
            if (await permissions.EffectiveAsync(currentUser.Subject, content, cancellationToken) < Domain.Security.PermissionLevel.Read)
            {
                logger.LogWarning("Dropped a citation to '{Path}' that the asking user may no longer read.", path);
                continue;
            }

            verified.Add(new AiCitationDto(path, title));
        }

        return verified;
    }

    [GeneratedRegex(@"^\s*Sources?\s*:\s*(.+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SourcesLine();
}
