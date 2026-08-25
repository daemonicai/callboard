using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The one JSON contract every machine-facing command emits on stdout: a single line, `ok`
/// discriminating success from refusal, with the two payload shapes mutually exclusive.
/// </summary>
internal sealed class CliEnvelope
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("result")]
    public System.Text.Json.JsonElement? Result { get; init; }

    [JsonPropertyName("refusal")]
    public CliRefusal? Refusal { get; init; }
}

internal sealed class CliRefusal
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The rule that refused the attempt (process-enforcement: "Refusals are explained
    /// and attributable", §9 block A) — present only for a refusal sourced from an outcome case
    /// that implements <see cref="Cards.ICardRefusalReason"/>; omitted (never emitted as
    /// <c>null</c> — see <see cref="CliJsonContext"/>'s <c>WhenWritingNull</c> policy) for a
    /// refusal this build has not yet retrofitted onto that mechanism.</summary>
    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    /// <summary>What would satisfy <see cref="Rule"/> — same population rule as
    /// <see cref="Rule"/>.</summary>
    [JsonPropertyName("remedy")]
    public string? Remedy { get; init; }
}
