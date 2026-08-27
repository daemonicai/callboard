namespace Callboard.Cards;

/// <summary>
/// The five roles a card's <c>owner</c> can name (card-model: "Ownership names whose turn it is"),
/// and the same five roles a comment's <c>to</c> can address (card-model: "Append-only addressed
/// comment threads"). One type for both — the spec draws the role vocabulary from a single list.
/// Modelled as a closed union for the same reason as <see cref="CardKind"/> — see that type's
/// doc comment.
/// </summary>
internal abstract record CardOwner
{
    private CardOwner()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onArchitect,
        Func<TResult> onWorker,
        Func<TResult> onReviewer,
        Func<TResult> onSupervisor,
        Func<TResult> onProductOwner);

    internal static readonly CardOwner Architect = new ArchitectCase();
    internal static readonly CardOwner Worker = new WorkerCase();
    internal static readonly CardOwner Reviewer = new ReviewerCase();
    internal static readonly CardOwner Supervisor = new SupervisorCase();
    internal static readonly CardOwner ProductOwner = new ProductOwnerCase();

    private sealed record ArchitectCase : CardOwner
    {
        internal override TResult Match<TResult>(Func<TResult> onArchitect, Func<TResult> onWorker, Func<TResult> onReviewer, Func<TResult> onSupervisor, Func<TResult> onProductOwner) => onArchitect();
    }

    private sealed record WorkerCase : CardOwner
    {
        internal override TResult Match<TResult>(Func<TResult> onArchitect, Func<TResult> onWorker, Func<TResult> onReviewer, Func<TResult> onSupervisor, Func<TResult> onProductOwner) => onWorker();
    }

    private sealed record ReviewerCase : CardOwner
    {
        internal override TResult Match<TResult>(Func<TResult> onArchitect, Func<TResult> onWorker, Func<TResult> onReviewer, Func<TResult> onSupervisor, Func<TResult> onProductOwner) => onReviewer();
    }

    private sealed record SupervisorCase : CardOwner
    {
        internal override TResult Match<TResult>(Func<TResult> onArchitect, Func<TResult> onWorker, Func<TResult> onReviewer, Func<TResult> onSupervisor, Func<TResult> onProductOwner) => onSupervisor();
    }

    private sealed record ProductOwnerCase : CardOwner
    {
        internal override TResult Match<TResult>(Func<TResult> onArchitect, Func<TResult> onWorker, Func<TResult> onReviewer, Func<TResult> onSupervisor, Func<TResult> onProductOwner) => onProductOwner();
    }
}

/// <summary>
/// Wire form of <see cref="CardOwner"/> and the parse path back, matched with explicit
/// <see cref="StringComparer.Ordinal"/> — see <see cref="CardKindWireFormat"/> for why.
/// </summary>
internal static class CardOwnerWireFormat
{
    private static readonly IReadOnlyDictionary<string, CardOwner> ByWireValue =
        new Dictionary<string, CardOwner>(StringComparer.Ordinal)
        {
            ["architect"] = CardOwner.Architect,
            ["worker"] = CardOwner.Worker,
            ["reviewer"] = CardOwner.Reviewer,
            ["supervisor"] = CardOwner.Supervisor,
            ["product-owner"] = CardOwner.ProductOwner,
        };

    internal static string ToWireString(this CardOwner owner) => owner.Match(
        onArchitect: static () => "architect",
        onWorker: static () => "worker",
        onReviewer: static () => "reviewer",
        onSupervisor: static () => "supervisor",
        onProductOwner: static () => "product-owner");

    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    /// <summary>Every recognised <see cref="CardOwner"/>, in the same order as <see
    /// cref="RecognisedValues"/> — §12 block B's board view reads this to order one owner group
    /// per column, rather than hand-listing the five roles a second time. A fixed literal, not
    /// <c>ByWireValue.Values</c>, for the same "enumeration order is not a contract" reason
    /// <see cref="CardKindWireFormat.AllKinds"/> is one.</summary>
    internal static readonly IReadOnlyList<CardOwner> AllOwners =
    [
        CardOwner.Architect,
        CardOwner.Worker,
        CardOwner.Reviewer,
        CardOwner.Supervisor,
        CardOwner.ProductOwner,
    ];

    internal static bool TryParse(string value, out CardOwner owner)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // See CardKindWireFormat.TryParse: every stored value is non-null, so `match` is
        // non-null whenever `found` is true, and the fallback on failure is always discarded.
        owner = found ? match! : CardOwner.Architect;
        return found;
    }
}
