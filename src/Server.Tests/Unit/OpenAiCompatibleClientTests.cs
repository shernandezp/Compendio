using System.Net;
using System.Text;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// Every failure the provider can hand back, mapped to a code a user can act on.
/// </summary>
/// <remarks>
/// <para>
/// The transport is faked rather than the client, because the mapping <em>is</em> the thing under
/// test: "the endpoint refused", "the endpoint was slow" and "the endpoint answered with rubbish"
/// send an administrator to three different places, and collapsing them into one 500 sends them
/// nowhere.
/// </para>
/// <para>
/// The other assertion running through all of these is that no message carries the API key. The
/// detail of a <c>ProblemDetails</c> reaches a browser, and a provider's error body can echo the
/// request that caused it.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleClientTests
{
    private const string ApiKey = "sk-do-not-leak-me";

    [Fact]
    public async Task AGoodAnswerComesBackAsACompletion()
    {
        var client = Build(_ => Json(HttpStatusCode.OK,
            """{"model":"llama3.1","choices":[{"message":{"role":"assistant","content":"Hola"}}]}"""));

        var completion = await client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken);

        completion.Text.ShouldBe("Hola");
        completion.Model.ShouldBe("llama3.1");
    }

    [Fact]
    public async Task AServerErrorBecomesAProviderErrorCarryingTheStatusAndNotTheKey()
    {
        var client = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            // A body that echoes the request, which is what several gateways actually do.
            Content = new StringContent($$"""{"error":"upstream failed","sent_key":"{{ApiKey}}"}""", Encoding.UTF8, "application/json"),
        });

        var failure = await Should.ThrowAsync<CompendioException>(
            () => client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(ProblemCodes.AiProviderError);
        failure.StatusCode.ShouldBe(502);
        failure.Arguments.ShouldContain("500");

        string.Join(' ', failure.Arguments).ShouldNotContain(ApiKey);
        failure.Message.ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task AMalformedBodyBecomesAProviderError()
    {
        var client = Build(_ => Json(HttpStatusCode.OK, "{ this is not json"));

        var failure = await Should.ThrowAsync<CompendioException>(
            () => client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(ProblemCodes.AiProviderError);
        failure.Arguments.ShouldContain("malformed");
    }

    /// <summary>A 200 with no usable content is its own failure: the fix is a different model.</summary>
    [Fact]
    public async Task AnEmptyChoiceListBecomesAProviderError()
    {
        var client = Build(_ => Json(HttpStatusCode.OK, """{"model":"m","choices":[]}"""));

        var failure = await Should.ThrowAsync<CompendioException>(
            () => client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(ProblemCodes.AiProviderError);
        failure.Arguments.ShouldContain("empty");
    }

    /// <summary>
    /// A slow provider is a timeout, not an error.
    /// </summary>
    /// <remarks>
    /// Distinguished because the remedy differs: a local model on a busy machine is simply slow and
    /// the answer is to wait or send less, while a 502 means something is misconfigured.
    /// </remarks>
    [Fact]
    public async Task ASlowProviderBecomesATimeout()
    {
        var client = Build(
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return Json(HttpStatusCode.OK, "{}");
            },
            timeoutSeconds: 1);

        var failure = await Should.ThrowAsync<CompendioException>(
            () => client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(ProblemCodes.AiTimeout);
        failure.StatusCode.ShouldBe(504);
    }

    /// <summary>With nothing configured the client refuses before it opens a socket.</summary>
    [Fact]
    public async Task WithNoConfigurationTheClientRefusesWithoutCallingAnything()
    {
        var called = false;

        var client = Build(
            _ =>
            {
                called = true;
                return Json(HttpStatusCode.OK, "{}");
            },
            configuration: AiConfiguration.Disabled);

        var failure = await Should.ThrowAsync<CompendioException>(
            () => client.CompleteAsync(new AiPrompt("system", "user"), TestContext.Current.CancellationToken));

        failure.Code.ShouldBe(ProblemCodes.AiDisabled);
        called.ShouldBeFalse();
    }

    /// <summary>The probe reports the transport error verbatim rather than throwing.</summary>
    [Fact]
    public async Task TheProbeReportsAFailureInsteadOfThrowing()
    {
        var client = Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var probe = await client.ProbeAsync(TestContext.Current.CancellationToken);

        probe.Ok.ShouldBeFalse();
        probe.Detail.ShouldContain("401");
        probe.Detail.ShouldNotContain(ApiKey);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenAiCompatibleClient Build(
        Func<CancellationToken, HttpResponseMessage> respond,
        int timeoutSeconds = 30,
        AiConfiguration? configuration = null) =>
        Build(token => Task.FromResult(respond(token)), timeoutSeconds, configuration);

    private static OpenAiCompatibleClient Build(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        int timeoutSeconds = 30,
        AiConfiguration? configuration = null)
    {
        var options = Options.Create(new CompendioOptions { Ai = new AiOptions { TimeoutSeconds = timeoutSeconds } });

        return new OpenAiCompatibleClient(
            new SingleHandlerFactory(new StubHandler(respond)),
            new StubSettings(configuration ?? new AiConfiguration(
                true, "http://localhost:11434/v1", "llama3.1", "localhost", [], new HashSet<string>(), 0, 0)),
            options,
            NullLogger<OpenAiCompatibleClient>.Instance);
    }

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubSettings(AiConfiguration configuration) : IAiSettings
    {
        public Task<AiConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(ApiKey);

        public Task SaveAsync(
            string? baseUrl,
            string? model,
            string? apiKey,
            IReadOnlyList<string>? allowedSpaces,
            IReadOnlyList<string>? disabledFeatures,
            int? dailyPerUser,
            int? dailyPerInstance,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Invalidate()
        {
        }
    }
}
