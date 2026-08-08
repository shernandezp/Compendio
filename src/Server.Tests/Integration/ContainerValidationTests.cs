using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Every registered service can actually be constructed, with scope validation on.
/// </summary>
/// <remarks>
/// <para>
/// The bug this exists for is silent in every configuration the test suite runs in and fatal in the
/// one developers use. <c>ValidateScopes</c> and <c>ValidateOnBuild</c> default to on in Development
/// and off elsewhere, so a singleton that captures a scoped service starts fine in Production, fine
/// under <c>WebApplicationFactory</c> — and throws on <c>dotnet run</c>.
/// </para>
/// <para>
/// v0 shipped one of these (<c>IndexerService</c> injecting a scoped <c>ISearchIndex</c>) and it was
/// caught by <c>dotnet ef</c> rather than by a test. This is that check, on purpose rather than by
/// luck.
/// </para>
/// </remarks>
public sealed class ContainerValidationTests
{
    [Fact]
    public void EveryServiceResolvesWithScopeValidationOn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"compendio-di-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);

        try
        {
            var options = new CompendioOptions { DataDir = root };
            var dataDirectory = DataDirectory.Resolve(options);

            var services = new ServiceCollection();
            services.AddCompendioForCli(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                dataDirectory, options);

            // ValidateScopes only. ValidateOnBuild would additionally try to construct every
            // framework descriptor, and the authorization services want an EndpointDataSource that
            // exists only inside a web host — a failure about ASP.NET Core's own wiring, not ours.
            //
            // Scope validation is the half that matters here: it throws at *resolve* time when a
            // singleton consumes a scoped service, naming both.
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false,
            });

            using var scope = provider.CreateScope();

            // Spot-check the seams v1 added, so a missing registration fails here rather than as a
            // 500 on the first request that needs one.
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.IAiSettings>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.IAiProvider>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.IAiRetrieval>().ShouldNotBeNull();

            // The budget is resolved from two places with very different shapes: a request scope,
            // and MaintenanceService's background scope, where there is no HttpContext at all. This
            // registration is CLI-shaped — no HTTP anywhere — so it is the harsher of the two, and
            // it is the one that would break the six-hourly prune rather than a user-facing request.
            scope.ServiceProvider.GetRequiredService<Application.Ai.AiBudget>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Ai.AiGuard>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Ai.AiTextActions>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.IGitMirror>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.INotificationWriter>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Engine.ReviewScan>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Engine.AcknowledgmentScan>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Engine.ChangeNotifier>().ShouldNotBeNull();
            scope.ServiceProvider.GetRequiredService<Application.Acknowledgments.AcknowledgmentRounds>().ShouldNotBeNull();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test run over.
            }
        }
    }
}
