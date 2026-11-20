#nullable enable
using System.Net;

namespace Blocks.Genesis;

/// <summary>One message of a chat completion request: a role (<c>system</c>, <c>user</c>, <c>assistant</c>) and its text.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>Per-call settings. A value left null falls back to the <see cref="AiOptions"/> the client was built with.</summary>
public sealed record ChatCompletionOptions
{
    /// <summary>Model (OpenAI) or deployment hint (Azure). Null uses <see cref="AiOptions.Model"/>.</summary>
    public string? Model { get; init; }

    /// <summary>Sampling temperature. Null uses <see cref="AiOptions.Temperature"/>; when both are null the provider default applies.</summary>
    public double? Temperature { get; init; }

    /// <summary>Upper bound on generated tokens. Null leaves the provider default.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>Prompt and completion token counts the provider reported for one response.</summary>
public sealed record ChatTokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// The model's answer. <see cref="Text"/> is null when the response had no message content
/// (an empty string is a real, empty answer). <see cref="Usage"/> is null when the provider reported none.
/// </summary>
public sealed record ChatCompletionResult(string? Text, ChatTokenUsage? Usage, string? Model);

/// <summary>
/// A failed completion: the provider answered with an error status (<see cref="StatusCode"/>, and
/// <see cref="RetryAfter"/> when it asked the caller to wait), the response could not be read,
/// or the client is not configured (<see cref="StatusCode"/> is null). The provider's response body is never put in the message.
/// </summary>
public sealed class ChatCompletionException : Exception
{
    public ChatCompletionException(string message, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// One chat-completion client for every AI feature. Implementations: <see cref="AzureOpenAiChatClient"/>
/// (<c>api-key</c> header) and <see cref="OpenAiCompatibleChatClient"/> (<c>Authorization: Bearer</c>).
/// Throws <see cref="ChatCompletionException"/> on a provider or configuration failure and
/// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
/// </summary>
public interface IChatCompletionClient
{
    Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions? options = null,
        CancellationToken cancellationToken = default);
}
