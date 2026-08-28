using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// One card file a read found and could not parse — <see cref="Cards.UnreadableCard"/> on the
/// wire (§13.5). Every read that walks the record carries an <c>unreadable</c> array of these:
/// <c>state</c>, <c>context</c>, <c>view</c>, <c>section export</c>, <c>change export</c>,
/// <c>rule review</c> and <c>section status</c>. One shape across all of them, so an agent parsing
/// any response reads the same two keys and does not have to learn a per-command spelling of the
/// same fact.
///
/// <para>
/// <b>The response still succeeds.</b> A corrupt card is reported here, not raised as a refusal:
/// record-retrieval's "Damage is contained" requires every other card to remain readable and
/// usable, and refusing a read because one unrelated file is corrupt would halt every query on
/// the record instead of narrowing one answer. The array is empty — never absent — when
/// everything the read walked parsed cleanly.
/// </para>
/// </summary>
internal sealed class UnreadableCardResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>Why the file would not parse, as the parser stated it — not a generic "corrupt"
    /// label. An agent that knows the reason can fix the file; one told only that a file is
    /// unreadable has to go and look.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Maps <see cref="Cards.UnreadableCard"/>s onto their wire form, in the order the
    /// producing read already sorted them (file path, ordinal) — the one place this projection
    /// lives, so no handler restates it.</summary>
    internal static IReadOnlyList<UnreadableCardResult> From(IReadOnlyList<Cards.UnreadableCard> unreadable) =>
    [
        .. unreadable.Select(static entry => new UnreadableCardResult
        {
            FilePath = entry.FilePath,
            Reason = entry.Reason,
        })
    ];
}
