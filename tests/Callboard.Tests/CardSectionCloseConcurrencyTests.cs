using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §8a block A remediation (reviewer nit 1, architect disposition: <c>fix-before-land</c>) — real
/// concurrency for <see cref="CardStore.CloseSectionUnderExistingLock"/>'s N-lock acquisition, the
/// same precedent <see cref="CardFindingRecordConcurrencyTests"/> already set for <see
/// cref="CardStore.RecordFinding"/>'s two-card write. Section close acquires more locks than any
/// other verb in this codebase — the section's own (already held by the outer <see
/// cref="CardStore.CloseSection"/>) plus every block it owns, blocking, in the ordinal order <see
/// cref="CardStore.ReadAllCards"/> returns them in — and until this file, the all-or-none guarantee
/// and the timeout-releases-everything path were proven only against a quiet tree.
/// </summary>
public sealed class CardSectionCloseConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string CurrentState = "current-state";
    private const int Rounds = 24;
    private const int BlocksPerSection = 3;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-close-concurrency-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The load-bearing proof behind the reviewer's nit: three blocks in one section, ordinal
    /// order <c>b-1</c> &lt; <c>b-2</c> &lt; <c>b-3</c>. <c>b-2</c>'s own lock is planted externally
    /// (held by this test process, so <see cref="CardLock"/>'s own liveness check correctly refuses
    /// to break it — the same "live PID" technique <see cref="CardFindingRecordConcurrencyTests"/>
    /// uses) before <see cref="CardStore.CloseSection"/> ever runs. Acquisition proceeds in ordinal
    /// order, so the call successfully takes <c>b-1</c>'s lock, then blocks on <c>b-2</c>'s for the
    /// whole of a short <c>lockTimeout</c> and times out — <c>b-3</c>'s lock is never even
    /// attempted. The properties this proves, none of them visible from a quiet-tree test: the
    /// <c>finally</c> releases <c>b-1</c>'s lock even though the call never reached its own decide-
    /// and-write phase; nothing was written to any of the three blocks or the section itself
    /// (the all-or-none guarantee holds even when the failure is a lock timeout, not a validation
    /// refusal); and a retried close — after the plant is released — succeeds and lands every
    /// block, proving the timed-out attempt left the record in a state a retry can still resolve.
    /// </summary>
    [Fact]
    public void CloseSection_WhenAMidOrdinalBlockLockIsHeldElsewhere_TimesOut_ReleasesEveryOtherLock_WritesNothing()
    {
        var testRoot = Path.Combine(_root, "timeout-releases");
        var directory = SetUpDirectory(testRoot);

        var sectionPath = WriteSectionCard(directory, "s-0001", "S-0001");
        var block1Path = WriteApprovedBlockCard(directory, "b-1", "B-0001", "S-0001");
        var block2Path = WriteApprovedBlockCard(directory, "b-2", "B-0002", "S-0001");
        var block3Path = WriteApprovedBlockCard(directory, "b-3", "B-0003", "S-0001");

        var sectionBytesBefore = File.ReadAllText(sectionPath);
        var block1BytesBefore = File.ReadAllText(block1Path);
        var block3BytesBefore = File.ReadAllText(block3Path);

        var plantedBlock2Lock = AssertAcquired(CardLock.Acquire(block2Path, TimeSpan.FromSeconds(30)));
        try
        {
            var outcome = CardStore.CloseSection(
                testRoot, sectionPath, CardOwner.Architect, Created, TimeSpan.FromMilliseconds(400), ChangeName);

            Assert.IsType<CardSectionCloseOutcome.ToolFailure>(outcome);

            // Nothing written by the timed-out attempt: section still open, both untouched-lock
            // blocks byte-identical to before the call.
            Assert.Equal(sectionBytesBefore, File.ReadAllText(sectionPath));
            Assert.Equal(block1BytesBefore, File.ReadAllText(block1Path));
            Assert.Equal(block3BytesBefore, File.ReadAllText(block3Path));
            Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(block1Path)).Frontmatter.Status);
            Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(block3Path)).Frontmatter.Status);

            // b-1's lock — acquired successfully before the call blocked on b-2's — is released by
            // the finally, not held past the failed call's own return.
            var block1Reacquire = CardLock.Acquire(block1Path, TimeSpan.FromSeconds(5));
            block1Reacquire.Match<object?>(
                onAcquired: acquired =>
                {
                    acquired.Lock.Dispose();
                    return null;
                },
                onTimedOut: timedOut => throw new Xunit.Sdk.XunitException(
                    $"b-1's lock was not released after CloseSection timed out on b-2's: {timedOut.Message}"));

            // b-3's lock was never even attempted (ordinal order stopped at b-2) — also free.
            var block3Reacquire = CardLock.Acquire(block3Path, TimeSpan.FromSeconds(5));
            block3Reacquire.Match<object?>(
                onAcquired: acquired =>
                {
                    acquired.Lock.Dispose();
                    return null;
                },
                onTimedOut: timedOut => throw new Xunit.Sdk.XunitException(
                    $"b-3's lock was unexpectedly unavailable after the call returned: {timedOut.Message}"));
        }
        finally
        {
            plantedBlock2Lock.Dispose();
        }

        // A retried close, now that the plant is released, succeeds and lands every block — the
        // record the timed-out attempt left behind is exactly what a retry can resolve.
        var retried = AssertClosed(CardStore.CloseSection(
            testRoot, sectionPath, CardOwner.Architect, Created.AddMinutes(1), TimeSpan.FromSeconds(10), ChangeName));
        Assert.Equal(3, retried.LandedBlocks.Count);
        Assert.All(retried.LandedBlocks, block => Assert.Equal("landed", block.Frontmatter.Status));
    }

    /// <summary>
    /// The other half of the timeout proof: a block's lock held only briefly — released well
    /// inside the call's own budget — lets the same blocking acquisition make real progress rather
    /// than only ever timing out cleanly. Proves <see cref="CardStore.CloseSectionUnderExistingLock"/>'s
    /// blocking (not release-and-retry) acquisition genuinely waits rather than failing fast the
    /// instant it meets contention.
    /// </summary>
    [Fact]
    public async Task CloseSection_WhenAContendedBlockLockIsReleasedPartway_TheWaitingCallSucceeds()
    {
        var testRoot = Path.Combine(_root, "succeeds-after-release");
        var directory = SetUpDirectory(testRoot);

        var sectionPath = WriteSectionCard(directory, "s-0002", "S-0002");
        var blockPath = WriteApprovedBlockCard(directory, "b-1", "B-0004", "S-0002");

        var plantedLock = AssertAcquired(CardLock.Acquire(blockPath, TimeSpan.FromSeconds(30)));

        var closeTask = Task.Run(() => CardStore.CloseSection(
            testRoot, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.False(closeTask.IsCompleted, "the call finished before the planted block lock was ever released.");
        plantedLock.Dispose();

        var outcome = await closeTask;
        var closed = AssertClosed(outcome);
        Assert.Single(closed.LandedBlocks);
        Assert.Equal("landed", AssertParseSuccess(CardStore.ReadCard(blockPath)).Frontmatter.Status);
    }

    /// <summary>
    /// Real-concurrency stress, the same "everyone starts at once" shape <see
    /// cref="CardFindingRecordConcurrencyTests.TwoInvocations_SharingTheSameBlindSpotFilePath_NeverLoseOrCorruptEitherCard"/>
    /// uses: <see cref="Rounds"/> entirely independent sections, each carrying <see cref="
    /// BlocksPerSection"/> approved blocks, all closed simultaneously — every round's <see
    /// cref="CardStore.CloseSection"/> call racing every other round's across the whole thread pool
    /// at once, not two at a time. Independent sections share no files, so nothing here should ever
    /// contend at all; what this proves is that acquiring several locks per call, many calls at
    /// once, exercises the real file-lock machinery under genuine thread-pool load without
    /// deadlocking, corrupting a card, or leaving any section partially landed.
    /// </summary>
    [Fact]
    public async Task CloseSection_ManyIndependentSectionsClosedSimultaneously_AllSucceed_NoCorruption_NoDeadlock()
    {
        const int rounds = Rounds;
        const int blocksPerSection = BlocksPerSection;

        ThreadPool.GetMinThreads(out var previousWorkerThreads, out var previousCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(previousWorkerThreads, rounds + 4), previousCompletionPortThreads);
        try
        {
            var sectionPaths = new string[rounds];
            var blockPaths = new string[rounds][];

            for (var i = 0; i < rounds; i++)
            {
                var roundRoot = Path.Combine(_root, $"stress-{i}");
                var directory = SetUpDirectory(roundRoot);
                var sectionId = $"S-STRESS-{i}";
                sectionPaths[i] = WriteSectionCard(directory, "s-round", sectionId);

                blockPaths[i] = new string[blocksPerSection];
                for (var b = 0; b < blocksPerSection; b++)
                {
                    blockPaths[i][b] = WriteApprovedBlockCard(directory, $"b-round-{b}", $"B-STRESS-{i}-{b}", sectionId);
                }
            }

            using var startBarrier = new Barrier(rounds);
            var tasks = new Task<(int Round, CardSectionCloseOutcome Outcome)>[rounds];
            for (var i = 0; i < rounds; i++)
            {
                var round = i;
                var roundRoot = Path.Combine(_root, $"stress-{round}");
                var sectionPath = sectionPaths[round];
                tasks[round] = Task.Run(() =>
                {
                    startBarrier.SignalAndWait();
                    var outcome = CardStore.CloseSection(
                        roundRoot, sectionPath, CardOwner.Supervisor, Created, TimeSpan.FromSeconds(20), ChangeName);
                    return (round, outcome);
                });
            }

            var overall = System.Diagnostics.Stopwatch.StartNew();
            var results = await Task.WhenAll(tasks);
            overall.Stop();

            // Every round is independent — no genuine contention exists between them, so a healthy
            // run finishes fast. A stall anywhere near the per-call timeout would look like an
            // unexpected cross-round block, which should be structurally impossible (see
            // CloseSectionUnderExistingLock's own doc comment) but is exactly what this bound
            // would catch if it were not.
            Assert.True(
                overall.Elapsed < TimeSpan.FromSeconds(15),
                $"took {overall.Elapsed} across {rounds} fully independent sections — looks like unexpected cross-round contention.");

            foreach (var (round, outcome) in results)
            {
                var closed = outcome.Match(
                    onClosed: static c => c,
                    onAlreadyClosed: already => throw new Xunit.Sdk.XunitException($"round {round}: unexpected AlreadyClosed('{already.FilePath}')"),
                    onNotASectionCard: n => throw new Xunit.Sdk.XunitException($"round {round}: unexpected NotASectionCard({n.Kind.ToWireString()})"),
                    onBlockNotApproved: n => throw new Xunit.Sdk.XunitException($"round {round}: unexpected BlockNotApproved({n.BlockId}, {n.ActualState})"),
                    onBlockGateFailed: f => throw new Xunit.Sdk.XunitException($"round {round}: unexpected BlockGateFailed({f.BlockId})"),
                    onBlockGateAbsent: a => throw new Xunit.Sdk.XunitException($"round {round}: unexpected BlockGateAbsent({a.BlockId})"),
                    onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"round {round}: unexpected CardNotFound('{notFound.FilePath}')"),
                    onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"round {round}: unexpected LayoutMismatch: {layoutMismatch.Reason}"),
                    onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"round {round}: unexpected CardCorrupt: {corrupt.Reason}"),
                    onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"round {round}: unexpected ToolFailure (looks like a deadlock or lost race): {toolFailure.Reason}"));

                Assert.Equal(blocksPerSection, closed.LandedBlocks.Count);
                Assert.All(closed.LandedBlocks, block => Assert.Equal("landed", block.Frontmatter.Status));

                foreach (var blockPath in blockPaths[round])
                {
                    Assert.Equal("landed", AssertParseSuccess(CardStore.ReadCard(blockPath)).Frontmatter.Status);
                }
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(previousWorkerThreads, previousCompletionPortThreads);
        }
    }

    private static string SetUpDirectory(string testRoot)
    {
        var directory = Path.Combine(testRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WriteSectionCard(string directory, string fileStem, string id)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteApprovedBlockCard(string directory, string fileStem, string id, string sectionId)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, sectionId, Created, Created);
        var blockFields = new BlockCardFields(
            Base: "base-commit", ReviewedState: CurrentState, Tasks: ["5.1"], Round: null, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static CardSectionCloseOutcome.Closed AssertClosed(CardSectionCloseOutcome outcome) =>
        outcome.Match(
            onClosed: static closed => closed,
            onAlreadyClosed: static already => throw new Xunit.Sdk.XunitException($"expected Closed, got AlreadyClosed: '{already.FilePath}'"),
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got NotASectionCard({n.Kind.ToWireString()})"),
            onBlockNotApproved: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockNotApproved({n.BlockId}, {n.ActualState})"),
            onBlockGateFailed: static f => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockGateFailed({f.BlockId})"),
            onBlockGateAbsent: static a => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockGateAbsent({a.BlockId})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Closed, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Closed, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Closed, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Closed, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
