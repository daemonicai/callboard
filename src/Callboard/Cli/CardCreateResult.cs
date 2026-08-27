using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The success result shared by every creation verb built on <see cref="Cards.CardStore.
/// CreateCard"/> (<c>rule create</c>, <c>hazard create</c>, <c>obligation create</c>,
/// <c>decision create</c>, <c>section create</c>, <c>question create</c>, and — from §13 —
/// <c>block create</c>) — one card, one shape, the same "own refusal code, own construction site"
/// discipline the rest of this CLI follows for its failure cases, applied here to the success case
/// instead since every verb succeeds the same way. <see cref="Condition"/>/<see cref="Cadence"/> are
/// set together only for a created <c>hazard</c>, and <see cref="Tasks"/> only for a created
/// <c>block</c> — <see langword="null"/> for every other kind.
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

    /// <summary>Who this card is owed to: the <c>section</c> card id for a created
    /// <c>obligation</c> (§7 block C), or the role that owes the answer for a created
    /// <c>question</c> (§9 block D, carried item G) — <see langword="null"/> for every other
    /// kind.</summary>
    [JsonPropertyName("owedBy")]
    public string? OwedBy { get; init; }

    /// <summary>The section this card was recorded under (<see cref="CardFrontmatter.Section"/>),
    /// empty for a card raised with no <c>--section</c> — most visibly on <c>question create</c>
    /// (§9 block F, carried from block E's review): a caller who omitted <c>--section</c> could not
    /// previously see that fact in the response at all, the same gap block D closed for
    /// <see cref="OwedBy"/>.</summary>
    [JsonPropertyName("section")]
    public required string Section { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The task references a created <c>block</c> implements (§13, work-lifecycle: "Every
    /// block card is minted by the tool"), in the order recorded — <see langword="null"/> for every
    /// other kind.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<string>? Tasks { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CardCreateResult);
}
