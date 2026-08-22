using Callboard.Cards;

namespace Callboard.Index;

/// <summary>
/// What <see cref="IndexPopulator.Populate"/> did: how many cards and comments it indexed, every
/// card file that failed to parse along the way — file path and reason, not silently dropped
/// (record-retrieval: a corrupt card degrades the rebuild, it does not abort it or vanish from the
/// report) — and every kind whose committed identity counter has fallen behind the highest
/// identity actually observed on disk (4.2's <see cref="CardIdentityAllocator.VerifyCounters"/>
/// check). Both lists are reported, neither is a refusal. The <c>index rebuild</c> verb surfaces
/// all of this in its JSON; this type only carries the data.
/// </summary>
internal sealed record IndexPopulationResult(
    int IndexedCardCount,
    int IndexedCommentCount,
    IReadOnlyList<(string FilePath, string Reason)> Failures,
    IReadOnlyList<CardIdentityCounterViolation> IdentityCounterViolations);
