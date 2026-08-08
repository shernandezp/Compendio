namespace Compendio.Application.Abstractions;

/// <param name="System">Instructions the model is given before the user's text.</param>
/// <param name="User">The user turn. Retrieved passages, when there are any, are already in here.</param>
public sealed record AiPrompt(string System, string User)
{
    /// <summary>Bounded to keep one long page from becoming a bill or a timeout.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Low for rewriting and answering; the default is the provider's.</summary>
    public double? Temperature { get; init; }
}

/// <param name="Text">The model's reply, expected to be Markdown.</param>
public sealed record AiCompletion(string Text, string Model, int? PromptTokens, int? CompletionTokens);

/// <summary>
/// One OpenAI-compatible endpoint.
/// </summary>
/// <remarks>
/// <para>
/// One integration covers Ollama, Groq, OpenAI, Azure OpenAI, LM Studio and vLLM, which is the whole
/// reason the product's AI configuration is a base URL, a key and a model name rather than a
/// provider list that has to grow.
/// </para>
/// <para>
/// There is no tool calling and no outbound fetch on this interface, and that is a security
/// property, not an omission: prompt injection in a page can produce a wrong answer and nothing
/// else, because there is no channel to exfiltrate down.
/// </para>
/// </remarks>
public interface IAiProvider
{
    /// <summary>
    /// Sends one prompt and waits for the whole reply.
    /// </summary>
    /// <remarks>
    /// Non-streaming by decision. Streaming is real value on a slow local model, and it is also an
    /// SSE path, a second response shape and a second failure mode; it is backlog, and the timeout
    /// plus a cancel button is what v1 offers instead.
    /// </remarks>
    Task<AiCompletion> CompleteAsync(AiPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// A one-token round trip for the admin screen's test button.
    /// </summary>
    /// <returns>The model's own reply, or the transport error verbatim. Never the API key.</returns>
    Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record AiProbeResult(bool Ok, string Detail, string? Model);
