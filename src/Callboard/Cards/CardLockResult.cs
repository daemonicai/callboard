namespace Callboard.Cards;

/// <summary>
/// Closed union over the two shapes acquiring a <see cref="CardLock"/> can end in. Same shape as
/// <see cref="CardFileParseResult"/> — a timeout is an expected, returned outcome (ADR-0003's own
/// consequence calls for "a timeout and a clear failure"), never a thrown exception.
/// </summary>
internal abstract record CardLockResult
{
    private CardLockResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Acquired, TResult> onAcquired,
        Func<TimedOut, TResult> onTimedOut);

    internal sealed record Acquired(CardLock Lock) : CardLockResult
    {
        internal override TResult Match<TResult>(Func<Acquired, TResult> onAcquired, Func<TimedOut, TResult> onTimedOut) =>
            onAcquired(this);
    }

    /// <param name="CardPath">The card file the lock guards — named explicitly so a caller can
    /// build a failure message that names the card, per ADR-0003's own wording.</param>
    /// <param name="Message">A message that names both the card and, where it could be
    /// determined, the lock's current holder.</param>
    internal sealed record TimedOut(string CardPath, string Message) : CardLockResult
    {
        internal override TResult Match<TResult>(Func<Acquired, TResult> onAcquired, Func<TimedOut, TResult> onTimedOut) =>
            onTimedOut(this);
    }
}
