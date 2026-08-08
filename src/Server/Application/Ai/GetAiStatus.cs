using Common.Mediator;
using Compendio.Application.Abstractions;

namespace Compendio.Application.Ai;

/// <param name="Enabled">False when no provider is configured. The client renders nothing when false.</param>
/// <param name="EndpointLabel">
/// The host content would be sent to. Shown next to every AI action, not buried in a settings page —
/// for this audience, "where does my HR policy go" is what decides whether the feature gets used.
/// </param>
/// <param name="Budget">
/// The caller's own remaining allowance. Sent with the status rather than fetched separately so the
/// menu can say "3 left today" on the same round trip that decides whether the menu exists at all.
/// </param>
public sealed record AiStatusDto(
    bool Enabled,
    IReadOnlyList<string> Features,
    string EndpointLabel,
    string Model,
    AiBudgetState Budget);

/// <summary>
/// Whether AI exists, for the client.
/// </summary>
/// <remarks>
/// <para>
/// The one AI endpoint that always answers. Every action returns <c>404 ai.disabled</c> when nothing
/// is configured; this returns <c>{ enabled: false }</c> so the UI knows to render no control at
/// all, rather than rendering a button that fails when pressed.
/// </para>
/// <para>
/// Not-mapping the action routes was the alternative and it is worse: mapping happens at startup and
/// configuration happens at runtime, so it would mean restarting the service after pasting a base
/// URL into a form.
/// </para>
/// </remarks>
public sealed record GetAiStatusQuery : IQuery<AiStatusDto>;

public sealed class GetAiStatusHandler(IAiSettings settings, AiBudget budget)
    : IRequestHandler<GetAiStatusQuery, AiStatusDto>
{
    public async Task<AiStatusDto> Handle(GetAiStatusQuery request, CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetAsync(cancellationToken);

        if (!configuration.Enabled)
        {
            return new AiStatusDto(false, [], string.Empty, string.Empty, AiBudget.Unlimited);
        }

        var features = AiFeatures.All.Where(f => !configuration.DisabledFeatures.Contains(f)).ToArray();

        if (features.Length == 0)
        {
            // Every feature switched off individually is the same outcome as no provider at all, and
            // the client should render the same nothing.
            return new AiStatusDto(false, [], string.Empty, string.Empty, AiBudget.Unlimited);
        }

        return new AiStatusDto(
            true,
            features,
            configuration.EndpointLabel,
            configuration.Model,
            await budget.ForCurrentUserAsync(configuration, cancellationToken));
    }
}
