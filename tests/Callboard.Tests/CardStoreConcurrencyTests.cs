using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 2.7 — two concurrent comment appends to one card both survive, in a determinate order.
///
/// <b>Ordering rule (record-retrieval: "the thread's order is preserved"):</b> the per-card lock
/// makes every append a strict read-current-file / add-one-comment / write-back cycle, so whichever
/// append acquires the lock first is the append that lands first in the file — a later append that
/// had to wait for the lock can never land ahead of one already in flight. <see cref="TwoConcurrentAppends_SurviveInLockAcquisitionOrder"/>
/// makes that order an experimental fact rather than an assumption: it deliberately holds the lock
/// itself to force append B to block behind append A, then asserts the file's comment order matches
/// exactly the order the lock was held in. <see cref="ManyConcurrentAppends_AllSurviveWithNoLossOrCorruption_UnderRealContention"/>
/// complements it with genuine unforced thread contention, where the winning order is not
/// predetermined — the property under test there is that the rule holds under a real race (every
/// comment survives exactly once, the file stays well-formed), not a specific order.
/// </summary>
public sealed class CardStoreConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-concurrency-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardStoreConcurrencyTests()
    {
        // Cards written under a change name, not directly under the temp root — CardStore now
        // validates every write against the scope-shaped directory CardLayout resolves, so a
        // test's own paths have to be real ones (record-retrieval / D3), not an arbitrary temp dir.
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoConcurrentAppends_SurviveInLockAcquisitionOrder()
    {
        var path = Path.Combine(_directory, "card.md");
        WriteInitialCard(path);

        var commentA = new CardComment("C-A", CardOwner.Worker, Created, "First to acquire the lock.", null, null, false, []);
        var commentB = new CardComment("C-B", CardOwner.Reviewer, Created, "Waits for A, then appends after.", null, null, false, []);

        // Hold the lock ourselves first, so B is guaranteed to still be waiting on it — the
        // ordering this test asserts is forced by that hold, not by timing luck.
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        var appendBTask = Task.Run(
            () => CardStore.AppendComment(path, commentB, TimeSpan.FromSeconds(10), ChangeName),
            TestContext.Current.CancellationToken);

        // B cannot possibly have acquired the lock yet — we are still holding it — so appending A
        // now, still under our hold, is guaranteed to land first regardless of scheduling.
        var resultA = CardStore.AppendCommentUnderExistingLock(path, commentA, ChangeName);
        AssertSuccess(resultA);

        held.Dispose();

        var resultB = await appendBTask;
        AssertSuccess(resultB);

        var final = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, final.Comments.Count);
        Assert.Equal(commentA, final.Comments[0]);
        Assert.Equal(commentB, final.Comments[1]);
    }

    [Fact]
    public void ManyConcurrentAppends_AllSurviveWithNoLossOrCorruption_UnderRealContention()
    {
        var path = Path.Combine(_directory, "stress.md");
        WriteInitialCard(path);

        const int appendCount = 20;
        var comments = Enumerable.Range(0, appendCount)
            .Select(i => new CardComment($"C-{i:D3}", CardOwner.Worker, Created, $"Comment {i}.", null, null, false, []))
            .ToList();

        // Dedicated threads, not the thread pool: CardLock.Acquire's retry loop blocks on
        // Thread.Sleep while contending, and driving 20 of those through Parallel.ForEach lets the
        // pool's slow ramp-up throttle how many run at once, which can push a legitimately-queued
        // append past a timeout that has nothing to do with this test's actual invariant.
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = comments
            .Select(comment => new Thread(() =>
            {
                try
                {
                    AssertSuccess(CardStore.AppendComment(path, comment, TimeSpan.FromSeconds(30), ChangeName));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }))
            .ToList();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (!exceptions.IsEmpty)
        {
            throw new AggregateException(exceptions);
        }

        var final = AssertParseSuccess(CardStore.ReadCard(path));

        Assert.Equal(appendCount, final.Comments.Count);
        Assert.Equal(
            comments.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal),
            final.Comments.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(final.Comments.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(), final.Comments.Count);
    }

    private static void WriteInitialCard(string path)
    {
        var frontmatter = new CardFrontmatter(
            "B-0200", CardKind.Block, "Concurrent appends", "open", CardOwner.Worker, CardScope.Change, "2", Created, Created);
        AssertSuccess(CardStore.WriteCard(path, new CardFile(frontmatter, "Body.", [], []), TimeSpan.FromSeconds(5), ChangeName));
    }

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match<CardLock>(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected write success, got failure: {failure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
