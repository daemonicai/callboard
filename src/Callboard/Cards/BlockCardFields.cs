using System.Collections.Immutable;

namespace Callboard.Cards;

/// <summary>
/// The six frontmatter fields work-lifecycle's "Blocks carry their brief context" names as known
/// on a <c>block</c> card only: <c>base</c> (the commit its brief was carved against),
/// <c>reviewed_state</c> (the commit a reviewer actually reviewed), <c>tasks</c> (the task
/// references it implements), its recorded <c>gate_results</c> (§5 block D), <c>round</c> (its
/// current remediation round), and <c>blocked_by</c> (the cards it is blocked by). Not part of
/// <see cref="CardFrontmatter"/> —
/// see that type's doc comment for why kind-specific fields live in their own type instead: a
/// <c>question</c> or <c>finding</c> card carries none of this, and giving every card a
/// <see cref="CardFrontmatter"/>-level <c>Base</c> would make an inapplicable field representable
/// on kinds that shouldn't have one at all.
///
/// <para>
/// <b>Known only on a <c>block</c> card (Architect ruling, §5 block A brief).</b> The same five
/// keys hand-written on a <c>question</c>, <c>finding</c>, or any other kind stay
/// preserved-unknown on <see cref="CardFile.UnknownFrontmatterFields"/>, untouched — exactly as
/// they were before this type existed. <see cref="CardFileParser"/> is what decides which of the
/// two homes a given card's five keys land in, based on the card's own <c>kind</c>.
/// </para>
///
/// <para>
/// All five fields are optional here — block A carries the vocabulary, not the enforcement.
/// Whether <c>Base</c> must be set before a block reaches <c>briefed</c> (work-lifecycle:
/// "`base` SHALL be recorded before the block is briefed") is 5.5's refusal, not this type's job.
/// </para>
///
/// <para>
/// <b>An empty or whitespace-only <c>Tasks</c>/<c>BlockedBy</c> item is unrepresentable
/// (reviewer finding 1, §5 block A review — reopened once, closed properly the second time).</b>
/// <see cref="CardFileFormat.JoinFrontmatterList"/> joins a list's items with a comma separator,
/// so a one-element list holding a single empty string joins to exactly the same raw text
/// (<see cref="string.Empty"/>) as an empty list — the two are indistinguishable on the wire, and
/// <see cref="CardFileFormat.SplitFrontmatterList"/> resolves that collision by always reading
/// empty raw text back as an empty list. Rather than invent a second on-the-wire spelling to
/// disambiguate the two, this type deletes the value that could collide: task references and card
/// identities are never empty or whitespace-only in the first place, so nothing constructed
/// through this type's public surface can hold one.
/// </para>
///
/// <para>
/// <b>What "nothing constructed through this type's public surface can hold one" actually means,
/// stated precisely rather than overstated (the first attempt at this fix claimed more than it
/// delivered — see the reopened review).</b> The first attempt validated only in the hand-written
/// constructor; a positional-looking record still synthesises a clone-and-<c>with</c> path that
/// sets <c>init</c> properties directly, bypassing any validation that lives only in the
/// constructor body, and a constructor that stores a caller's own <see cref="List{T}"/> by
/// reference lets a caller validate-then-mutate after the fact. Both compiled. Both are closed
/// here, together, by storing <see cref="Tasks"/> and <see cref="BlockedBy"/> as
/// <see cref="ImmutableArray{T}"/> behind a validating <c>init</c> accessor (backed by a private
/// field, not an auto-property) rather than in the constructor alone: the accessor is the *one*
/// place both the constructor (which assigns through it) and a <c>with</c> expression (which the
/// compiler also lowers to an assignment through it) are forced to pass, and
/// <see cref="ImmutableArray{T}"/> itself has no aliasable backing store a caller could retain and
/// mutate after handing it in — the constructor's <c>.ToImmutableArray()</c> call is the copy that
/// makes retained-reference mutation structurally unable to reach the built value, not merely
/// unlikely to.
/// </para>
///
/// <para>
/// This is a <b>runtime guarantee that nothing this type's public surface can construct or clone
/// holds an empty item</b> — not a compile-time impossibility. A caller with `unsafe` code, raw
/// reflection over the private backing fields, or another assembly with `InternalsVisibleTo`
/// access could still defeat it; none of those are reachable from this codebase's own call sites,
/// which is the guarantee actually worth having here.
/// </para>
/// </summary>
internal sealed record BlockCardFields
{
    /// <summary>The commit the block's brief was carved against, or <see langword="null"/> when
    /// not yet recorded. SHALL NOT change across remediation rounds (work-lifecycle) — enforcing
    /// that is 5.2/5.5's job, not this type's.</summary>
    internal string? Base { get; init; }

