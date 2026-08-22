namespace Callboard.Cards;

/// <summary>
/// One kind whose committed counter file has fallen behind the highest identity number actually
/// observed on disk for that kind — the shape <see cref="CardIdentityAllocator.VerifyCounters"/>
/// reports. This is never produced as, or turned into, a refusal: D4 makes rebuild survive
/// degraded, and a counter that can only go forward is restored by simply raising it, not by
/// stopping the loop. §9 owns the closed refusal set; this type is deliberately not a member of it.
/// </summary>
internal sealed record CardIdentityCounterViolation(CardKind Kind, int CounterValue, int ObservedMaxId, string Reason);
