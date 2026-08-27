namespace Callboard.Cards;

/// <summary>
/// Closed union over the three shapes allocating a card identity can end in — same shape as
/// <see cref="CardWriteResult"/> and <see cref="CardLockResult"/> and for the same reason: a lock
/// timeout, an unverifiable counter, or a counter that has fallen behind the record is an expected
/// outcome the caller must handle, not an exception escaping past the caller's control.
///
/// <para>
/// <b><see cref="Borne"/> (work-lifecycle: "Every block card is minted by the tool"; card-model:
/// "the system SHALL refuse to issue an identity that a card in the record already bears").</b> The
/// counter stays the sole source of the <em>next</em> number — <see cref="CardIdentityAllocator"/>'s
/// own doc comment explains at length why deriving it from a scan would recycle the moment a
/// card-bearing directory moves out of the scanned tree — but before an allocation is ever handed
/// back, <see cref="CardIdentityAllocator.Allocate"/> confirms, against the record, that no card
/// already bears the number it just issued. A hand-authored card sharing the counter's next number
/// (the exact failure mode "an identity SHALL NOT be reused" existed to prevent, and could not
/// enforce, before this case existed) is reported here rather than handed out as if nothing were
/// wrong.
/// </para>
/// </summary>
internal abstract record CardIdentityAllocationResult
{
    private CardIdentityAllocationResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Allocated, TResult> onAllocated,
        Func<Failed, TResult> onFailed,
        Func<Borne, TResult> onBorne);

    internal sealed record Allocated(string Id) : CardIdentityAllocationResult
    {
        internal override TResult Match<TResult>(Func<Allocated, TResult> onAllocated, Func<Failed, TResult> onFailed, Func<Borne, TResult> onBorne) =>
            onAllocated(this);
    }

    internal sealed record Failed(string Reason) : CardIdentityAllocationResult
    {
        internal override TResult Match<TResult>(Func<Allocated, TResult> onAllocated, Func<Failed, TResult> onFailed, Func<Borne, TResult> onBorne) =>
            onFailed(this);
    }

    /// <param name="Id">The identity the counter issued, but which the record already shows in use.</param>
    /// <param name="CardFilePaths">Every file in the record already carrying <paramref name="Id"/>,
    /// ordered <see cref="StringComparer.Ordinal"/> — ordinarily one file (<see cref="
    /// CardIdentityResolution.Found"/>), occasionally more than one when the record itself already
    /// carries a duplicate (<see cref="CardIdentityResolution.Duplicate"/>).</param>
    internal sealed record Borne(string Id, IReadOnlyList<string> CardFilePaths) : CardIdentityAllocationResult
    {
        internal override TResult Match<TResult>(Func<Allocated, TResult> onAllocated, Func<Failed, TResult> onFailed, Func<Borne, TResult> onBorne) =>
            onBorne(this);
    }
}
