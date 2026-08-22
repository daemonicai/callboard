namespace Callboard.Cards;

/// <summary>
/// Closed union over the two shapes allocating a card identity can end in — same shape as
/// <see cref="CardWriteResult"/> and <see cref="CardLockResult"/> and for the same reason: a lock
/// timeout or an unverifiable counter is an expected outcome the caller must handle, not an
/// exception escaping past the caller's control. No verb wires this to a
/// <see cref="Callboard.Cli.CliRefusal"/> in this block — 4.2 allocates no card from a CLI verb —
/// so this stays library-internal until a future section's verb converts a <see cref="Failed"/> at
/// the CLI boundary.
/// </summary>
internal abstract record CardIdentityAllocationResult
{
    private CardIdentityAllocationResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Allocated, TResult> onAllocated,
        Func<Failed, TResult> onFailed);

    internal sealed record Allocated(string Id) : CardIdentityAllocationResult
    {
        internal override TResult Match<TResult>(Func<Allocated, TResult> onAllocated, Func<Failed, TResult> onFailed) =>
            onAllocated(this);
    }

    internal sealed record Failed(string Reason) : CardIdentityAllocationResult
    {
        internal override TResult Match<TResult>(Func<Allocated, TResult> onAllocated, Func<Failed, TResult> onFailed) =>
            onFailed(this);
    }
}
