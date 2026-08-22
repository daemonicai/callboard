using System.Diagnostics;
using System.Globalization;

namespace Callboard.Cards;

/// <summary>
/// The per-card advisory lock design.md D7 / ADR-0003 call for: one lock file beside the card it
/// guards (<c>&lt;card-path&gt;.lock</c>), so contention is scoped to that single card — acting on
/// two distinct cards SHALL be contention-free (record-retrieval), and a lock keyed off the card's
/// own path, rather than any shared or global resource, is what makes that true without SQLite or
/// any other coordination point (D7 explicitly rejects serialising through the index).
///
/// <para>
/// <b>Stale-holder decision (recorded for the DEVLOG, not just here):</b> a crashed agent's lock
/// is not left to expire on the timeout alone. The lock file's content is the holder's OS process
/// id; a caller that cannot acquire the lock checks whether that process is still alive
/// (<see cref="Process.GetProcessById(int)"/>) before waiting, and deletes-and-retries a lock
/// whose holder is gone rather than making every other writer wait out the full timeout for a
/// process that will never release it. This trades a small, accepted risk — PID reuse racing
/// exactly into the stale window, so a live unrelated process is mistaken for the original holder
/// — for the alternative of an indefinitely wedged card whenever an agent is killed rather than
/// exited cleanly, which ADR-0003's own consequence calls the expected case, not an exotic one.
/// A lock that cannot be read or parsed is never guessed at — treated as live, so acquisition
/// falls through to the ordinary timeout instead of guessing.
/// </para>
///
/// <para>
/// <b>Contention fix (recorded for the DEVLOG):</b> the lock file used to be created with
/// <c>FileShare.None</c>. On this platform's Unix <see cref="FileStream"/> implementation,
/// <c>FileShare.None</c> is enforced by a second, separate advisory-lock step after the file's
/// already been created by <see cref="FileMode.CreateNew"/> — so under real, heavy contention a
/// creator could win the atomic create and then lose that second step to a concurrent racer,
/// throwing "the process cannot access the file because it is being used by another process" and
/// returning <see langword="false"/> from <see cref="TryCreate(string)"/> as if it had lost the
/// race, while the empty file it had already created was left behind on disk. That empty,
/// content-less lock file is exactly the case <see cref="TryReadHolderPid(string, out int)"/>
/// cannot parse a pid from, so <see cref="TryBreakStaleLock(string)"/> correctly refuses to touch
/// it ("never guessed at") — and nothing else ever claims it, wedging the card until the process
/// exits. Mutual exclusion has never depended on <c>FileShare</c> — <see cref="FileMode.CreateNew"/>
/// alone is what only ever lets one caller win — so the fix drops the redundant second locking
/// step (<c>FileShare.Read</c>, which still keeps other writers out) rather than working around
/// its failure mode. Measured under this environment's real 20-thread contention (repeated
/// sandboxed full-suite runs): starvation with the old value, none observed after the change.
/// </para>
///
/// <para>
/// <b>Orphaned zero-byte lock (recorded for the DEVLOG):</b> a process can be killed in the
/// window between <see cref="FileMode.CreateNew"/> succeeding and its pid actually being written
/// — an ordinary <c>kill -9</c>, not a rare race — leaving a 0-byte lock file that
/// <see cref="TryReadHolderPid(string, out int)"/> can never parse. Two shapes were evaluated for
/// what happens next:
/// <list type="bullet">
/// <item><b>A create-only, no-overwrite atomic rename</b> (build the lock complete with its pid
/// already written at a temp path beside the target, then rename it into place so a lock file is
/// always either absent or complete, never observed half-made) — this would remove the need for
/// any staleness heuristic on this file at all. It was <i>not</i> adopted: a 32-thread, 20,000-round
/// hammer loop against <c>File.Move(src, dest, overwrite: false)</c> on this platform (the .NET
/// API that expresses a create-only rename) proved it is <b>not</b> atomic — 173,159 reported
/// successes across 20,000 rounds where exactly one was expected, and the destination's content
/// repeatedly did not match the reporting "winner". The BCL implementation is a check-then-rename,
/// the same TOCTOU shape as the <c>FileShare.None</c> bug this type already shipped once. Raw
/// platform-specific syscalls (e.g. macOS's <c>renamex_np</c> with <c>RENAME_EXCL</c>) were not
/// pursued — that trades one contained bug for permanent native-interop surface, disproportionate
/// once the managed path is ruled out by measurement rather than assumption.</item>
/// <item><b>An age-based grace window on zero-byte files specifically</b> — the shape actually
/// shipped, see <see cref="TryBreakOrphanedEmptyLock(string)"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Release-by-content, not release-by-path (recorded for the DEVLOG):</b> the grace window
/// above opens a further gap. <see cref="TryCreate(string, out string)"/>'s own buffered write
/// (the <c>using var writer</c> flushed only on <c>Dispose</c>) can stall for the full grace
/// window under real scheduler pressure — GC, a CPU quota, a suspended process — not just the
/// microseconds it takes on an idle machine. If it does, a second contender can see the still-0-
/// byte file as orphaned, break it, and create its own live lock at the same path; the original
/// holder's stalled write then lands harmlessly in an unlinked inode, but its own eventual
/// <see cref="Dispose"/> used to delete "whatever is at <c>_lockPath</c>" — which by then is the
/// second holder's real lock — releasing it while that holder may still be mid-write. Two writers
/// on one card is exactly what per-card locking exists to prevent. The fix: every lock this type
/// creates carries a per-acquisition nonce alongside its pid, <see cref="Dispose"/> compares the
/// file's current content against what this instance itself wrote before unlinking, and
/// <see cref="TryBreakOrphanedEmptyLock(string)"/> re-checks the file is still zero bytes
/// immediately before its own delete rather than trusting the age check alone. Both are
/// compare-then-delete, not delete-by-path — narrower, not atomic: a read-then-unlink is still
/// two operations, and nothing in the managed API surface offers an atomic compare-and-delete on
/// this platform (the same conclusion the create-only-rename measurement above already reached).
/// The residual left open: a contender could still win between the read/re-check and the delete
/// that follows it. That window is measured in the cost of one more file operation, not a full
/// grace window, and is stated here rather than left implicit.
/// </para>
///
/// <para>
/// <b>Acquisition-by-verified-write, not acquisition-by-successful-write (recorded for the
/// DEVLOG):</b> the release-by-content fix above closes the route where a stalled writer's
/// <em>eventual</em> <see cref="Dispose"/> deletes a lock it no longer owns. It does nothing for
/// the same stall's effect one step earlier, inside <see cref="TryCreate(string, out string)"/>
/// itself: a holder can stall between winning <see cref="FileMode.CreateNew"/> and its buffered
/// write flushing, be legitimately broken by <see cref="TryBreakOrphanedEmptyLock(string)"/> once
/// the grace window elapses, and — because POSIX keeps an open file handle valid past the unlink —
/// still have its stalled write "succeed" into the now-detached inode when the stall clears.
/// Without a check, <see cref="TryCreate(string, out string)"/> would return <see langword="true"/>
/// regardless, and <see cref="Acquire(string, TimeSpan)"/> would hand its caller a genuine
/// <see cref="CardLockResult.Acquired"/> for a path it no longer owns — two holders reached
/// entirely through acquisition, never touching release. The fix is the same discipline already
/// applied at both release sites, moved one step earlier: after the write, re-read the path and
/// compare against the content just written; a mismatch (including the file being gone) is
/// reported as an ordinary lost race, not an error. This is the general rule this type now applies
/// everywhere it establishes or relies on ownership of the lock file: verify a file operation's
/// effect immediately before acting on it, rather than assuming the effect persisted.
/// </para>
///
/// <para>
/// <b>The stale-holder break was the one site the rule above did not reach (§2 remediation,
/// recorded for the DEVLOG):</b> <see cref="TryBreakStaleLock(string, Action{string}?)"/> reads a
/// dead holder's recorded pid, confirms it via <see cref="Process.GetProcessById(int)"/> — not a
/// cheap call — and, before this fix, deleted whatever file was then at the path on the strength
/// of that now-stale read. Reached entirely through the crash-recovery path ADR-0003 calls
/// expected: waiters <c>C1</c>, <c>C2</c>, <c>C3</c> all read the same dead pid; <c>C1</c> wins the
/// delete-then-<see cref="TryCreate(string, out string, Action{string}?)"/> race and legitimately
/// holds the card; <c>C2</c>, still between its own liveness check and its own delete, deletes
/// <c>C1</c>'s live lock; <c>C3</c> then wins the now-free path — two holders, reached without
/// either the release-by-content or the acquisition-by-verified-write fix above ever seeing
/// anything wrong, because both are sound for the site each one guards and this was a third, only
/// visible once every prior round's fix was assumed complete. The fix applies the same discipline
/// as both prior fixes, at this one remaining site: re-read the file immediately before deleting
/// and compare against the exact content this call itself judged dead, refusing to delete on any
/// mismatch.
/// </para>
/// </summary>
internal sealed class CardLock : IDisposable
{
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// How old a zero-byte lock file must be before it is treated as orphaned rather than a live
    /// holder that has just created the file and not yet written its pid. Measured on this
    /// platform: 5,000 back-to-back create+write cycles never exceeded ~3ms, averaging well under
    /// 0.1ms. 1 second is roughly 300x that observed worst case — comfortably longer than a create
    /// and a single-line write can legitimately take, so a live holder mid-acquire is never robbed
    /// of its lock, while still recovering a genuinely orphaned file in a bounded, short time
    /// rather than wedging the card until a human intervenes.
    /// </summary>
    private static readonly TimeSpan EmptyLockGraceWindow = TimeSpan.FromSeconds(1);

