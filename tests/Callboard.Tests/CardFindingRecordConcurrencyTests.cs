using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block B — the two-lock protocol's own concurrency behaviour, across three remediation
/// rounds. Reviewer blocker 1: two concurrent <c>finding record</c> invocations naming the
/// identical <c>--blind-spot-file</c> path used to be able to interleave on that path's completely
/// unlocked write, and the loser's own rollback could then delete the winner's card — "B's finding
/// permanently references a raised card that does not exist", the reviewer's own reproduction.
/// <see cref="CardStore.RecordFinding"/> now takes a <see cref="CardLock"/> on the raised card's
/// own path too, not only the finding's. A later round (the reviewer's "cross-invocation lock
/// ordering" finding) found that acquiring the two locks in a deterministic path order was itself
/// unsafe — two invocations naming the same pair of physical files with different casing could
/// compute opposite orders and deadlock. <see cref="CardStore.AcquireLocksAndRecord"/> now uses no
/// ordering at all: it always acquires the finding's lock first (blocking), probes the raised
/// lock with no wait, and releases-and-retries on a miss rather than ever holding one lock while
/// blocked on the other — see that method's own doc comment for the full argument.
/// </summary>
public sealed class CardFindingRecordConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string Section = "6";
    private const int Rounds = 60;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-finding-record-concurrency-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Reproduces the reviewer's exact shape, at real concurrency: <see cref="Rounds"/> independent
    /// round-pairs (fresh directory each round; A's own finding path pre-occupied so A's finding
    /// write always fails and always triggers rollback of whatever it wrote to that round's shared
    /// raised-card path; B's finding path is fresh) are all launched at once — every round's A and
    /// every round's B racing across the whole thread pool simultaneously, not two-at-a-time — to
    /// give the same tight interleaving the reviewer's own "batches of 20 concurrent trials"
    /// reproduction relied on a real chance to land. Under the old, unlocked implementation this
    /// reliably reproduced B's card being silently overwritten and then deleted by A's rollback;
    /// every round here asserts the invariant that failure broke: B — the only one of the two that
    /// can ever legitimately succeed, since A's own finding path is always occupied — is never left
    /// referencing a raised card that does not exist on disk, and never has a raised card it
    /// legitimately wrote quietly destroyed by another invocation.
    /// </summary>
    [Fact]
    public async Task TwoInvocations_SharingTheSameBlindSpotFilePath_NeverLoseOrCorruptEitherCard()
    {
        // The startBarrier below needs Rounds*2 threads genuinely running at once; the CLR's
        // default thread-pool growth throttles new-thread injection to roughly one every half
        // second once the pool is saturated, which would otherwise make this test stall for many
        // seconds (or, on a constrained sandbox, risk the barrier never completing within the test
        // timeout) waiting for threads that only trickle in. Raising the minimum makes them
        // available immediately.
        ThreadPool.GetMinThreads(out var previousWorkerThreads, out var previousCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(previousWorkerThreads, Rounds * 2 + 4), previousCompletionPortThreads);
        try
        {
            await RunAsync();
        }
        finally
        {
            ThreadPool.SetMinThreads(previousWorkerThreads, previousCompletionPortThreads);
        }
    }

    private async Task RunAsync()
    {
        var rounds = new (string RoundRoot, string FindingPathA, string FindingPathB, string SharedRaisedPath)[Rounds];

        for (var i = 0; i < Rounds; i++)
        {
            var roundRoot = Path.Combine(_root, $"round-{i}");
            var directory = Path.Combine(roundRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
            var registerDirectory = Path.Combine(roundRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(registerDirectory);

            var findingPathA = Path.Combine(directory, "f-a.md");
            var findingPathB = Path.Combine(directory, "f-b.md");
            var sharedRaisedPath = Path.Combine(registerDirectory, "h-shared.md");

            // A's own finding path is pre-occupied — A's finding write can never succeed, in either
            // ordering, which is what makes "did A's rollback destroy B's card?" the only
            // interesting question each round is testing. A readable, unrelated card, not garbage
            // text (§13: the identity allocator now confirms, against the whole record, that the
            // id it is about to issue is not already borne; an unparseable file anywhere in the
            // record reports Unreadable rather than "confirmed unclaimed", failing every
            // allocation in this round outright rather than the AlreadyExists this round targets).
            var unrelatedFrontmatter = new CardFrontmatter(
                "F-9999", CardKind.Finding, "Unrelated", "open", CardOwner.Architect, CardScope.Change, Section, Recorded, Recorded);
            File.WriteAllText(findingPathA, CardFileWriter.Serialize(new CardFile(unrelatedFrontmatter, "Unrelated.", [], [])));

            rounds[i] = (roundRoot, findingPathA, findingPathB, sharedRaisedPath);
        }

        var tasksA = new Task<CardFindingRecordOutcome>[Rounds];
        var tasksB = new Task<CardFindingRecordOutcome>[Rounds];

        // Every one of the Rounds*2 tasks below rendezvous on this barrier immediately after it
        // starts running and before it calls RecordFinding — nobody's RecordFinding call actually
        // begins until every task has reached that point, so the whole batch (all 150 A/B pairs)
        // genuinely starts at the same instant instead of trickling out as the thread pool happens
        // to schedule Task.Run calls. This is what turns "some interleaving, eventually, maybe" into
        // real, synchronised contention on every round's shared path at once.
        using var startBarrier = new Barrier(Rounds * 2);

        for (var i = 0; i < Rounds; i++)
        {
            var (roundRoot, findingPathA, findingPathB, sharedRaisedPath) = rounds[i];
            var raiseRequestA = new FindingBlindSpotRaiseRequest(CardKind.Hazard, sharedRaisedPath, "Blind spot (A)", "A's blind spot content.");
            var raiseRequestB = new FindingBlindSpotRaiseRequest(CardKind.Hazard, sharedRaisedPath, "Blind spot (B)", "B's blind spot content.");

            tasksA[i] = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return CardStore.RecordFinding(
                    roundRoot, findingPathA, "A's finding", CardOwner.Worker, Section, "Body A.",
                    instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequestA, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(20), ChangeName);
            });
            tasksB[i] = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return CardStore.RecordFinding(
                    roundRoot, findingPathB, "B's finding", CardOwner.Worker, Section, "Body B.",
                    instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequestB, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(20), ChangeName);
            });
        }

        // Every round's A and every round's B, across the whole batch, actually contend for
        // thread-pool time together here — not two tasks at a time, Rounds*2 of them at once.
        var resultsA = await Task.WhenAll(tasksA);
        var resultsB = await Task.WhenAll(tasksB);

        for (var i = 0; i < Rounds; i++)
        {
            var (_, _, _, sharedRaisedPath) = rounds[i];
            var outcomeA = resultsA[i];
            var outcomeB = resultsB[i];
            var round = i;

            // A always ends refused — by its own occupied finding path, or (if B fully finished
            // first) by the shared raised-card path already belonging to B. Never anything else:
            // never a tool-failure, never a silent success that also wrote a card.
            outcomeA.Match<object?>(
                onRecorded: recorded => throw new Xunit.Sdk.XunitException(
                    $"round {round}: A unexpectedly succeeded — its own finding path was pre-occupied. Raised card: {recorded.RaisedCard?.Frontmatter.Id}"),
                onFindingAlreadyExists: static _ => null,
                onBlindSpotCardAlreadyExists: static _ => null,
                onFindingLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"round {round}: A got FindingLayoutMismatch: {mismatch.Reason}"),
                onBlindSpotLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"round {round}: A got BlindSpotLayoutMismatch: {mismatch.Reason}"),
                onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"round {round}: A got ToolFailure: {toolFailure.Reason}"));

            // B always succeeds — its finding path was never contended, and whichever ordering the
            // two invocations actually ran in, B is the one legitimate owner of the shared raised
            // path by the time both have finished.
            var recordedB = outcomeB.Match(
                onRecorded: static recorded => recorded,
                onFindingAlreadyExists: already => throw new Xunit.Sdk.XunitException($"round {round}: B unexpectedly got FindingAlreadyExists('{already.FilePath}')"),
                onBlindSpotCardAlreadyExists: already => throw new Xunit.Sdk.XunitException($"round {round}: B unexpectedly got BlindSpotCardAlreadyExists('{already.FilePath}')"),
                onFindingLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"round {round}: B got FindingLayoutMismatch: {mismatch.Reason}"),
                onBlindSpotLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"round {round}: B got BlindSpotLayoutMismatch: {mismatch.Reason}"),
                onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"round {round}: B got ToolFailure: {toolFailure.Reason}"));

            Assert.NotNull(recordedB.RaisedCard);
            var raisedIdB = recordedB.RaisedCard!.Frontmatter.Id;

            // The load-bearing assertion: the raised card B's finding declares is the raised card
            // actually sitting on disk — never a reference to something A's rollback destroyed, and
            // never a card A's write silently overwrote.
            Assert.True(File.Exists(sharedRaisedPath), $"round {round}: the shared raised-card path does not exist after both invocations completed.");
            var onDisk = AssertParseSuccess(CardStore.ReadCard(sharedRaisedPath));
            Assert.Equal(raisedIdB, onDisk.Frontmatter.Id);
            Assert.Contains(recordedB.Finding.Frontmatter.Id, onDisk.Body, StringComparison.Ordinal);
            Assert.Contains("B's blind spot content.", onDisk.Body, StringComparison.Ordinal);

            var declaredByFinding = recordedB.Finding.FindingFields.BlindSpot.Match(
                onNone: static () => (string?)null,
                onRaisedAs: id => id);
            Assert.Equal(raisedIdB, declaredByFinding);
        }
    }

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

        var findingPath = Path.Combine(directory, "f-retry.md");
        var raisedPath = Path.Combine(directory, "h-retry.md");

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raisedPath, "Blind spot title", "Blind spot content.");
        var recordTask = Task.Run(() => CardStore.RecordFinding(
            testRoot, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
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

        var findingPath = Path.Combine(directory, "f-retry.md");
        var raisedPath = Path.Combine(directory, "h-retry.md");

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));
        try
        {
            var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raisedPath, "Blind spot title", "Blind spot content.");
            var outcome = await Task.Run(() => CardStore.RecordFinding(
                testRoot, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
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

        var findingPath = Path.Combine(directory, "f-retry-success.md");
        var raisedPath = Path.Combine(directory, "h-retry-success.md");

        var plantedRaisedLock = AssertAcquired(CardLock.Acquire(raisedPath, TimeSpan.FromSeconds(30)));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raisedPath, "Blind spot title", "Blind spot content.");
        var recordTask = Task.Run(() => CardStore.RecordFinding(
            testRoot, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
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

    /// <summary>
    /// The reviewer's exact repro shape, run for real: two invocations naming the same pair of
    /// physical files with reversed case variants — the construction that made the old ordinal
    /// ordering disagree about which lock to acquire first. Best-effort, real-concurrency
    /// confirmation on top of the deterministic proof above (real process/thread timing means this
    /// cannot force the exact simultaneous-first-lock interleaving the way the deterministic tests
    /// do — the same coarse-granularity limit the reviewer's own 80-trial attempt hit), asserting
    /// the property that actually matters: every round completes quickly, never anywhere near
    /// double the per-call lock timeout, which is what an AB/BA deadlock bounded only by two
    /// independent timeouts would look like.
    /// </summary>
    [Fact]
    public async Task ReversedCaseVariantPair_AcrossTwoInvocations_NeverDeadlocks()
    {
        const int reversedRounds = 20;
        var perCallTimeout = TimeSpan.FromSeconds(3);
        var tasks = new List<Task<CardFindingRecordOutcome>>(reversedRounds * 2);

        for (var i = 0; i < reversedRounds; i++)
        {
            var roundRoot = Path.Combine(_root, $"reversed-{i}");
            var directory = Path.Combine(roundRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            // Invocation 1: finding lower-case, raised upper-case. Invocation 2: finding
            // upper-case, raised lower-case — the exact reversed-role, case-variant construction
            // the reviewer used to break the old ordinal ordering.
            var finding1 = Path.Combine(directory, $"aaa{i}.md");
            var raised1 = Path.Combine(directory, $"ZZZ{i}.MD");
            var finding2 = Path.Combine(directory, $"AAA{i}.MD");
            var raised2 = Path.Combine(directory, $"zzz{i}.md");

            var raiseRequest1 = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raised1, "Blind spot (1)", "Content 1.");
            var raiseRequest2 = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raised2, "Blind spot (2)", "Content 2.");

            tasks.Add(Task.Run(() => CardStore.RecordFinding(
                roundRoot, finding1, "Finding 1", CardOwner.Worker, Section, "Body 1.",
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest1, FindingDisposition.Measured, Recorded, perCallTimeout, ChangeName)));
            tasks.Add(Task.Run(() => CardStore.RecordFinding(
                roundRoot, finding2, "Finding 2", CardOwner.Worker, Section, "Body 2.",
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest2, FindingDisposition.Measured, Recorded, perCallTimeout, ChangeName)));
        }

        var overall = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = await Task.WhenAll(tasks);
        overall.Stop();

        // A genuine AB/BA deadlock resolved only by both sides' own timeouts would take roughly
        // perCallTimeout for every colliding pair — but since every round runs concurrently with
        // every other round, the bound that actually distinguishes "no deadlock" is against a
        // single per-call timeout with slack, not the number of rounds.
        Assert.True(
            overall.Elapsed < perCallTimeout + TimeSpan.FromSeconds(2),
            $"took {overall.Elapsed} across {reversedRounds} rounds — looks like an AB/BA deadlock reproduced.");

        foreach (var outcome in outcomes)
        {
            outcome.Match<object?>(
                onRecorded: static _ => null,
                onFindingAlreadyExists: static _ => null,
                onBlindSpotCardAlreadyExists: static _ => null,
                onFindingLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"unexpected FindingLayoutMismatch: {mismatch.Reason}"),
                onBlindSpotLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"unexpected BlindSpotLayoutMismatch: {mismatch.Reason}"),
                onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"unexpected ToolFailure — looks like a deadlock: {toolFailure.Reason}"));
        }
    }

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match<CardLock>(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out instead: {timedOut.Message}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
