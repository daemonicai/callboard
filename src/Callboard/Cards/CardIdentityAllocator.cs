using System.Globalization;
using System.Text;

namespace Callboard.Cards;

/// <summary>
/// Allocates a card's stable, kind-prefixed identity (card-model: "Stable, human-quotable,
/// kind-prefixed identity") from a per-kind, committed, human-legible high-water counter file in
/// the record — never from the derived index (design.md D4 / ADR-0004: the index is never
/// authoritative). Rejected: deriving the next number by scanning existing filenames on disk
/// alone, which would silently let identities recycle the moment an archive directory (or any
/// other card-bearing directory) is moved out of the tree the scan covers — exactly what "an
/// identity SHALL NOT be reused" forbids. The counter file is a second, independent statement of a
/// fact the filenames also carry; <see cref="VerifyCounters"/> is the reconciliation between the
/// two, run at <c>index rebuild</c> time.
///
/// <para>
/// <b>Never recycled, concretely:</b> the counter only ever increases — closing, discharging or
/// withdrawing a card has no code path back to this type, and nothing here derives "the next
/// number" from a count of existing cards, which would fall the moment a card is removed from
/// view (closed, archived, or simply deleted by a human). Allocation takes the same
/// <see cref="CardLock"/> the record's card writes do, keyed on the counter file's own path so
/// contention over one kind's counter never blocks another kind's, and — the discipline §2 and §3
/// both earned the hard way (see <c>CardLock</c>'s own doc comment) — re-reads and confirms the
/// counter's on-disk content immediately after writing it, before ever handing the identity back
/// to a caller, rather than trusting the write call's own success return.
/// </para>
/// </summary>
internal static class CardIdentityAllocator
{
    /// <summary>
    /// Allocates the next identity for <paramref name="kind"/> under <paramref name="cardsRoot"/>.
    /// A corrupt or unreadable counter file is never guessed at as "start over from zero" — that
    /// is precisely the recycling this type exists to prevent — so it is reported as
    /// <see cref="CardIdentityAllocationResult.Failed"/> rather than silently defaulted.
    /// </summary>
    internal static CardIdentityAllocationResult Allocate(string cardsRoot, CardKind kind, TimeSpan lockTimeout)
    {
        var counterPath = CounterPath(cardsRoot, kind);
        var directory = Path.GetDirectoryName(counterPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new CardIdentityAllocationResult.Failed($"'{counterPath}' has no containing directory to write into.");
        }

        Directory.CreateDirectory(directory);

        var lockResult = CardLock.Acquire(counterPath, lockTimeout);
        return lockResult.Match(
            onAcquired: acquired =>
            {
                using (acquired.Lock)
                {
                    return AllocateUnderLock(counterPath, kind);
                }
            },
            onTimedOut: timedOut => new CardIdentityAllocationResult.Failed(timedOut.Message));
    }

    /// <summary>
    /// Compares each kind's committed counter against the highest identity number
    /// <paramref name="observedMaxIdByKind"/> reports actually seeing on disk for that kind (built
    /// by the caller from a full read of the record, e.g. <c>index rebuild</c>). A counter below
    /// its kind's observed max means the next allocation for that kind could collide with — and
    /// so recycle — an identity that already exists. Per the block A brief, this is reported, not
    /// refused: it is neither a refusal nor a tool-failure, and mints no refusal code — a
    /// reported failure inside what is otherwise a successful rebuild, the same category
    /// record-retrieval already requires for a corrupt card.
    /// </summary>
    internal static IReadOnlyList<CardIdentityCounterViolation> VerifyCounters(
        string cardsRoot,
        IReadOnlyDictionary<CardKind, int> observedMaxIdByKind)
    {
        var violations = new List<CardIdentityCounterViolation>();

        foreach (var (kind, observedMax) in observedMaxIdByKind)
        {
            var counterPath = CounterPath(cardsRoot, kind);

            if (!TryReadCounter(counterPath, out var counterValue, out var readFailure))
            {
                violations.Add(new CardIdentityCounterViolation(kind, counterValue, observedMax, readFailure!));
                continue;
            }

            if (counterValue < observedMax)
            {
                violations.Add(new CardIdentityCounterViolation(
                    kind,
                    counterValue,
                    observedMax,
                    $"the '{kind.ToWireString()}' identity counter at '{counterPath}' reads " +
                    $"{counterValue.ToString(CultureInfo.InvariantCulture)}, but a '{kind.ToWireString()}' card with " +
                    $"identity number {observedMax.ToString(CultureInfo.InvariantCulture)} exists on disk; the next " +
                    "allocation for this kind could recycle an identity already in use."));
            }
        }

        return violations;
    }