    private readonly string _lockPath;
    private readonly string _ownContent;
    private bool _disposed;

    private CardLock(string lockPath, string cardPath, string ownContent)
    {
        _lockPath = lockPath;
        CardPath = cardPath;
        _ownContent = ownContent;
    }

    /// <summary>
    /// The path this lock guards — <em>not</em> the <c>.lock</c> file beside it. This is what
    /// makes a held <see cref="CardLock"/> the sole source of the path a
    /// <c>*UnderExistingLock</c> write method acts on (card-model 4.5, O-2 remediation): those
    /// methods no longer take a separate <c>filePath</c> argument that could name a different card
    /// than the one actually locked — there is exactly one path in play, and it comes from here.
    /// </summary>
    internal string CardPath { get; }

    /// <summary>
    /// Attempts to acquire the lock for <paramref name="cardPath"/>, retrying until either it
    /// succeeds or <paramref name="timeout"/> elapses. A lock whose recorded holder process is no
    /// longer running is broken immediately rather than counted against the timeout.
    /// </summary>
    /// <param name="testOnlyAfterWriteHook">
    /// Test-only seam: threaded through to <see cref="TryCreate(string, out string, Action{string}?)"/>,
    /// which invokes it immediately after its write completes and before its post-write self-verify
    /// read, given the lock path just written. Lets a test deterministically stand in for the
    /// scheduler stall this type's doc comment describes — substituting the file's content at
    /// exactly the point a real stall would let a second contender's own acquisition land — without
    /// an actual race or a timing-dependent sleep. Scoped to this call rather than a shared static:
    /// a process-wide mutable field here would let one test's substitution be read by an unrelated
    /// <see cref="TryCreate(string, out string, Action{string}?)"/> call racing concurrently in a
    /// different test collection, which is exactly the kind of unverified shared-state defect this
    /// type's own doc comments spent several rounds closing in production code. Never passed outside
    /// tests; <see langword="null"/> (the default) makes this a no-op.
    /// </param>
    /// <param name="testOnlyBeforeStaleDeleteHook">
    /// Test-only seam, same discipline as <paramref name="testOnlyAfterWriteHook"/>: threaded
    /// through to the private stale-lock-break helper, invoked immediately after it judges a
    /// lock's recorded holder dead and before its own compare-then-delete re-read, given the lock
    /// path. Lets a test deterministically stand in for a second waiter racing ahead of this call
    /// between the (not cheap) liveness check and the delete — substituting the file with a live
    /// lock a faster waiter has already won — without an actual multi-thread race. Never passed
    /// outside tests; <see langword="null"/> (the default) makes this a no-op.
    /// </param>
    internal static CardLockResult Acquire(
        string cardPath,
        TimeSpan timeout,
        Action<string>? testOnlyAfterWriteHook = null,
        Action<string>? testOnlyBeforeStaleDeleteHook = null)
    {
        var lockPath = cardPath + ".lock";
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            if (TryCreate(lockPath, out var ownContent, testOnlyAfterWriteHook))
            {
                return new CardLockResult.Acquired(new CardLock(lockPath, cardPath, ownContent));
            }

            // Deadline and sleep are unconditional on every iteration, independent of whether
            // this pass broke a stale lock — a run of iterations that keeps finding (and
            // clearing) an apparently-stale lock must still respect the caller's timeout rather
            // than looping past it. ADR-0003 makes the timeout load-bearing: a crashed agent must
            // not leave a card unwritable forever, but a caller waiting on this one must not wait
            // longer than it asked to either.
            TryBreakStaleLock(lockPath, testOnlyBeforeStaleDeleteHook);

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new CardLockResult.TimedOut(
                    cardPath,
                    $"timed out after {timeout.TotalSeconds:0.###}s waiting for the lock on '{cardPath}'; " +
                    $"currently held by {DescribeHolder(lockPath)}.");
            }

