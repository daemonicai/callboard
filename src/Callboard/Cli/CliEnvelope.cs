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
}
