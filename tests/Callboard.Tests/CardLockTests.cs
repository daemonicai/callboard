using System.Diagnostics;
using System.Globalization;
using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardLockTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "callboard-lock-tests-" + Guid.NewGuid().ToString("N"));

    public CardLockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Acquire_TwoDistinctCards_IsContentionFree()
    {
        var cardA = Path.Combine(_directory, "a.md");
        var cardB = Path.Combine(_directory, "b.md");

        var lockA = AssertAcquired(CardLock.Acquire(cardA, TimeSpan.FromSeconds(5)));
        try
        {
            // Card B's own lock is untouched by card A's — record-retrieval's "acting on distinct
            // cards SHALL be contention-free" — so this succeeds well inside a short timeout
            // rather than waiting on A at all.
            var stopwatch = Stopwatch.StartNew();
            var resultB = CardLock.Acquire(cardB, TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            var lockB = AssertAcquired(resultB);
            lockB.Dispose();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"expected no contention, took {stopwatch.Elapsed}");
        }
        finally
        {
            lockA.Dispose();
        }
    }

    [Fact]
    public void Acquire_TimesOut_WhenAnotherHolderStillHoldsTheLock()
    {
        var cardPath = Path.Combine(_directory, "held.md");

        var holder = AssertAcquired(CardLock.Acquire(cardPath, TimeSpan.FromSeconds(5)));
        try
        {
            var result = CardLock.Acquire(cardPath, TimeSpan.FromMilliseconds(200));

            var timedOut = AssertTimedOut(result);
            Assert.Equal(cardPath, timedOut.CardPath);
            Assert.Contains(cardPath, timedOut.Message, StringComparison.Ordinal);
            Assert.Contains(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                timedOut.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void Acquire_SucceedsAgain_AfterTheHolderReleases()
    {
        var cardPath = Path.Combine(_directory, "reacquired.md");

        var first = AssertAcquired(CardLock.Acquire(cardPath, TimeSpan.FromSeconds(5)));
        first.Dispose();

        var second = AssertAcquired(CardLock.Acquire(cardPath, TimeSpan.FromSeconds(5)));
        second.Dispose();
    }

    [Fact]
    public void Acquire_BreaksAStaleLock_LeftByAProcessThatNoLongerExists()
    {
        var cardPath = Path.Combine(_directory, "stale.md");
        var lockPath = cardPath + ".lock";
        File.WriteAllText(lockPath, FindAlmostCertainlyDeadPid().ToString(CultureInfo.InvariantCulture));

        // A stale lock is broken on sight, not waited out — assert it resolves fast, well inside
        // a timeout long enough that "it merely timed out and then happened to succeed" would
        // also pass, which would be the wrong thing for this test to accept.
        var stopwatch = Stopwatch.StartNew();
        var result = CardLock.Acquire(cardPath, TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        var acquired = AssertAcquired(result);
        acquired.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"expected the stale lock to be broken immediately, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Acquire_DoesNotBreakALock_WhoseHolderPidBelongsToThisStillRunningProcess()
    {
        var cardPath = Path.Combine(_directory, "live.md");
        var lockPath = cardPath + ".lock";
        File.WriteAllText(lockPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        // This process is (trivially) alive, so the lock must not be treated as stale — it can
        // only be waited out, and this proves that by timing out rather than acquiring.
        var result = CardLock.Acquire(cardPath, TimeSpan.FromMilliseconds(200));

        AssertTimedOut(result);
        File.Delete(lockPath);
    }

    [Fact]
    public void Acquire_BreaksAnOrphanedEmptyLock_LeftByAProcessKilledBetweenCreateAndWritingItsPid()
    {
        var cardPath = Path.Combine(_directory, "orphaned-empty.md");
        var lockPath = cardPath + ".lock";

        // Simulate a process that won FileMode.CreateNew and was killed before it wrote its pid:
        // create the file, write nothing, close it. Back-date it past the grace window rather
        // than sleeping the test through it, so this stays a fast, deterministic assertion of the
        // policy rather than a timing-sensitive one.
        using (File.Create(lockPath))
        {
        }
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        var result = CardLock.Acquire(cardPath, TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        var acquired = AssertAcquired(result);
        acquired.Dispose();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"expected the orphaned empty lock to be broken well inside the timeout, took {stopwatch.Elapsed}");
    }

    [Fact]
    public void Acquire_DoesNotBreakAFreshEmptyLock_StillInsideTheGraceWindow()
    {
        var cardPath = Path.Combine(_directory, "fresh-empty.md");
        var lockPath = cardPath + ".lock";

        // A lock file created just now, with no pid written yet, is indistinguishable from a live
        // holder mid-acquire — it must not be broken, only waited out.
        using (File.Create(lockPath))
        {
        }

        var result = CardLock.Acquire(cardPath, TimeSpan.FromMilliseconds(200));

        AssertTimedOut(result);
        File.Delete(lockPath);
    }

    [Fact]
    public void Dispose_DoesNotDeleteALockFile_WhoseContentNoLongerMatchesWhatThisInstanceWrote()
    {
        var cardPath = Path.Combine(_directory, "reclaimed.md");
        var lockPath = cardPath + ".lock";

        var acquired = AssertAcquired(CardLock.Acquire(cardPath, TimeSpan.FromSeconds(5)));

        // Simulate this instance losing ownership of its own lock path to a second contender —
        // exactly what the empty-lock grace window can let happen if this instance's own
        // TryCreate write stalls past the grace window: the file at _lockPath is still there, but
        // it is now someone else's live lock, not the one this instance created.
        const string anotherHoldersContent = "999999\nnot-this-instances-nonce";
        File.WriteAllText(lockPath, anotherHoldersContent);

        acquired.Dispose();

        // Release-by-content must refuse to unlink a file it did not itself write, leaving the
        // substituted holder's lock exactly as it found it.
        Assert.True(File.Exists(lockPath));
        Assert.Equal(anotherHoldersContent, File.ReadAllText(lockPath));
    }

    [Fact]
    public void Acquire_RetriesAndSucceeds_WhenTryCreatesOwnWriteLosesTheRaceBeforeReturning()
    {
        var cardPath = Path.Combine(_directory, "stalled-write.md");
        var lockPath = cardPath + ".lock";
        var deadPid = FindAlmostCertainlyDeadPid();
        var sawMismatch = false;

        // Stand in for the scheduler stall this instance's own doc comment describes: right after
        // TryCreate's write completes — before it re-reads to verify — substitute the file's
        // content, exactly as a second contender's own genuine acquisition would look on disk had
        // it broken this attempt's still-effectively-orphaned file and won the path first. This
        // fires once, guarded by a local flag rather than clearing a shared static — the hook is
        // threaded through Acquire's own call-scoped parameter, so nothing outside this call stack
        // ever observes it, and a concurrent Acquire on an unrelated card racing in another test
        // collection cannot trip it.
        var hookFired = false;

        var result = CardLock.Acquire(
            cardPath,
            TimeSpan.FromSeconds(5),
            testOnlyAfterWriteHook: path =>
            {
                if (hookFired)
                {
                    return;
                }

                hookFired = true;
                sawMismatch = true;
                File.WriteAllText(path, deadPid.ToString(CultureInfo.InvariantCulture) + "\nanother-holders-nonce");
            });
        var acquired = AssertAcquired(result);
        try
        {
            Assert.True(sawMismatch, "expected the post-write hook to have fired");

            // The lock this instance now believes it holds must be the one it actually wrote on
            // its retry, not the substituted content the mismatch check was supposed to reject.
            var onDisk = File.ReadAllText(lockPath);
            Assert.StartsWith(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n",
                onDisk,
                StringComparison.Ordinal);
            Assert.DoesNotContain("another-holders-nonce", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            acquired.Dispose();
        }
    }

    [Fact]
    public void Acquire_DoesNotDeleteALiveLock_WonByAnotherWaiterBetweenJudgingTheHolderDeadAndDeleting()
    {
        var cardPath = Path.Combine(_directory, "two-waiters.md");
        var lockPath = cardPath + ".lock";
        var deadPid = FindAlmostCertainlyDeadPid();
        File.WriteAllText(lockPath, deadPid.ToString(CultureInfo.InvariantCulture));

        // This process's own pid, not a fabricated one — Process.GetProcessById(Environment.ProcessId)
        // is trivially, unconditionally alive for as long as the test runs, so once substituted this
        // content is protected from every later loop iteration's own liveness check too, not only the
        // one iteration the hook fires on. That is what lets a single deterministic assertion at the
        // end of a short timeout stand in for "never deleted", not just "not deleted on this one pass".
        var anotherWaitersLiveContent =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\nanother-waiters-nonce";
        var hookFired = false;

        // Stand in for a second waiter racing this call between it reading the dead pid (and
        // judging it dead via Process.GetProcessById, which is not cheap) and this call's own
        // delete: substitute the file with a live lock a faster waiter has already won — the exact
        // shape the DEVLOG's two-waiters trace describes for the crash-recovery path ADR-0003 calls
        // expected. Fires once, scoped to this call's stack via the threaded parameter — the same
        // seam pattern as TestOnlyAfterWriteHook, never a shared static.
        var result = CardLock.Acquire(
            cardPath,
            TimeSpan.FromMilliseconds(200),
            testOnlyBeforeStaleDeleteHook: path =>
            {
                if (hookFired)
                {
                    return;
                }

                hookFired = true;
                File.WriteAllText(path, anotherWaitersLiveContent);
            });

        AssertTimedOut(result);
        Assert.True(hookFired, "expected the before-delete hook to have fired");

        // The other waiter's live lock must survive untouched — deleting it would be exactly the
        // "two writers on one card" bug this fix closes: a third contender could then acquire while
        // the second still believed it held the card.
        Assert.Equal(anotherWaitersLiveContent, File.ReadAllText(lockPath));

        File.Delete(lockPath);
    }

    [Fact]
    public void Acquire_DoesNotBreakALock_WhoseContentIsNonEmptyButUnparseable()
    {
        var cardPath = Path.Combine(_directory, "garbage.md");
        var lockPath = cardPath + ".lock";

        // Non-empty, unparseable content is ambiguous in a way a zero-byte file is not — it could
        // be a live holder mid-write of a real pid, truncated at the moment it's read — so it
        // stays "never guessed at" regardless of age, even well past the empty-lock grace window.
        File.WriteAllText(lockPath, "not-a-pid");
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(30));

        var result = CardLock.Acquire(cardPath, TimeSpan.FromMilliseconds(200));

        AssertTimedOut(result);
        File.Delete(lockPath);
    }

    private static int FindAlmostCertainlyDeadPid()
    {
        for (var candidate = 999_999; candidate > 100_000; candidate--)
        {
            try
            {
                using var process = Process.GetProcessById(candidate);
            }
            catch (ArgumentException)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("could not find an unused pid to fabricate a stale lock with.");
    }

    // §6 block B second remediation, reviewer's "identical path" finding: CurrentlyNames decides
    // "does this other path string name the file I already hold" by content, not by comparing path
    // strings — because no single StringComparison is right across every filesystem this project
    // ships on (Ordinal is wrong on case-insensitive APFS, OrdinalIgnoreCase is wrong on
    // case-sensitive APFS). These tests exercise that decision directly and portably: rather than
    // depending on the test-running filesystem's actual case-folding behaviour (which would make
    // this suite's own result filesystem-dependent, exactly the bug being fixed), the "same file,
    // different path string" case is simulated by writing the identical lock content a real
    // acquisition would produce to a second path — the only way that content could legitimately
    // exist there is if it really is the same file reached differently.
    [Fact]
    public void CurrentlyNames_ItsOwnPath_IsTrue()
    {
        var path = Path.Combine(_directory, "self.md");
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(held.CurrentlyNames(path));
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public void CurrentlyNames_AGenuinelyDifferentPath_IsFalse()
    {
        var path = Path.Combine(_directory, "self.md");
        var otherPath = Path.Combine(_directory, "other.md");
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(held.CurrentlyNames(otherPath));
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public void CurrentlyNames_APathWithNoLockFileAtAll_IsFalse()
    {
        var path = Path.Combine(_directory, "self.md");
        var neverAcquired = Path.Combine(_directory, "never-acquired.md");
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(held.CurrentlyNames(neverAcquired));
        }
        finally
        {
            held.Dispose();
        }
    }

    // The load-bearing case: a path string that differs from the held lock's own path but whose
    // .lock file carries the exact content this acquisition wrote — exactly what a case- or
    // separator-variant path resolving to the same physical file on this volume would produce,
    // simulated here without depending on this test-running machine's actual filesystem semantics.
    [Fact]
    public void CurrentlyNames_ADifferentPathStringWhoseLockFileHasTheExactSameContent_IsTrue()
    {
        var path = Path.Combine(_directory, "samepath.md");
        var caseVariantPath = Path.Combine(_directory, "SAMEPATH.md");
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));
        try
        {
            // On a case-insensitive filesystem this "copy" would be redundant — `path + ".lock"`
            // and `caseVariantPath + ".lock"` are already the same file — but writing it explicitly
            // is what keeps this test's own result independent of the machine it runs on, which is
            // exactly the property the production fix itself now has.
            var ownLockContent = File.ReadAllText(path + ".lock");
            File.WriteAllText(caseVariantPath + ".lock", ownLockContent);

            Assert.True(held.CurrentlyNames(caseVariantPath));
        }
        finally
        {
            held.Dispose();
            // Clean up the simulated second lock file — on a genuinely case-insensitive filesystem
            // this is the same file Dispose() above already removed; File.Delete on an already-gone
            // path is not attempted (Delete throws if it truly doesn't exist on some platforms only
            // via a missing check — File.Exists guards it here).
            if (File.Exists(caseVariantPath + ".lock"))
            {
                File.Delete(caseVariantPath + ".lock");
            }
        }
    }

    // The mismatch half: a genuinely different file's lock content, even if non-empty, must never
    // be mistaken for "the same file" — content equality is the entire proof, not mere presence.
    [Fact]
    public void CurrentlyNames_ADifferentPathStringWithDifferentLockContent_IsFalse()
    {
        var path = Path.Combine(_directory, "self.md");
        var otherPath = Path.Combine(_directory, "unrelated-but-locked.md");
        var otherHeld = AssertAcquired(CardLock.Acquire(otherPath, TimeSpan.FromSeconds(5)));
        var held = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(held.CurrentlyNames(otherPath));
        }
        finally
        {
            held.Dispose();
            otherHeld.Dispose();
        }
    }

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match<CardLock>(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out instead: {timedOut.Message}"));

    private static CardLockResult.TimedOut AssertTimedOut(CardLockResult result) =>
        result.Match(
            onAcquired: acquired =>
            {
                acquired.Lock.Dispose();
                throw new Xunit.Sdk.XunitException("expected the lock acquisition to time out, it succeeded instead.");
            },
            onTimedOut: timedOut => timedOut);
}
