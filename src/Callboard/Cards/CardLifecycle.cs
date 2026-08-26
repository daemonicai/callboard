namespace Callboard.Cards;

/// <summary>
/// "Closed", read across every kind's own status vocabulary (working-context: "The response SHALL
/// NOT contain closed cards", record-retrieval: "Closed cards leave the working set without
/// leaving the repository"). Each kind names its own lifecycle in its own closed union — there is
/// no single <c>status: closed</c> string shared across the record — so this is the one place that
/// maps "closed" onto each kind's own terminal wire state, rather than every caller re-deriving
/// the mapping by hand.
///
/// <para>
/// <b>Block, section: their own <c>Closed</c> case.</b> Both flow-state unions name a literal
/// <c>closed</c> state; this reads it directly.
/// </para>
///
/// <para>
/// <b>Rule, hazard, obligation, decision: <see cref="RegisterLifecycleState.Discharged"/>.</b>
/// register: "SHALL be <c>open</c> or <c>discharged</c> and SHALL NOT occupy flow states" — the
/// two-state register lifecycle has no literal <c>closed</c>, and <c>discharged</c> is its
/// terminal state (a rule superseded, an obligation settled).
/// </para>
///
/// <para>
/// <b>Question: <see cref="QuestionStatus.Answered"/> only.</b> process-enforcement: "the close
/// proceeds and the question remains open against its target" — a deferred question is
/// deliberately still live (its target is who now owes the answer), so only <c>answered</c> closes
/// it. This is the one kind where "closed" is not simply "reached the terminal state of a two- or
/// seven-state union" — deferred is a terminal-looking state that the spec explicitly keeps open.
/// </para>
///
/// <para>
/// <b>Finding: never closed.</b> No transition in this build ever moves a <c>finding</c> card's own
/// <c>status</c> off <c>open</c> — a finding is a permanent record of what was found, not a task
/// that completes (findings: a finding is verified or re-verified, never closed). Recurrence moves
/// the <em>block</em> remediating it, not the finding card itself.
/// </para>
/// </summary>
internal static class CardLifecycle
{
    internal static bool IsClosed(CardFile card) => card.Frontmatter.Kind.Match(
        onBlock: () => IsBlockClosed(card),
        onQuestion: () => IsQuestionClosed(card),
        onFinding: static () => false,
        onObligation: () => IsRegisterDischarged(card),
        onRule: () => IsRegisterDischarged(card),
        onHazard: () => IsRegisterDischarged(card),
        onDecision: () => IsRegisterDischarged(card),
        onSection: () => IsSectionClosed(card));

    private static bool IsBlockClosed(CardFile card) =>
        BlockFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var state) && ReferenceEquals(state, BlockFlowState.Closed);

    private static bool IsSectionClosed(CardFile card) =>
        SectionFlowStateWireFormat.TryParse(card.Frontmatter.Status, out var state) && ReferenceEquals(state, SectionFlowState.Closed);

    private static bool IsQuestionClosed(CardFile card) =>
        QuestionStatusWireFormat.TryParse(card.Frontmatter.Status, out var state) && ReferenceEquals(state, QuestionStatus.Answered);

    private static bool IsRegisterDischarged(CardFile card) =>
        RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state) && ReferenceEquals(state, RegisterLifecycleState.Discharged);
}