    /// <summary>The commit a reviewer actually reviewed, or <see langword="null"/> when no review
    /// has happened yet.</summary>
    internal string? ReviewedState { get; init; }

    private readonly ImmutableArray<string> _tasks;

    /// <summary>The task references (e.g. <c>5.1</c>) this block implements, in the order
    /// recorded. Never contains an empty or whitespace-only item — see this type's own doc
    /// comment for how that is enforced on every path in, including <c>with</c>. An empty array
    /// is "none recorded", the same convention <see cref="CardFrontmatter.Section"/> uses for
    /// absence.</summary>
    internal ImmutableArray<string> Tasks
    {
        get => _tasks;
        init => _tasks = RequireNoEmptyOrWhitespaceItems(value, nameof(Tasks));
    }

    /// <summary>The block's current remediation round, or <see langword="null"/> when not yet
    /// recorded. <c>changes-requested</c> (<see cref="BlockFlowTransitions"/>) increments this —
    /// applying that increment is block B's job.</summary>
    internal int? Round { get; init; }

    private readonly ImmutableArray<string> _blockedBy;

    /// <summary>The ids of the cards this block is blocked by, in the order recorded. Never
    /// contains an empty or whitespace-only item — see <see cref="Tasks"/>'s doc comment; the same
    /// enforcement applies here. A non-empty set is what work-lifecycle's "Blocked is derived, not
    /// stored" derives blocked-ness from — deriving it is 5.7's job, not this type's.</summary>
    internal ImmutableArray<string> BlockedBy
    {
        get => _blockedBy;
        init => _blockedBy = RequireNoEmptyOrWhitespaceItems(value, nameof(BlockedBy));
    }

    private readonly ImmutableArray<GateResult> _gateResults;

    /// <summary>The block's recorded gate results (work-lifecycle: "Gate results are recorded as
    /// exit codes", §5 block D), at most one entry per <see cref="GateResult.Label"/> — recording
    /// a second result for a label already present replaces it
    /// (<see cref="CardStore.RecordGateResultUnderExistingLock"/>), it does not append a second,
    /// ambiguous entry. Never contains a duplicate label or an invalid one — see
    /// <see cref="GateResult.IsValidLabel"/>, enforced by the same three-door discipline
    /// <see cref="Tasks"/>/<see cref="BlockedBy"/> already apply (constructor, <c>with</c>, and
    /// <see cref="CardFileParser"/>'s own pre-construction check).</summary>
    internal ImmutableArray<GateResult> GateResults
    {
        get => _gateResults;
        init => _gateResults = RequireValidGateResults(value);
    }

    /// <summary>The six fields, all unset — every card that is not a <c>block</c>, and a
    /// brand-new block with no brief context recorded yet.</summary>
    internal static readonly BlockCardFields Empty = new(null, null, [], null, [], []);

