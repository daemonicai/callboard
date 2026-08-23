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
            // interesting question each round is testing.
            File.WriteAllText(findingPathA, "not a card");

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
                    instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequestA, Recorded, TimeSpan.FromSeconds(20), ChangeName);
            });
            tasksB[i] = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return CardStore.RecordFinding(
                    roundRoot, findingPathB, "B's finding", CardOwner.Worker, Section, "Body B.",
                    instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequestB, Recorded, TimeSpan.FromSeconds(20), ChangeName);
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
        try
        {
            var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raisedPath, "Blind spot title", "Blind spot content.");
            var recordTask = Task.Run(() => CardStore.RecordFinding(
                testRoot, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body.",
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, Recorded, TimeSpan.FromSeconds(3), ChangeName));

            // Give the call time to acquire the finding's lock at least once, fail its zero-wait
            // probe of the (still planted) raised lock, and release — i.e. to be genuinely inside
            // its retry loop, not merely about to start.
            await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
            Assert.False(recordTask.IsCompleted, "the call finished before the probe below could run — the planted lock was not actually blocking it.");

            // The load-bearing assertion: acquirable from outside the call while the call itself is
            // still retrying. A single zero-wait attempt can legitimately land during one of the
            // call's own brief, repeated holds of this same lock (it re-acquires every retry, if
            // only for the instant it takes to probe the raised lock and fail) — so this uses a
            // short *blocking* probe instead: if the mechanism releases-and-retries as designed,
            // that succeeds almost immediately (well inside one retry interval); if it held the
            // lock continuously for the whole wait (the defect this proves is gone), it would not
            // succeed until the call's own 3s budget is exhausted.
            var probeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var probeResult = CardLock.Acquire(findingPath, TimeSpan.FromMilliseconds(500));
            probeStopwatch.Stop();
            var probeLock = AssertAcquired(probeResult);
            probeLock.Dispose();
            Assert.True(
                probeStopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
                $"took {probeStopwatch.Elapsed} to acquire the finding's lock from outside the call — looks like it was held continuously.");

            // Let the call finish out its own retry budget, holding the planted lock the whole
            // time — it must end in an honest tool-failure, never a silent write through the
            // planted lock and never a hang past its own declared timeout.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await recordTask;
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"took {stopwatch.Elapsed} — longer than the call's own 3s budget plus slack.");
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
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, Recorded, TimeSpan.FromSeconds(5), ChangeName));

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
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest1, Recorded, perCallTimeout, ChangeName)));
            tasks.Add(Task.Run(() => CardStore.RecordFinding(
                roundRoot, finding2, "Finding 2", CardOwner.Worker, Section, "Body 2.",
                instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest2, Recorded, perCallTimeout, ChangeName)));
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
