using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The success result shared by every §7 block A creation verb (<c>rule create</c>,
/// <c>hazard create</c>, <c>obligation create</c>, <c>decision create</c>, <c>section create</c>) —
/// one card, one shape, the same "own refusal code, own construction site" discipline the rest of
/// this CLI follows for its failure cases, applied here to the success case instead since all five
/// verbs succeed the same way. <see cref="Condition"/>/<see cref="Cadence"/> are set together only
/// for a created <c>hazard</c> — <see langword="null"/> for every other kind.
/// </summary>
internal sealed class CardCreateResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    [JsonPropertyName("cadence")]
    public string? Cadence { get; init; }

    /// <summary>The <c>section</c> card id this obligation is owed to — set for a created
    /// <c>obligation</c> (§7 block C), <see langword="null"/> for every other kind.</summary>
    [JsonPropertyName("owedBy")]
    public string? OwedBy { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CardCreateResult);
}
