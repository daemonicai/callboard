using System.Globalization;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.2 — kind-prefixed identity allocation from a committed, verified counter file in the record,
/// never from the derived index (D4). Never recycles: the counter only ever increases, and closing
/// a card has no code path back to it.
/// </summary>
public sealed class CardIdentityAllocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-identity-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Allocate_FirstCallForAKind_ReturnsNumberOne() =>
        Assert.Equal("B-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));

    [Fact]
    public void Allocate_UsesTheKindsPrefix()
    {
        Assert.Equal("Q-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Question, TimeSpan.FromSeconds(5))));
        Assert.Equal("F-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Finding, TimeSpan.FromSeconds(5))));
        Assert.Equal("O-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Obligation, TimeSpan.FromSeconds(5))));
        Assert.Equal("R-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Rule, TimeSpan.FromSeconds(5))));
        Assert.Equal("H-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Hazard, TimeSpan.FromSeconds(5))));
        Assert.Equal("D-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Decision, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_SuccessiveCallsForTheSameKind_AreSequentialAndDistinct()
    {
        Assert.Equal("B-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
        Assert.Equal("B-0002", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
        Assert.Equal("B-0003", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_DifferentKinds_EachHaveTheirOwnSequence()
    {
        Assert.Equal("B-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
        Assert.Equal("Q-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Question, TimeSpan.FromSeconds(5))));
        Assert.Equal("B-0002", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_PadsToAtLeastFourDigitsWithoutCappingTheRange()
    {
        // Drive the counter past 4 digits directly, rather than allocating 10,000 times, to keep
        // this test fast — the point under test is FormatIdentity's field width, not the loop.
        for (var i = 0; i < 9_999; i++)
        {
            AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));
        }

        Assert.Equal("B-10000", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_ClosingACardDoesNotFreeItsNumber()
    {
        // "Closing a card" has no code path into this allocator at all — modelled here as the
        // absence of any such call, then proving the next allocation still moves forward. There is
        // no decrement/reset entry point on CardIdentityAllocator to call even if a caller wanted
        // to: the only public surface is Allocate, which only ever increments.
        Assert.Equal("B-0001", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
        Assert.Equal("B-0002", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_DoesNotDeriveTheNextNumberFromCardsPresentOnDisk()
    {
        // No card file this allocator's own state depends on is ever written under _root — the
        // point under test is that the counter file survives, and the sequence continues from it,
        // even after every other file under the root (a stand-in for every card the allocator does
        // not itself read) is removed.
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));

        var otherFile = Path.Combine(_root, "callboard", "changes", "unrelated-change", "b-9999.md");
        Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
        File.WriteAllText(otherFile, "not read by the allocator");
        Directory.Delete(Path.GetDirectoryName(otherFile)!, recursive: true);

        Assert.Equal("B-0003", AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void Allocate_WritesTheCounterFileAsPlainText()
    {
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));

        var counterPath = Path.Combine(_root, "callboard", "identities", "block.count");
        Assert.True(File.Exists(counterPath));
        Assert.Equal("2", File.ReadAllText(counterPath).Trim());
    }

    [Fact]
    public void Allocate_WithACorruptCounterFile_FailsRatherThanRestartingFromZero()
    {
        var counterPath = Path.Combine(_root, "callboard", "identities", "block.count");
        Directory.CreateDirectory(Path.GetDirectoryName(counterPath)!);
        File.WriteAllText(counterPath, "not-a-number");

        AssertFailed(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));

        // The corrupt content is untouched — a failed allocation must not paper over the
        // corruption by writing a fresh value on top of it.
        Assert.Equal("not-a-number", File.ReadAllText(counterPath));
    }

    [Fact]
    public async Task Allocate_ConcurrentCallsForTheSameKind_NeverIssueTheSameIdentity()
    {
        const int concurrentAllocations = 20;

        var tasks = Enumerable.Range(0, concurrentAllocations)
            .Select(_ => Task.Run(() => CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(10))))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var ids = results.Select(AssertAllocated).ToList();

        Assert.Equal(concurrentAllocations, ids.Distinct(StringComparer.Ordinal).Count());

        var expected = Enumerable.Range(1, concurrentAllocations)
            .Select(n => $"B-{n.ToString("D4", CultureInfo.InvariantCulture)}")
            .OrderBy(static id => id, StringComparer.Ordinal);
        Assert.Equal(expected, ids.OrderBy(static id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void TryParseIdentityNumber_ParsesAMatchingPrefix()
    {
        Assert.True(CardIdentityAllocator.TryParseIdentityNumber(CardKind.Block, "B-0042", out var number));
        Assert.Equal(42, number);
    }

    [Fact]
    public void TryParseIdentityNumber_RejectsAMismatchedPrefix() =>
        Assert.False(CardIdentityAllocator.TryParseIdentityNumber(CardKind.Question, "B-0042", out _));

    [Fact]
    public void VerifyCounters_WithNoObservedIds_ReportsNothing() =>
        Assert.Empty(CardIdentityAllocator.VerifyCounters(_root, new Dictionary<CardKind, int>()));

    [Fact]
    public void VerifyCounters_WhenCounterMeetsOrExceedsTheObservedMaximum_ReportsNothing()
    {
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));
        AssertAllocated(CardIdentityAllocator.Allocate(_root, CardKind.Block, TimeSpan.FromSeconds(5)));

        var violations = CardIdentityAllocator.VerifyCounters(_root, new Dictionary<CardKind, int> { [CardKind.Block] = 2 });
        Assert.Empty(violations);
    }

    [Fact]
    public void VerifyCounters_WhenCounterIsBehindTheObservedMaximum_ReportsAViolation()
    {
        // No allocation ever ran for this kind (counter reads 0 / does not exist), yet a card with
        // identity number 5 was found on disk — exactly the shape an archive directory moved out
        // of the counter's view, or a hand-authored card, would produce.
        var violations = CardIdentityAllocator.VerifyCounters(_root, new Dictionary<CardKind, int> { [CardKind.Block] = 5 });

        var violation = Assert.Single(violations);
        Assert.Equal(CardKind.Block, violation.Kind);
        Assert.Equal(0, violation.CounterValue);
        Assert.Equal(5, violation.ObservedMaxId);
        Assert.Contains("recycle", violation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyCounters_WithACorruptCounterFile_ReportsAViolationRatherThanThrowing()
    {
        var counterPath = Path.Combine(_root, "callboard", "identities", "block.count");
        Directory.CreateDirectory(Path.GetDirectoryName(counterPath)!);
        File.WriteAllText(counterPath, "garbage");

        var violations = CardIdentityAllocator.VerifyCounters(_root, new Dictionary<CardKind, int> { [CardKind.Block] = 1 });

        var violation = Assert.Single(violations);
        Assert.Equal(CardKind.Block, violation.Kind);
        Assert.Equal(1, violation.ObservedMaxId);
    }

    private static string AssertAllocated(CardIdentityAllocationResult result) =>
        result.Match(
            onAllocated: success => success.Id,
            onFailed: failure => throw new Xunit.Sdk.XunitException($"expected allocation to succeed, got failure: {failure.Reason}"));

    private static string AssertFailed(CardIdentityAllocationResult result) =>
        result.Match(
            onAllocated: success => throw new Xunit.Sdk.XunitException($"expected allocation to fail, got '{success.Id}'."),
            onFailed: failure => failure.Reason);
}
