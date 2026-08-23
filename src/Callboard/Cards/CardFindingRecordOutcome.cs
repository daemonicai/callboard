namespace Callboard.Cards;

/// <summary>
/// Closed union over the shapes <see cref="CardStore.RecordFinding"/> can end in — same shape and
/// same reason as <see cref="CardWriteResult"/> and <see cref="CardBlockTransitionOutcome"/>: a
/// refusal (caller-correctable) and a tool-failure (enforcement unavailable) carry opposite
/// instructions to the caller, so they cannot share a case. Every case here is refusal-shaped except
/// <see cref="ToolFailure"/> — a lock timeout, an I/O error, or an allocator that could not confirm
/// the identity it just wrote, none of which the caller can fix by supplying different arguments.
/// </summary>
internal abstract record CardFindingRecordOutcome
{
    private CardFindingRecordOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recorded, TResult> onRecorded,
        Func<FindingAlreadyExists, TResult> onFindingAlreadyExists,
        Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists,
        Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch,
        Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Finding">The finding card as written — including the blind-spot declaration
    /// actually recorded (<see cref="FindingBlindSpotDeclaration.None"/> or a
    /// <see cref="FindingBlindSpotDeclaration.RaisedAs"/> naming <paramref name="RaisedCard"/>'s own
    /// id).</param>
    /// <param name="RaisedCard">The obligation or hazard card raised alongside the finding, or
    /// <see langword="null"/> when the blind spot was declared <see cref="FindingBlindSpotDeclaration.
    /// None"/> and nothing was raised.</param>
    internal sealed record Recorded(CardFile Finding, CardFile? RaisedCard) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>A card already exists at the finding's target path. Refusal-shaped: nothing was
    /// written, including no raised card — see <see cref="CardStore.RecordFinding"/>'s own doc
    /// comment for the all-or-nothing ordering that guarantees this.</summary>
    internal sealed record FindingAlreadyExists(string FilePath) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onFindingAlreadyExists(this);
    }

    /// <summary>A card already exists at the raised card's own target path. Refusal-shaped: the
    /// finding is never written in this case either.</summary>
    internal sealed record BlindSpotCardAlreadyExists(string FilePath) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onBlindSpotCardAlreadyExists(this);
    }

    /// <summary>The finding's target path does not resolve under the repository root and its
    /// (fixed) <see cref="CardScope.Section"/> scope, or the required change name was missing or
    /// invalid. Raised after the raised card, if any, has already been written and rolled back —
    /// see <see cref="CardStore.RecordFinding"/>'s own doc comment.</summary>
    internal sealed record FindingLayoutMismatch(string Reason) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onFindingLayoutMismatch(this);
    }

    /// <summary>The raised card's own target path does not resolve under its (fixed) scope — change
    /// for an obligation, repository for a hazard — or the required change name was missing or
    /// invalid. Nothing was written in this case: this check runs before the raised card's own
    /// write.</summary>
    internal sealed record BlindSpotLayoutMismatch(string Reason) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onBlindSpotLayoutMismatch(this);
    }

    /// <summary>Enforcement itself is unavailable — an identity could not be allocated (or
    /// confirmed), the finding's lock could not be acquired, or an I/O error occurred while writing
    /// either card. Tool-failure-shaped, never a refusal: the board is not saying no.</summary>
    internal sealed record ToolFailure(string Reason) : CardFindingRecordOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<FindingAlreadyExists, TResult> onFindingAlreadyExists, Func<BlindSpotCardAlreadyExists, TResult> onBlindSpotCardAlreadyExists, Func<FindingLayoutMismatch, TResult> onFindingLayoutMismatch, Func<BlindSpotLayoutMismatch, TResult> onBlindSpotLayoutMismatch, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
