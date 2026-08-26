using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>obligation decline</c> command's success result (§9 block F, register: "An obligation
/// that will not be met SHALL be closable by declining it with a recorded reason"). Same "recorded,
/// not merely echoed from argv" shape as <see cref="CardRegisterDischargeResult"/>, plus
/// <see cref="Reason"/> — the one fact a decline response cannot omit without defeating the whole
/// point of the verb: a caller reading this response back must be able to see the reason without a
/// second read of the card file.
/// </summary>
internal sealed class ObligationDeclineResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("declinedAt")]
    public required DateTimeOffset DeclinedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ObligationDeclineResult);
}
