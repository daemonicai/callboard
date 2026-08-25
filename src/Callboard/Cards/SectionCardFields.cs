using System.Collections.Immutable;

namespace Callboard.Cards;

/// <summary>
/// The three frontmatter fields work-lifecycle's "Sections are entities" requirement names as known
/// on a <c>section</c> card only: its <c>base</c> commit (the same "brief context" concept
/// <see cref="BlockCardFields.Base"/> carries for a block, reused rather than respelled — §5 block E
/// brief: "identity is kind-prefixed and allocated from the committed counter, never from the
/// index", and the same discipline extends to not inventing a second spelling for a concept a
/// sibling kind already names), and <c>closed_by</c>/<c>closed_at</c> (work-lifecycle: "closing it
/// SHALL record the acting role and the time"). Not part of <see cref="CardFrontmatter"/> — see that
/// type's doc comment for why kind-specific fields live in their own type instead, the same reason
/// <see cref="BlockCardFields"/> is its own type rather than living there.
///
/// <para>
/// <b>The section's own <c>status</c> (open/closed) is <em>not</em> a field on this type — it lives
/// on <see cref="CardFrontmatter.Status"/>, read through <see cref="SectionFlowStateWireFormat"/>,
/// exactly the way <see cref="BlockFlowState"/> reads a block card's own status.</b> This is the
/// structural half of work-lifecycle's hardest scenario here ("the system answers from the section
/// entity without requiring its cards to be read"): <see cref="SectionFlowStateWireFormat.TryParse"/>
/// takes a single wire string — the section card's own <c>status</c> field — and nothing else. There
/// is no method anywhere on this type, on <see cref="SectionFlowState"/>, or on
/// <see cref="CardStore"/>'s section-reading surface whose signature accepts a directory listing, an
/// <c>IReadOnlyList&lt;CardFile&gt;</c>, or any other channel a "walk the cards this section raised
/// and aggregate their state" implementation would need — the same class of guarantee
/// <see cref="BlockCardFields.GateStatusOf"/> gives against <see cref="CardFile.Comments"/>: not
/// "nothing currently does this", but "nothing currently in scope for this signature <em>could</em>
/// do this and still compile as a call to it". A plausible wrong alternative this rules out
/// concretely: deriving a section's status from whether every block card carrying its identity in
/// <see cref="CardFrontmatter.Section"/> has itself reached <c>closed</c> — see
/// <c>SectionStatusStructuralTests</c> for the test that proves this by construction, not just by
/// today's call graph.
/// </para>
///
/// <para>
/// All three fields here are optional — this type carries the vocabulary, the same "block A carries
/// the vocabulary, not the enforcement" convention <see cref="BlockCardFields"/>'s own doc comment
/// states. Whether a section may close (§9's obligations/questions/threads conditions) is not this
/// type's job; recording <em>that</em> it closed, by whom and when, is.
/// </para>
/// </summary>
internal sealed record SectionCardFields
{
    /// <summary>The commit the section's base was carved against, or <see langword="null"/> when
    /// not yet recorded — the section-scoped counterpart of <see cref="BlockCardFields.Base"/>.</summary>
    internal string? Base { get; init; }

    /// <summary>The role that closed this section, or <see langword="null"/> while it is still
    /// open. Set together with <see cref="ClosedAt"/>, never independently —
    /// <see cref="CardStore.CloseSectionUnderExistingLock"/> is the only writer of either.</summary>
    internal CardOwner? ClosedBy { get; init; }

    /// <summary>When this section was closed, or <see langword="null"/> while it is still open.</summary>
    internal DateTimeOffset? ClosedAt { get; init; }

    private readonly ImmutableArray<SectionVerdictEntry> _verdicts;

    /// <summary>The section's recorded supervisor verdicts (work-lifecycle: "the verdict, the
    /// range and the acting role are recorded against that section entity"), oldest first — see
    /// <see cref="SectionVerdictEntry"/>'s own doc comment for why this is an append-only sequence
    /// rather than a single overwritable verdict.</summary>
    internal ImmutableArray<SectionVerdictEntry> Verdicts
    {
        get => _verdicts;
        init => _verdicts = value;
    }

    private readonly ImmutableArray<SectionAuthorisationEntry> _authorisations;

    /// <summary>The section's recorded Product Owner authorisations (work-lifecycle: "Remediation
    /// beyond the second round requires recorded authorisation", §8a block C), oldest first — see
    /// <see cref="SectionAuthorisationEntry"/>'s own doc comment for why this lives here, appended
    /// the same way <see cref="Verdicts"/> is, rather than as a separate <c>decision</c> card.</summary>
    internal ImmutableArray<SectionAuthorisationEntry> Authorisations
    {
        get => _authorisations;
        init => _authorisations = value;
    }

    /// <summary>The four fields, all unset — every card that is not a <c>section</c>, and a
    /// brand-new section with no verdict, no authorisation and no closure recorded yet.</summary>
    internal static readonly SectionCardFields Empty = new(null, null, null, [], []);

    internal SectionCardFields(
        string? Base,
        CardOwner? ClosedBy,
        DateTimeOffset? ClosedAt,
        IReadOnlyList<SectionVerdictEntry> Verdicts,
        IReadOnlyList<SectionAuthorisationEntry> Authorisations)
    {
        this.Base = Base;
        this.ClosedBy = ClosedBy;
        this.ClosedAt = ClosedAt;

        // .ToImmutableArray() copies Verdicts's/Authorisations's current contents now, at
        // construction time — the same reviewer-closed bypass BlockCardFields.Tasks/BlockedBy's own
        // doc comment explains: a caller's later mutation of a retained List<T> source cannot reach
        // the value built here.
        this.Verdicts = Verdicts.ToImmutableArray();
        this.Authorisations = Authorisations.ToImmutableArray();
    }

    // Same reason as BlockCardFields's own override: ImmutableArray<T>'s own Equals compares the
    // underlying array by reference, not element-wise.
    public bool Equals(SectionCardFields? other) =>
        other is not null
        && Base == other.Base
        && ClosedBy == other.ClosedBy
        && ClosedAt == other.ClosedAt
        && Verdicts.SequenceEqual(other.Verdicts)
        && Authorisations.SequenceEqual(other.Authorisations);

    public override int GetHashCode() =>
        HashCode.Combine(Base, ClosedBy, ClosedAt, Verdicts.Length, Authorisations.Length);
}
