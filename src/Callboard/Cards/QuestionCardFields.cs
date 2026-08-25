namespace Callboard.Cards;

/// <summary>
/// The frontmatter fields known only on a <c>question</c> card (§9 block D, process-enforcement:
/// "An answer must be written down"). Not part of <see cref="CardFrontmatter"/>, and not folded into
/// <see cref="RegisterCardFields"/> — see that type's own doc comment for why kind-specific fields
/// live in their own type, and see <see cref="CardFileWriter"/>'s <c>isRegisterCard</c> gate for the
/// structural reason a question specifically cannot share that type: <see cref="CardFileParser"/>
/// only ever populates <see cref="CardFile.RegisterFields"/> for the four register kinds, so a value
/// set there for a question would silently never reach disk.
///
/// <para>
/// <b>An answer names exactly one of a <c>decision</c> reference or an inline answer, never
/// both (process-enforcement: "unless it names the <c>decision</c> card recording the answer, or
/// records the answer inline where it is trivial").</b> <see cref="AnswerDecisionId"/> holds a
/// <b>decision card id</b>, resolved through <see cref="CardIdentityResolver"/> before an answer is
/// ever recorded — the same "never a free-text label" discipline <see cref="RegisterCardFields.
/// OwedBy"/>'s own doc comment establishes for an obligation's owed-to section.
/// <see cref="AnswerInline"/> holds the answer's own text for the trivial case. Both
/// <see langword="null"/> while the question is still <see cref="QuestionStatus.Open"/>; set
/// together with <see cref="AnsweredBy"/>/<see cref="AnsweredAt"/>, never independently, by
/// <see cref="CardStore.AnswerQuestionUnderExistingLock"/> — the only writer of any of the four.
/// </para>
///
/// <para>
/// <b><see cref="DeferredTarget"/> is free text, not a resolved card id (Architect ruling, §9 block
/// D).</b> Register's own scenario names the target as "a named later section or change" — a section
/// that may not exist as a card yet at the moment a question is deferred to it (a future section of
/// the same change, or a change not yet proposed). Requiring it to resolve through
/// <see cref="CardIdentityResolver"/> the way <see cref="AnswerDecisionId"/> and
/// <see cref="RegisterCardFields.OwedBy"/> do would make deferring to work that has no card yet
/// impossible — exactly the case this field exists to name. Set together with <see cref="DeferredBy"/>/
/// <see cref="DeferredAt"/>, never independently, by <see cref="CardStore.
/// DeferQuestionUnderExistingLock"/> — the only writer of any of the three.
/// </para>
/// </summary>
internal sealed record QuestionCardFields
{
    /// <summary>The role that answered this question, or <see langword="null"/> while it is not
    /// <see cref="QuestionStatus.Answered"/>. Set together with <see cref="AnsweredAt"/>, never
    /// independently.</summary>
    internal CardOwner? AnsweredBy { get; init; }

    /// <summary>When this question was answered, or <see langword="null"/> while it is not
    /// <see cref="QuestionStatus.Answered"/>.</summary>
    internal DateTimeOffset? AnsweredAt { get; init; }

    /// <summary>The id of the <c>decision</c> card recording the answer, or <see langword="null"/>
    /// when the answer was recorded inline instead — see this type's own doc comment for why the
    /// two are mutually exclusive but neither is required over the other.</summary>
    internal string? AnswerDecisionId { get; init; }

    /// <summary>The answer's own text, recorded inline for a trivial answer, or
    /// <see langword="null"/> when a <see cref="AnswerDecisionId"/> was named instead.</summary>
    internal string? AnswerInline { get; init; }

    /// <summary>The role that deferred this question, or <see langword="null"/> while it is not
    /// <see cref="QuestionStatus.Deferred"/>. Set together with <see cref="DeferredAt"/>, never
    /// independently.</summary>
    internal CardOwner? DeferredBy { get; init; }

    /// <summary>When this question was deferred, or <see langword="null"/> while it is not
    /// <see cref="QuestionStatus.Deferred"/>.</summary>
    internal DateTimeOffset? DeferredAt { get; init; }

    /// <summary>The later section or change this question is deferred to, named as free text — see
    /// this type's own doc comment for why it is not a resolved card id. <see langword="null"/>
    /// while the question is not <see cref="QuestionStatus.Deferred"/>.</summary>
    internal string? DeferredTarget { get; init; }

    /// <summary>The seven fields, all unset — every card that is not a <c>question</c>, and a
    /// brand-new question with neither an answer nor a deferral recorded yet.</summary>
    internal static readonly QuestionCardFields Empty = new();
}
