namespace Callboard.Index;

/// <summary>
/// What <see cref="IndexPopulator.Populate"/> did: how many cards and comments it indexed, and
/// every card file that failed to parse along the way — file path and reason, not silently
/// dropped (record-retrieval: a corrupt card degrades the rebuild, it does not abort it or vanish
/// from the report). The <c>index rebuild</c> verb surfaces all three in its JSON; this type only
/// carries the data.
/// </summary>
internal sealed record IndexPopulationResult(
    int IndexedCardCount,
    int IndexedCommentCount,
    IReadOnlyList<(string FilePath, string Reason)> Failures);
