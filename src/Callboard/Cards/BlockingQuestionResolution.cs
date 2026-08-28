namespace Callboard.Cards;

/// <summary>
/// What <see cref="CardStore.FindBlockingOpenProductOwnerQuestion"/> answers for one card's
/// <see cref="BlockCardFields.BlockedBy"/> list — a closed union of exactly three cases (§13.7).
/// Replaces the prior <c>(string QuestionId, string Title)?</c> shape, which had only two states
/// (a blocker, or none) and so had nowhere to put a third fact this method has always been able to
/// discover but, before §13.7, silently folded into "none": <em>at least one id could not be
/// resolved to a determinate answer at all</em>. A nullable tuple with a sentinel is not that third
/// state — it is the same two states with one of them mislabelled.
///
/// <list type="bullet">
/// <item><see cref="None"/> — every <c>blocked_by</c> id resolved (to a card that is not a live,
/// Product-Owner-owned, open question, or to nothing at all — <see cref="CardIdentityResolution.
/// NotFound"/> stays correct here: a dangling id names no question). Nothing halts this card.</item>
/// <item><see cref="Blocked"/> — at least one id resolved to a card that is a live, Product-Owner-
/// owned, open (or deferred) question. That question halts this card.</item>
/// <item><see cref="Undetermined"/> — at least one id could not be resolved to a determinate
/// answer: <see cref="CardIdentityResolver.Resolve"/> returned <see cref="CardIdentityResolution.
/// Duplicate"/>, <see cref="CardIdentityResolution.Corrupt"/> or <see cref="CardIdentityResolution.
/// Unreadable"/> for it. Whether this card is halted cannot be said either way.</item>
/// </list>
///
/// <para>
/// <b>§13.7 overturns the prior "resolution failures are conservative by omission" ruling for every
/// write path.</b> The prior ruling treated an unresolvable id as no evidence of a blocking
/// question and let the write proceed — which the task line names for what it is: permissive, not
/// conservative, since omitting a possible blocker is exactly the failure a stop-and-ask exists to
/// prevent. A write-path caller must route <see cref="Undetermined"/> to its own refusal case
/// (never collapse it into <see cref="None"/>) — see <see cref="CardStore.
/// ApplyBlockTransitionUnderExistingLock"/>, <see cref="CardStore.
/// RecordApprovalUnderExistingLock"/> and <see cref="CardStore.ValidateBlockForLanding"/>. A read
/// (<see cref="Callboard.Cards.DerivedStateAssembler"/>, <see cref="Callboard.Cards.
/// WorkingContextAssembler"/>) keeps 13.5's "report, don't refuse" shape instead: it has no process
/// to protect by refusing, only a summary to get right, so <see cref="Undetermined"/> there is
/// folded into the same <see cref="UnreadableCard"/> reporting channel every other unparseable card
/// already uses, not a refusal.
/// </para>
/// </summary>
internal abstract record BlockingQuestionResolution
{
    private BlockingQuestionResolution()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onNone,
        Func<string, string, TResult> onBlocked,
        Func<IReadOnlyList<UnreadableCard>, TResult> onUndetermined);

    internal static BlockingQuestionResolution None() => new NoneCase();

    internal static BlockingQuestionResolution Blocked(string questionId, string questionTitle) => new BlockedCase(questionId, questionTitle);

    internal static BlockingQuestionResolution Undetermined(IReadOnlyList<UnreadableCard> files) => new UndeterminedCase(files);

    private sealed record NoneCase : BlockingQuestionResolution
    {
        internal override TResult Match<TResult>(Func<TResult> onNone, Func<string, string, TResult> onBlocked, Func<IReadOnlyList<UnreadableCard>, TResult> onUndetermined) =>
            onNone();
    }

    /// <param name="QuestionId">The blocking question's id.</param>
    /// <param name="QuestionTitle">The blocking question's title.</param>
    private sealed record BlockedCase(string QuestionId, string QuestionTitle) : BlockingQuestionResolution
    {
        internal override TResult Match<TResult>(Func<TResult> onNone, Func<string, string, TResult> onBlocked, Func<IReadOnlyList<UnreadableCard>, TResult> onUndetermined) =>
            onBlocked(QuestionId, QuestionTitle);
    }

    /// <param name="Files">Every file that made at least one <c>blocked_by</c> id undeterminable —
    /// path and reason together. A duplicate id contributes one entry per claimant file, with
    /// <see cref="UnreadableCard.Reason"/> stating the duplication (that id parsed fine everywhere;
    /// what could not be resolved is which file is the live one) rather than a parser's own
    /// message.</param>
    private sealed record UndeterminedCase(IReadOnlyList<UnreadableCard> Files) : BlockingQuestionResolution
    {
        internal override TResult Match<TResult>(Func<TResult> onNone, Func<string, string, TResult> onBlocked, Func<IReadOnlyList<UnreadableCard>, TResult> onUndetermined) =>
            onUndetermined(Files);
    }
}
