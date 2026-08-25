namespace Callboard.Cards;

/// <summary>
/// The three states a <c>question</c> card's own <c>status</c> field occupies (§9 block D —
/// <see cref="Callboard.Cli.CommandDispatcher.RunQuestionCreate"/>'s own doc comment names this the
/// vocabulary §7 deliberately stopped short of): <c>open</c>, <c>answered</c> or <c>deferred</c>.
/// Modelled the same way as <see cref="BlockFlowState"/>, <see cref="SectionFlowState"/> and
/// <see cref="RegisterLifecycleState"/> — a private constructor and three sealed nested cases close
/// the hierarchy to this file, and <see cref="Match{TResult}"/> is the only way to consume a value.
/// See <see cref="CardKind"/>'s doc comment for why this is a closed union and not a C# <c>enum</c>.
///
/// <para>
/// <b>Its own type, not folded into <see cref="RegisterLifecycleState"/>.</b> A question is not one
/// of the four register kinds (register: "Register kinds have a two-state lifecycle... and SHALL NOT
/// occupy flow states" — the same "SHALL NOT" that already keeps a block/section's own flow states
/// apart from this one), and a two-state open/discharged reader would parse <c>answered</c> or
/// <c>deferred</c> as an unrecognised status rather than a distinct, deliberate state. Deferred is
/// not "discharged" either: register's own scenario ("Question outlives its change") — "the question
/// remains open and continues to surface to the role that owes its answer" — is exactly what a
/// deferred question still does; discharged means settled, and a deferred question is not settled,
/// only redirected.
/// </para>
///
/// <para>
/// <b><c>answered</c> and <c>deferred</c> are both terminal-for-this-verb-surface, not
/// interchangeable with each other or with a return to <c>open</c>.</b> <see cref="CardStore.
/// AnswerQuestionUnderExistingLock"/> and <see cref="CardStore.DeferQuestionUnderExistingLock"/> both
/// refuse when the card is not currently <c>open</c> (<c>QuestionNotOpen</c>) — there is no edge back
/// from either terminal state on this build's surface, the same "answer or defer, once, and that is
/// the record" discipline a register card's own open→discharged edge already has no return path for.
/// </para>
/// </summary>
internal abstract record QuestionStatus
{
    private QuestionStatus()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onOpen,
        Func<TResult> onAnswered,
        Func<TResult> onDeferred);

    internal static readonly QuestionStatus Open = new OpenCase();
    internal static readonly QuestionStatus Answered = new AnsweredCase();
    internal static readonly QuestionStatus Deferred = new DeferredCase();

    private sealed record OpenCase : QuestionStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onAnswered, Func<TResult> onDeferred) => onOpen();
    }

    private sealed record AnsweredCase : QuestionStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onAnswered, Func<TResult> onDeferred) => onAnswered();
    }

    private sealed record DeferredCase : QuestionStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onAnswered, Func<TResult> onDeferred) => onDeferred();
    }
}

/// <summary>
/// The wire form of <see cref="QuestionStatus"/> — the text a <c>question</c> card's <c>status</c>
/// field carries — and the parse path back. Ordinal comparison throughout, same reason as
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class QuestionStatusWireFormat
{
    private static readonly IReadOnlyDictionary<string, QuestionStatus> ByWireValue =
        new Dictionary<string, QuestionStatus>(StringComparer.Ordinal)
        {
            ["open"] = QuestionStatus.Open,
            ["answered"] = QuestionStatus.Answered,
            ["deferred"] = QuestionStatus.Deferred,
        };

    internal static string ToWireString(this QuestionStatus status) => status.Match(
        onOpen: static () => "open",
        onAnswered: static () => "answered",
        onDeferred: static () => "deferred");

    /// <summary>The recognised wire values, in the order register's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out QuestionStatus status)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null QuestionStatus singleton, so `match` is
        // non-null whenever `found` is true; the fallback to Open on failure is discarded by every
        // caller, which always checks the returned bool first.
        status = found ? match! : QuestionStatus.Open;
        return found;
    }
}
