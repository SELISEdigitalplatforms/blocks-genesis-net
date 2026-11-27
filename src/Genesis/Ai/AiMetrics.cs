#nullable enable
using System.Diagnostics.Metrics;

namespace Blocks.Genesis;

/// <summary>Token and request counters for AI calls. They feed the AI cost gate; add <see cref="MeterName"/> to the exporter's meters to ship them.</summary>
public static class AiMetrics
{
    public const string MeterName = "blocks.genesis.ai";
    public const string TokensInstrument = "blocks.genesis.ai.tokens";
    public const string RequestsInstrument = "blocks.genesis.ai.requests";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Tokens the provider reported, tagged <c>provider</c>, <c>model</c> and <c>token_type</c> (<c>prompt</c> or <c>completion</c>).</summary>
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(TokensInstrument, "{token}", "Prompt and completion tokens reported by the AI provider");

    /// <summary>Completions attempted, tagged <c>provider</c>, <c>model</c> and <c>outcome</c> (<c>ok</c> or <c>error</c>).</summary>
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(RequestsInstrument, "{request}", "AI chat completions attempted");

    internal static void RecordUsage(string provider, string model, ChatTokenUsage usage)
    {
        Tokens.Add(usage.PromptTokens, new KeyValuePair<string, object?>("provider", provider), new("model", model), new("token_type", "prompt"));
        Tokens.Add(usage.CompletionTokens, new KeyValuePair<string, object?>("provider", provider), new("model", model), new("token_type", "completion"));
    }

    internal static void RecordRequest(string provider, string model, bool ok) =>
        Requests.Add(1, new KeyValuePair<string, object?>("provider", provider), new("model", model), new("outcome", ok ? "ok" : "error"));
}