            // Jittered, not a fixed interval: a fixed delay lets every losing contender wake up
            // and retry in lockstep, so every attempt after the first collides again — jitter
            // desynchronises them, which matters once contention is more than a couple of
            // waiters deep.
            Thread.Sleep(BaseRetryDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 20)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Compare-and-delete, not delete-by-path: releasing must mean "unlink the file this
        // instance itself created", not "unlink whatever currently sits at this path". Under the
        // empty-lock grace window (TryBreakOrphanedEmptyLock), a stalled TryCreate can lose
        // ownership of _lockPath to a second contender before this instance ever gets here — if
        // Dispose deleted blind, it would release that contender's live lock out from under it,
        // letting a third contender acquire while the second still believes it holds the card.
        // Comparing content first — including the per-acquisition nonce, so two locks that
        // happen to share a pid (same process, different threads) are still told apart —
        // catches that case: a mismatch means this instance's lock was already reclaimed, so
        // there is nothing of this instance's left to release.
        //
        // Residual, stated rather than hidden: a read-then-unlink is still two file operations,
        // not one atomic one, so this narrows the window rather than closing it — a contender
        // could win TryCreate at this exact path in the gap between the read below and the
        // delete. .NET exposes no compare-and-delete primitive on this platform to close that
        // gap outright; doing so would mean the same unproven raw-syscall route already rejected
        // for the create-only rename above.
        try
        {
            if (File.ReadAllText(_lockPath) == _ownContent)
            {
                File.Delete(_lockPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort release. Covers both "already gone" (nothing to release) and "cannot be
            // deleted" (stays as a stale entry for the next acquirer's staleness check to clear
            // once this process's PID is no longer running) — neither wedges the card
            // permanently.
        }
    }

    private static bool TryCreate(string lockPath, out string content, Action<string>? testOnlyAfterWriteHook = null)
    {
        // A cheap existence check before the exception-throwing FileMode.CreateNew path: under
        // real contention almost every attempt finds the lock still held, and letting each one
        // throw+catch an IOException to discover that is needless overhead on the hot path.
        // File.Exists is racy (TOCTOU) but that is fine: it only ever produces a false negative
        // that falls through to the real atomic CreateNew below, never a false positive that
        // skips it, so acquisition safety is unaffected either way.
        if (File.Exists(lockPath))
        {
            content = string.Empty;
            return false;
        }

        // Pid plus a per-acquisition nonce, not pid alone: the nonce is what makes Dispose's
        // compare-and-delete unambiguous even when two CardLock instances in the same process
        // (different threads, different cards' locks racing) would otherwise share a pid — a
        // bare pid comparison could not tell those two apart.
        content = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n" + Guid.NewGuid().ToString("N");

        try
        {
            // FileShare.Read, not FileShare.None: mutual exclusion comes entirely from
            // FileMode.CreateNew (only one caller's create can ever succeed for a given path) —
            // FileShare.None adds nothing to that safety property and, on this platform, costs a
            // second advisory-lock step that a concurrent racer can fail after the create has
            // already happened (see the type's doc comment). Read access is still fine to share:
            // TryReadHolderPid/DescribeHolder read this same file while it may be held.
            using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        testOnlyAfterWriteHook?.Invoke(lockPath);

        // Self-verify, not trust: CreateNew succeeding and the write call returning without
        // throwing only means this call's own descriptor accepted the bytes — under a scheduler
        // stall spanning the empty-lock grace window (see the type's doc comment), a second
        // contender can legitimately break the still-zero-byte file and create its own live lock
        // at this same path before this write ever flushes; POSIX keeps this call's file handle
        // valid past that unlink, so the write above still "succeeds", landing harmlessly in the
        // now-detached inode while the path itself belongs to the second contender. Re-reading the
        // path and comparing against what was just written is what tells the two cases apart: a
        // match means this call still owns the path it created; a mismatch (including the file
        // being gone entirely) means it lost the race after already winning CreateNew, and that is
        // reported as an ordinary lost race — `false`, for Acquire's retry loop to go around again —
        // never as an exception or a trusted `true`.
        try
        {
            return File.ReadAllText(lockPath) == content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryBreakStaleLock(string lockPath, Action<string>? testOnlyBeforeStaleDeleteHook = null)
    {
        if (!TryReadLockContent(lockPath, out var content) || !TryParsePid(content, out var pid))
        {
            // Content that fails to parse is either a live holder mid-write of a real pid, or an
            // orphaned zero-byte file left by a holder killed before it ever wrote one — those two
            // cases are distinguished by age, not guessed at together. See
            // TryBreakOrphanedEmptyLock's doc comment.
            return TryBreakOrphanedEmptyLock(lockPath);
        }

        if (IsProcessAlive(pid))
        {
            return false;
        }

        testOnlyBeforeStaleDeleteHook?.Invoke(lockPath);

        try
        {
            // Compare-then-delete against the exact content just judged dead, not delete-by-path:
            // Process.GetProcessById above is not cheap, and by the time it returns, another
            // waiter can have raced through this same branch ahead of us, deleted this same dead
            // holder's file, won CreateNew, written, and self-verified — a live lock now sits at
            // lockPath that belongs to that waiter, not to the dead pid we just judged. A blind
            // File.Delete here would release that waiter's live lock out from under it, letting a
            // third contender acquire while the second still believes it holds the card — the
            // same "two writers on one card" shape Dispose's and TryBreakOrphanedEmptyLock's own
            // compare-then-delete already exist to prevent, reached here via the one ownership
            // site that used to delete on the strength of a read that was, by now, several file
            // and process operations old.
            if (File.ReadAllText(lockPath) != content)
            {
                return false;
            }

            File.Delete(lockPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Someone else already cleared it, or is racing us to — either way, loop and retry
            // the create rather than assuming which.
            return true;
        }
    }

    /// <summary>
    /// A lock file's content is only ever written after <see cref="FileMode.CreateNew"/> already
    /// succeeded, so an unparseable file is either (a) a live holder that has just created the
    /// file and has not yet written its pid — a window measured at low single-digit milliseconds
    /// on this platform — or (b) a holder killed inside that same window, which will never write
    /// it and, absent this check, would wedge the card until a human deletes the file by hand.
    /// Only a genuinely <b>zero-byte</b> file is treated this way: non-empty-but-unparseable
    /// content (garbage, a write truncated partway through a real pid) stays "never guessed at" —
    /// that really could be a live holder mid-write, and guessing wrong there risks two callers
    /// both believing they hold the lock. A zero-byte file carries no such ambiguity about
    /// content, only about age, which <see cref="EmptyLockGraceWindow"/> resolves.
    /// </summary>
    private static bool TryBreakOrphanedEmptyLock(string lockPath)
    {
        DateTimeOffset lastWriteUtc;

        try
        {
            var info = new FileInfo(lockPath);
            if (!info.Exists || info.Length != 0)
            {
                return false;
            }

            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - lastWriteUtc < EmptyLockGraceWindow)
        {
            // Still inside the window a live holder's create-then-write could plausibly occupy —
            // do not guess yet; the ordinary timeout/retry loop will reach this file again.
            return false;
        }

        try
        {
            // Re-check immediately before deleting, not delete on the strength of the check
            // above: the two are separated by the age comparison and every prior caller's own
            // work, which is plenty of time for the stalled holder this method is about to break
            // to finally flush its buffered write (see CardLock.cs:176-179's doc comment) and
            // turn this file from a genuine orphan into a live, non-empty lock. This does not
            // make the delete atomic — a write can still land in the gap between this re-check
            // and File.Delete itself — but it narrows the window from "since the age check" to
            // "since this line", which is the same compare-and-delete discipline Dispose applies
            // on release.
            var recheck = new FileInfo(lockPath);
            if (!recheck.Exists || recheck.Length != 0)
            {
                return false;
            }

            File.Delete(lockPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryReadHolderPid(string lockPath, out int pid)
    {
        if (TryReadLockContent(lockPath, out var content))
        {
            return TryParsePid(content, out pid);
        }

        pid = 0;
        return false;
    }

    private static bool TryReadLockContent(string lockPath, out string content)
    {
        try
        {
            content = File.ReadAllText(lockPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Content is "pid\nnonce" for a lock this build created, but a lock written before this
    /// nonce existed (or fabricated by a test) is still just a bare pid — reading only the first
    /// line handles both without caring which shape produced it.
    /// </summary>
    private static bool TryParsePid(string content, out int pid)
    {
        var firstLine = content.Split('\n', 2)[0].Trim();
        return int.TryParse(firstLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string DescribeHolder(string lockPath)
    {
        if (!TryReadHolderPid(lockPath, out var pid))
        {
            return "an unreadable holder";
        }

        try
        {
            var since = File.GetLastWriteTimeUtc(lockPath).ToString("O", CultureInfo.InvariantCulture);
            return $"pid {pid.ToString(CultureInfo.InvariantCulture)} (locked since {since} UTC)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"pid {pid.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}