    internal BlockCardFields(
        string? Base, string? ReviewedState, IReadOnlyList<string> Tasks, int? Round, IReadOnlyList<string> BlockedBy, IReadOnlyList<GateResult> GateResults)
    {
        this.Base = Base;
        this.ReviewedState = ReviewedState;

        // .ToImmutableArray() copies Tasks/BlockedBy/GateResults's current contents now, at
        // construction time — this is what makes a caller's later mutation of the source list (if
        // the argument was a List<T> the caller kept a reference to) structurally unable to reach
        // the value being built here, not merely unlikely to (reviewer's bypass 2). The assignment
        // then runs through this type's own init accessor above, the same one `with` is lowered to
        // use, which is what closes bypass 1.
        this.Tasks = Tasks.ToImmutableArray();
        this.Round = Round;
        this.BlockedBy = BlockedBy.ToImmutableArray();
        this.GateResults = GateResults.ToImmutableArray();
    }

    /// <summary>What the card reports for <paramref name="label"/> — see <see cref="GateStatus"/>'s
    /// own doc comment for why this reads exclusively from <see cref="GateResults"/> and nowhere
    /// else, structurally, not by convention.</summary>
    internal GateStatus GateStatusOf(string label)
    {
        foreach (var result in GateResults)
        {
            if (string.Equals(result.Label, label, StringComparison.Ordinal))
            {
                return GateStatus.Recorded(result.ExitCode);
            }
        }

        return GateStatus.Absent;
    }

    /// <summary>
    /// The one predicate every door into this type's <see cref="Tasks"/>/<see cref="BlockedBy"/>
    /// reacts to — the constructor (via the init accessors above), a <c>with</c> expression (the
    /// same accessors, lowered), and <see cref="CardFileParser"/>'s own pre-construction check
    /// over raw split items straight off the wire — so none of the three can drift on what counts
    /// as an item this type refuses to hold.
    /// </summary>
    internal static bool IsValidListItem(string item) => !string.IsNullOrWhiteSpace(item);

    private static ImmutableArray<string> RequireNoEmptyOrWhitespaceItems(ImmutableArray<string> items, string paramName)
    {
        foreach (var item in items)
        {
            if (!IsValidListItem(item))
            {
                throw new ArgumentException(
                    $"'{paramName}' cannot contain an empty or whitespace-only item — task references and " +
                    "card identities are never empty, and an empty item is indistinguishable on the wire " +
                    "from an empty list.",
                    paramName);
            }
        }

        return items;
    }

    /// <summary>
    /// The gate-results equivalent of <see cref="RequireNoEmptyOrWhitespaceItems"/>: every label
    /// must be a valid one (<see cref="GateResult.IsValidLabel"/>), and no label may appear twice —
    /// a second recording of the same label is an update (<see cref="CardStore.
    /// RecordGateResultUnderExistingLock"/>), never a second, ambiguous entry this type could be
    /// asked to disagree with itself over.
    /// </summary>
    private static ImmutableArray<GateResult> RequireValidGateResults(ImmutableArray<GateResult> results)
    {
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (!GateResult.IsValidLabel(result.Label))
            {
                throw new ArgumentException(
                    $"gate result label '{result.Label}' is invalid — a label cannot be empty, whitespace-only, " +
                    "or contain '=' or ','.",
                    nameof(results));
            }

            if (!seenLabels.Add(result.Label))
            {
                throw new ArgumentException(
                    $"gate result label '{result.Label}' is recorded more than once — recording a gate result " +
                    "again for a label already present replaces it, it does not add a second entry.",
                    nameof(results));
            }
        }

        return results;
    }

    // Same reason as CardComment's own override: ImmutableArray<T>'s own Equals compares the
    // underlying array by reference, not element-wise — two structurally identical arrays built
    // separately (a freshly-parsed BlockCardFields vs. one built by hand) would otherwise never
    // compare equal even when every element genuinely matches.
    public bool Equals(BlockCardFields? other) =>
        other is not null
        && Base == other.Base
        && ReviewedState == other.ReviewedState
        && Tasks.SequenceEqual(other.Tasks)
        && Round == other.Round
        && BlockedBy.SequenceEqual(other.BlockedBy)
        && GateResults.SequenceEqual(other.GateResults);

    public override int GetHashCode() =>
        HashCode.Combine(Base, ReviewedState, Tasks.Length, Round, BlockedBy.Length, GateResults.Length);
}
