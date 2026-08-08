using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Ai;

/// <summary>
/// One <c>POST {baseUrl}/chat/completions</c>, hand-written.
/// </summary>
/// <remarks>
/// <para>
/// No SDK. One endpoint and one response shape do not justify a dependency, and the OpenAI-compatible
/// surface is stable enough across Ollama, Groq, OpenAI, Azure OpenAI, LM Studio and vLLM that the
/// portability an SDK would buy is portability we already have.
/// </para>
/// <para>
/// Every failure is translated into a stable problem code here, so a handler never has to interpret
/// an HTTP status: the provider erroring, the provider taking too long and the provider answering
/// with something unusable are three different things a user can act on differently. The API key
/// appears in no message, no log and no <c>ProblemDetails</c> detail.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleClient(
    IHttpClientFactory httpClientFactory,
    IAiSettings settings,
    IOptions<CompendioOptions> options,
    ILogger<OpenAiCompatibleClient> logger) : IAiProvider
{
    public const string HttpClientName = "compendio-ai";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiCompletion> CompleteAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetAsync(cancellationToken);
        if (!configuration.Enabled)
        {
            throw new CompendioException(ProblemCodes.AiDisabled, StatusCodes.Status404NotFound);
        }

        var request = new ChatRequest
        {
            Model = configuration.Model,
            Temperature = prompt.Temperature,
            MaxTokens = prompt.MaxOutputTokens,
            Messages =
            [
                new ChatMessage { Role = "system", Content = prompt.System },
                new ChatMessage { Role = "user", Content = prompt.User },
            ],
        };

        var response = await SendAsync(configuration, request, cancellationToken);

        var text = response.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            // A 200 with no usable content. Distinct from a transport failure and worth its own
            // message, because the fix is a different model rather than a different network.
            throw new CompendioException(ProblemCodes.AiProviderError, StatusCodes.Status502BadGateway, "empty");
        }

        return new AiCompletion(
            text.Trim(),
            response.Model ?? configuration.Model,
            response.Usage?.PromptTokens,
            response.Usage?.CompletionTokens);
    }

    public async Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await settings.GetAsync(cancellationToken);
        if (!configuration.Enabled)
        {
            return new AiProbeResult(false, "not-configured", null);
        }

        try
        {
            // The budget is deliberately generous rather than just large enough for "OK": a reasoning
            // model (gpt-oss, DeepSeek-R1, QwQ and the like) spends output tokens on hidden reasoning
            // before it emits any content, so a tight cap is consumed entirely by reasoning and the
            // provider answers 200 with an empty message — which the probe would otherwise report as
            // "empty" against a provider that is in fact working.
            var completion = await CompleteAsync(
                new AiPrompt("Reply with the single word OK.", "Say OK.") { MaxOutputTokens = 512, Temperature = 0 },
                cancellationToken);

            return new AiProbeResult(true, completion.Text, completion.Model);
        }
        catch (CompendioException e)
        {
            // The transport error verbatim, because "connection refused" and "401" send an admin to
            // completely different places, and a generic "could not connect" sends them nowhere.
            return new AiProbeResult(false, string.Join(' ', e.Arguments.Select(a => a.ToString())), null);
        }
    }

    private async Task<ChatResponse> SendAsync(
        AiConfiguration configuration,
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.Value.Ai.TimeoutSeconds));

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{configuration.BaseUrl}/chat/completions")
        {
            Content = JsonContent.Create(request, options: Json),
        };

        var apiKey = await settings.GetApiKeyAsync(cancellationToken);
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The AI provider at {Host} did not answer within the timeout.", configuration.EndpointLabel);
            throw new CompendioException(ProblemCodes.AiTimeout, StatusCodes.Status504GatewayTimeout,
                configuration.EndpointLabel);
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "The AI provider at {Host} could not be reached.", configuration.EndpointLabel);
            throw new CompendioException(ProblemCodes.AiProviderError, StatusCodes.Status502BadGateway,
                e.HttpRequestError.ToString());
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status only. A provider's error body can echo the request, and the request
                // carries page content — which must not end up in a log or a ProblemDetails.
                logger.LogWarning(
                    "The AI provider at {Host} answered {Status}.", configuration.EndpointLabel, (int)response.StatusCode);

                throw new CompendioException(ProblemCodes.AiProviderError, StatusCodes.Status502BadGateway,
                    ((int)response.StatusCode).ToString());
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellationToken)
                       ?? throw new CompendioException(ProblemCodes.AiProviderError, StatusCodes.Status502BadGateway, "empty");
            }
            catch (JsonException)
            {
                throw new CompendioException(ProblemCodes.AiProviderError, StatusCodes.Status502BadGateway, "malformed");
            }
        }
    }

    // The subset of the OpenAI schema this product uses. Everything else the providers return is
    // ignored on purpose — parsing more would mean tracking six vendors' extensions.
    private sealed class ChatRequest
    {
        public required string Model { get; init; }

        public required IReadOnlyList<ChatMessage> Messages { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; init; }

        public bool Stream => false;
    }

    private sealed class ChatMessage
    {
        public required string Role { get; init; }

        public required string Content { get; init; }
    }

    private sealed class ChatResponse
    {
        public string? Model { get; init; }

        public List<ChatChoice>? Choices { get; init; }

        public ChatUsage? Usage { get; init; }
    }

    private sealed class ChatChoice
    {
        public ChatMessage? Message { get; init; }
    }

    private sealed class ChatUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; init; }
    }
}
