using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block B — the two-lock protocol's own concurrency behaviour. Reviewer blocker 1: two
/// concurrent <c>finding record</c> invocations naming the identical <c>--blind-spot-file</c> path
/// used to be able to interleave on that path's completely unlocked write, and the loser's own
/// rollback could then delete the winner's card — "B's finding permanently references a raised
/// card that does not exist", the reviewer's own reproduction.
/// <see cref="CardStore.RecordFinding"/> now takes a <see cref="CardLock"/> on the raised card's
/// own path too, not only the finding's. A later round (the reviewer's "cross-invocation lock
/// ordering" finding) found that acquiring the two locks in a deterministic path order was itself
/// unsafe — two invocations naming the same pair of physical files with different casing could
/// compute opposite orders and deadlock. <see cref="CardStore.AcquireLocksAndRecord"/> now uses no
/// ordering at all: it always acquires the finding's lock first (blocking), probes the raised
/// lock with no wait, and releases-and-retries on a miss rather than ever holding one lock while
/// blocked on the other — see that method's own doc comment for the full argument.
///
/// <para>
/// <b>14.5-remediation (§14 supervisor finding): two of this file's original fixtures are gone,
/// not merely rewritten.</b> Both forced two <c>RecordFinding</c> invocations to collide on a
/// caller-supplied raised-card path — the very door 14.5's remediation closes, since that path is
/// now always minted from a freshly-allocated, unique identity. See the two retirement notes below
/// for what each proved and where that coverage now lives.
/// </para>
/// </summary>
public sealed class CardFindingRecordConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string Section = "6";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-finding-record-concurrency-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // §6 block B remediation's own "two invocations sharing the same --blind-spot-file path"
    // reproduction — retired here (14.5-remediation, §14 supervisor finding), not merely deleted
    // without a trace: it forced two concurrent RecordFinding calls to name the identical raised
    // card path, which is exactly the CLI door this remediation closes. The raised path is now
    // minted from CardLayout.FileNameFor over a freshly-allocated, unique identity every call, so
    // two invocations can no longer be made to collide on it — there is no longer a caller input
    // that could reconstruct this fixture's premise. The still-live half of what this test proved —
    // a losing invocation's rollback must never destroy a winner's legitimately-written card — stays
    // covered at the single-invocation level by CardFindingRecordTests' own
    // RollbackRaisedCard_ContentDoesNotMatch_LeavesTheFileAlone (content-mismatch, never delete).

    /// <summary>
    /// §6 block B fifth remediation, reviewer's "cross-invocation lock ordering" finding, proved
    /// the way the reviewer proved it: deterministically, not by racing a narrow timing window (80
    /// of the reviewer's own trials produced zero natural hits). Plants a live lock on the raised
    /// card's own path — held by this test process itself, so <see cref="CardLock"/>'s staleness
    /// check correctly refuses to break it, the same "live PID" technique <c>CardLockTests</c> uses
    /// — then runs a real <see cref="CardStore.RecordFinding"/> call whose raised path is that same
    /// planted lock. While that call is necessarily still retrying (it cannot get past the planted
    /// lock yet), this test independently acquires the <em>finding's own</em> lock from outside the
    /// call and releases it again. That only succeeds if <see cref="CardStore.RecordFinding"/> is
    /// not currently holding it — proof, not inference, that the finding's lock is released between
    /// retries rather than held for the whole time the call is blocked on the raised lock. That is
    /// exactly the property that makes an AB/BA deadlock across two invocations impossible: a call
    /// can only be "holding X while blocked on Y" during the zero-wait probe itself, never for the
    /// full retry loop.
    ///
    /// The ordering this proves is made structural rather than temporal, after a prior version of
    /// this test staged the same proof as a wall-clock race against the call's own retry budget: a
    /// 400ms sleep intended to land the call mid-retry against a 3s budget, both wall-clock and
    /// only meaningful relative to each other. Under a contended full test run the sleep
    /// overshot — observed at ~3s on the two runs that caught it — by which point the call had
    /// legitimately exhausted its own budget and finished; the test's own precondition then failed
    /// over a call that had behaved correctly. Here the call's budget (30s) is long enough that no
    /// amount of scheduler slop can consume it before the probes below run, so "the call has not
    /// finished" holds by construction — the raised lock is still planted — rather than by arriving
    /// in time to observe it. The budget-exhaustion behaviour this test used to also prove is now
    /// its own race-free fact, below.
    ///
    /// A first attempt at removing the 400ms sleep replaced it with nothing — the reviewer caught
    /// that this opens a *different* race, in the opposite direction: <c>Task.Run</c> only enqueues
    /// the delegate, so on a loaded thread pool the calling thread can reach a single-shot probe
    /// before <c>recordTask</c> has ever touched <paramref name="findingPath"/>'s lock, in which
    /// case the probe acquires an uncontended lock and the test passes having exercised nothing —
    /// a false negative invisible to a green run, most likely on exactly the contended runs this
    /// remediation exists to survive. The fix below requires *positive* evidence of both states
    /// before it will pass: phase 1 polls with a zero-wait acquire until it actually observes the
    /// lock held (proof the call has reached its retry loop, not merely started), and only then
    /// does phase 2 run the load-bearing blocking probe. Neither phase can pass by never having
    /// coincided with the call at all.
    /// </summary>
    [Fact]
    public async Task WhenTheRaisedLockIsUnavailable_TheFindingLockIsReleasedBetweenRetries_NotHeldWhileWaiting()
    {
        var testRoot = Path.Combine(_root, "released-between-retries");
        var directory = Path.Combine(testRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        // 14.5-remediation: neither path is a caller's to name any more — both are
        // CardLayout.FileNameFor over the first Finding/Obligation identity a fresh counter in
        // this test's own testRoot ever mints ("F-0001"/"O-0001"), predictable ahead of the call
        // precisely because this is the only RecordFinding invocation this testRoot will ever see.
        var findingPath = Path.Combine(directory, CardLayout.FileNameFor("F-0001"));
        var raisedPath = Path.Combine(directory, CardLayout.FileNameFor("O-0001"));

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, "Blind spot title", "Blind spot content.");
        var recordTask = Task.Run(() => CardStore.RecordFinding(
            testRoot, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(30), ChangeName));

        // Phase 1 — positive proof the call has actually reached its retry loop, closing the false
        // negative above: poll a zero-wait acquire of the finding's lock until it is observed
        // held. Bounded well short of the call's 30s budget; if this window elapses without ever
        // observing a hold, the call was never actually caught inside its retry loop and phase 2
        // below would prove nothing, so that is a hard failure here rather than a silent pass.
        var observeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var observedHeld = false;
        while (DateTimeOffset.UtcNow < observeDeadline)
        {
            var attempt = CardLock.Acquire(findingPath, TimeSpan.Zero);
            var stillFree = attempt.Match(
                onAcquired: acquired =>
                {
                    acquired.Lock.Dispose();
                    return true;
                },
                onTimedOut: static _ => false);

            if (!stillFree)
            {
                observedHeld = true;
                break;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(
            observedHeld,
            "never observed the finding's lock held during the poll window — the call may not have " +
            "reached its retry loop yet, so this run cannot prove the release-between-retries property " +
            "(the false negative a single-shot probe risked).");

        // Phase 2 — the load-bearing assertion, now guaranteed to coincide with genuine contention:
        // a blocking acquire with a timeout well inside the call's 30s budget must still succeed.
        // If the mechanism releases-and-retries as designed, this succeeds quickly even though
        // phase 1 just proved the lock was held a moment ago; if it held the lock continuously for
        // the whole wait (the defect this proves is gone), this cannot succeed until the call's own
        // budget runs out — far past this probe's 5s timeout.
        var probeResult = CardLock.Acquire(findingPath, TimeSpan.FromSeconds(5));
        probeResult.Match<object?>(
            onAcquired: acquired =>
            {
                acquired.Lock.Dispose();
                return null;
            },
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException(
                "the finding's lock was observed held a moment ago but did not become acquirable again " +
                $"within 5s — looks like it is held continuously across the whole retry loop instead of " +
                $"released between retries. {timedOut.Message}"));

        // The raised lock is still planted at this point (we have not released it yet), so the
        // call cannot have made it past its own zero-wait probe of that lock — true by
        // construction, not by racing to observe it before it changes.
        Assert.False(recordTask.IsCompleted, "the call finished before the raised lock was ever released — it should still be retrying.");
        Assert.False(File.Exists(findingPath), "the finding was written while the raised card's lock was still held.");

        // Release the planted lock so the retrying call can make real progress and the test ends
        // promptly rather than waiting out the full 30s budget.
        plantedRaisedLock.Dispose();

        var outcome = await recordTask;
        var recorded = Assert.IsType<CardFindingRecordOutcome.Recorded>(outcome);
        Assert.NotNull(recorded.RaisedCard);
        Assert.True(File.Exists(findingPath));
    }

    /// <summary>
    /// The budget-exhaustion half, split off from the release-between-retries proof above so it
    /// carries no interleaving and therefore no wall-clock race: with the raised lock planted for
    /// the whole call, a real <see cref="CardStore.RecordFinding"/> call given a short budget must
    /// end in an honest tool-failure once that budget is exhausted — never a silent write through
    /// the planted lock, never a hang past its own declared timeout. This test has two events (the
    /// call starts, the call ends), not three, and so cannot lose an ordering it never depends on —
    /// unlike the test above, it does not need to observe the call mid-retry.
    /// </summary>
    [Fact]
    public async Task WhenTheRaisedLockRemainsUnavailable_TheCallEndsInAnHonestToolFailureAtItsOwnBudget()
    {
        var testRoot = Path.Combine(_root, "budget-exhaustion");
        var directory = Path.Combine(testRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        // 14.5-remediation: same predictable-first-identity reasoning as the sibling test above.
        var findingPath = Path.Combine(directory, CardLayout.FileNameFor("F-0001"));
        var raisedPath = Path.Combine(directory, CardLayout.FileNameFor("O-0001"));

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));
        try
        {
            var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, "Blind spot title", "Blind spot content.");
            var outcome = await Task.Run(() => CardStore.RecordFinding(
                testRoot, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromMilliseconds(500), ChangeName));

            var toolFailure = Assert.IsType<CardFindingRecordOutcome.ToolFailure>(outcome);
            Assert.Contains(raisedPath, toolFailure.Reason, StringComparison.Ordinal);
            Assert.False(File.Exists(findingPath), "the finding was written even though the raised card's lock was held throughout.");
        }
        finally
        {
            plantedRaisedLock.Dispose();
        }
    }

    /// <summary>
    /// The other half: once the planted lock above is released, the same shape succeeds — proving
    /// the release-and-retry loop makes real progress rather than only ever timing out cleanly.
    /// </summary>
    [Fact]
    public async Task WhenTheRaisedLockIsReleasedPartway_TheRetryingCallSucceeds()
    {
        var testRoot = Path.Combine(_root, "succeeds-after-release");
        var directory = Path.Combine(testRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        // 14.5-remediation: same predictable-first-identity reasoning as the two sibling tests above.
        var findingPath = Path.Combine(directory, CardLayout.FileNameFor("F-0001"));
        var raisedPath = Path.Combine(directory, CardLayout.FileNameFor("O-0001"));

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, "Blind spot title", "Blind spot content.");
        var recordTask = Task.Run(() => CardStore.RecordFinding(
            testRoot, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName));

        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.False(recordTask.IsCompleted, "the call finished before the planted lock was ever released.");
        plantedRaisedLock.Dispose();

        var outcome = await recordTask;
        var recorded = Assert.IsType<CardFindingRecordOutcome.Recorded>(outcome);
        Assert.NotNull(recorded.RaisedCard);
        Assert.True(File.Exists(findingPath));
        Assert.True(File.Exists(raisedPath));
    }

    // The reviewer's own "reversed case-variant pair, across two invocations" repro — retired here
    // (14.5-remediation, §14 supervisor finding), same reasoning as the shared-blind-spot-file
    // fixture retired above: it forced two invocations' finding/raised paths into a reversed
    // case-variant collision with each other, which required naming both paths as a caller. Both
    // paths are now minted from unique, freshly-allocated identities, so two invocations can no
    // longer be constructed to collide at all — there is nothing left for a "does this deadlock"
    // fixture to force. The property it protected — no lock ordering, so no AB/BA deadlock — is
    // still proved deterministically by WhenTheRaisedLockIsUnavailable_TheFindingLockIsReleased
    // BetweenRetries_NotHeldWhileWaiting above, which needs only a single invocation.

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match<CardLock>(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out instead: {timedOut.Message}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
