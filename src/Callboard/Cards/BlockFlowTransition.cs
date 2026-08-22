namespace Callboard.Cards;

/// <summary>
/// One legal edge in the block flow: a named move from one <see cref="BlockFlowState"/> to
/// another. <see cref="Name"/> is the wire form block B's refusal message and CLI verb dispatch
/// will use to name it — <c>changes-requested</c> is work-lifecycle's own name for the one edge
/// the spec text names explicitly; the rest (<c>brief</c>, <c>claim</c>, <c>submit-for-review</c>,
/// <c>approve</c>, <c>land</c>, <c>close</c>) are named here for the same reason every other edge
/// needs an unambiguous identifier a refusal or a verb can cite. See
/// <see cref="BlockFlowTransitions"/> for the exhaustive table these values populate.
/// </summary>
internal sealed record BlockFlowTransition(string Name, BlockFlowState From, BlockFlowState To);