    /// <summary>
    /// Extracts the trailing number from <paramref name="id"/> when it carries the prefix
    /// <paramref name="kind"/> allocates with (e.g. <c>42</c> from <c>B-0042</c> for
    /// <see cref="CardKind.Block"/>). A card whose recorded <c>id</c> does not match its own
    /// <c>kind</c>'s prefix — hand-edited, or from a future format — yields <see langword="false"/>
    /// rather than a wrong number, so a caller building <paramref name="observedMaxIdByKind"/> for
    /// <see cref="VerifyCounters"/> simply omits it from that kind's observed maximum.
    /// </summary>
    internal static bool TryParseIdentityNumber(CardKind kind, string id, out int number)
    {
        var prefix = kind.PrefixFor() + "-";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            number = 0;
            return false;
        }

        return int.TryParse(
            id.AsSpan(prefix.Length),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static CardIdentityAllocationResult AllocateUnderLock(string counterPath, CardKind kind)
    {
        if (!TryReadCounter(counterPath, out var current, out var readFailure))
        {
            return new CardIdentityAllocationResult.Failed(readFailure!);
        }

        var next = current + 1;

        var writeFailure = TryWriteCounter(counterPath, next);
        if (writeFailure is not null)
        {
            return new CardIdentityAllocationResult.Failed(writeFailure);
        }

        // Verify before acting on the effect (§2/§3's working rule): re-read what is now on disk
        // and confirm it is the value this call just wrote, rather than trusting the write call's
        // own success return. This call holds the counter's CardLock exclusively for its whole
        // duration, so a mismatch here is not a lost race the way it would be for the lock file
        // itself — it means the on-disk counter was altered by something outside this allocator
        // while the lock was held, and is reported as a failure rather than silently trusted.
        if (!TryReadCounter(counterPath, out var confirmed, out var confirmReadFailure))
        {
            return new CardIdentityAllocationResult.Failed(confirmReadFailure!);
        }

        if (confirmed != next)
        {
            return new CardIdentityAllocationResult.Failed(
                $"allocation for '{kind.ToWireString()}' could not be verified: expected counter " +
                $"{next.ToString(CultureInfo.InvariantCulture)}, found {confirmed.ToString(CultureInfo.InvariantCulture)} " +
                $"at '{counterPath}'.");
        }

        return new CardIdentityAllocationResult.Allocated(FormatIdentity(kind, next));
    }

    /// <summary>
    /// Zero-padded to at least 4 digits (<c>B-0042</c>), and the padding never caps the range —
    /// card 10000 reads <c>B-10000</c>, nine characters wide, never wrapped or truncated: "D4" is a
    /// minimum field width, not a fixed one.
    /// </summary>
    private static string FormatIdentity(CardKind kind, int number) =>
        $"{kind.PrefixFor()}-{number.ToString("D4", CultureInfo.InvariantCulture)}";

    private static string CounterPath(string cardsRoot, CardKind kind) =>
        Path.Combine(cardsRoot, CardLayout.IdentityCounterPath(kind).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A missing counter file reads as 0 — the ordinary state for a kind that has never allocated
    /// an identity. A <em>present but unparseable</em> file is never treated the same way: that
    /// would silently restart numbering from zero for a kind whose counter genuinely exists,
    /// which is the recycling this whole type exists to prevent, so it is reported as a failure
    /// instead.
    /// </summary>
    private static bool TryReadCounter(string counterPath, out int value, out string? failure)
    {
        if (!File.Exists(counterPath))
        {
            value = 0;
            failure = null;
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(counterPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            value = 0;
            failure = $"could not read counter '{counterPath}': {ex.Message}";
            return false;
        }

        var trimmed = text.Trim();
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            failure = $"counter '{counterPath}' is corrupt: '{trimmed}' is not a non-negative integer; " +
                "refusing to guess a starting point that could recycle an identity.";
            return false;
        }

        failure = null;
        return true;
    }

    private static string? TryWriteCounter(string counterPath, int value)
    {
        var directory = Path.GetDirectoryName(counterPath);
        if (string.IsNullOrEmpty(directory))
        {
            return $"'{counterPath}' has no containing directory to write into.";
        }

        Directory.CreateDirectory(directory);

        // Beside the target, on the same filesystem, never the system temp directory — a rename
        // across filesystems degrades to a copy and stops being atomic (ADR-0003 / D7), the same
        // discipline CardStore.AtomicWrite applies to card files. This type does not call
        // CardStore itself: the counter file is not a card (block A's brief), so it carries its
        // own copy of the same atomic-write shape rather than borrowing a caller it is not one of.
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(counterPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(value.ToString(CultureInfo.InvariantCulture));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, counterPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not write counter '{counterPath}': {ex.Message}";
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
