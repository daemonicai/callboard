using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardStoreWriteTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-store-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardStoreWriteTests()
    {
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
    public void WriteCard_CreatesAReadableFile()
    {
        var path = Path.Combine(_directory, "b-0001.md");
        var card = SampleCard("B-0001");

        var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5), ChangeName);

        AssertSuccess(result);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(card.Frontmatter, read.Frontmatter);
    }

    [Fact]
    public void WriteCard_LeavesNoTempFileBehindOnSuccess()
    {
        var path = Path.Combine(_directory, "b-0002.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0002"), TimeSpan.FromSeconds(5), ChangeName));

        var entries = Directory.GetFiles(_directory);
        Assert.Equal([path], entries);
    }

    [Fact]
    public void WriteCard_CreatesTheContainingDirectory_WhenItDoesNotYetExist()
    {
        // A fresh change name, not the one the constructor already created — its scope directory
        // must not exist on disk yet for this to actually exercise directory creation.
        const string freshChangeName = "not-yet-created-change";
        var directory = Path.Combine(_root, CardLayout.ChangesDirectory(freshChangeName).Replace('/', Path.DirectorySeparatorChar));
        var path = Path.Combine(directory, "b-0003.md");
        Assert.False(Directory.Exists(directory));

        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0003"), TimeSpan.FromSeconds(5), freshChangeName));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AppendComment_Repeatedly_NeverExposesAPartiallyWrittenFileToAConcurrentReader()
    {
        // Repeated whole-file rewrites through AtomicWrite, exercised via AppendComment rather
        // than WriteCard — WriteCard is create-only (DEVLOG §4 block C review round 1) and cannot
        // be called twice on the same path, but every append is its own full read-modify-write
        // through the same AtomicWrite primitive, so the atomicity claim under test is exercised
        // identically.
        var path = Path.Combine(_directory, "b-0004.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0004"), TimeSpan.FromSeconds(5), ChangeName));

        var readerFailures = new List<string>();
        var stop = false;

        var reader = Task.Run(
            () =>
            {
                while (!Volatile.Read(ref stop))
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var result = CardStore.ReadCard(path);
                    result.Match<object?>(
                        onSuccess: static _ => null,
                        onFailure: failure =>
                        {
                            lock (readerFailures)
                            {
                                readerFailures.Add(failure.Reason);
                            }

                            return null;
                        });
                }
            },
            TestContext.Current.CancellationToken);

        for (var i = 0; i < 50; i++)
        {
            var comment = new CardComment(
                $"C-{i:D3}", CardOwner.Worker, Created, new string((char)('a' + (i % 26)), 2_000), null, null, null, []);
            AssertSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName));
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Empty(readerFailures);
    }

    [Fact]
    public void AppendComment_AddsToAnExistingCard()
    {
        var path = Path.Combine(_directory, "b-0005.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0005"), TimeSpan.FromSeconds(5), ChangeName));

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);
        AssertSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    [Fact]
    public void AppendComment_PreservesAnUnrecognisedFrontmatterField_ThatWasAlreadyOnDisk()
    {
        var path = Path.Combine(_directory, "b-0006.md");

        // Written directly, bypassing CardFile/CardStore, standing in for a later section's field
        // a newer build (or a human) already added to this card — this build's own schema does
        // not model "future-field", which is exactly the case this test asserts AppendComment
        // must not silently destroy. (§5's own five block-only fields — base, reviewed_state,
        // tasks, round, blocked_by — are now known fields of a block card, so this test uses a
        // genuinely unmodelled key rather than one of them.)
        var raw =
            "---\n" +
            "id: B-0006\nkind: block\ntitle: Title\nstatus: open\nowner: worker\nscope: change\nsection: 2\n" +
            "created: 2026-08-20T09:00:00+00:00\nupdated: 2026-08-20T09:00:00+00:00\n" +
            "future-field: B-0001\n" +
            "---\n" +
            "Body.\n";
        File.WriteAllText(path, raw, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);
        AssertSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(("future-field", "B-0001"), Assert.Single(read.UnknownFrontmatterFields));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    [Fact]
    public void WriteCard_RefusesAChangeScopedCard_WhenNoChangeNameIsSupplied()
    {
        // ValidateAgainstLayout resolves the expected directory via CardLayout.DirectoryFor,
        // which throws ArgumentException for a Change-scoped card with no change name; the guard
        // catches that and turns it into a CardWriteResult.Failure rather than letting it escape
        // as an unhandled exception. Exercised at the CardStore boundary, not just on CardLayout
        // in isolation, since that catch-and-convert is exactly what this block wired up.
        var path = Path.Combine(_directory, "b-0099.md");

        var result = CardStore.WriteCard(_root, path, SampleCard("B-0099"), TimeSpan.FromSeconds(5), changeName: null);

        AssertFailure(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteCard_RefusesWhenTheFilePathDoesNotLiveInTheScopesLayoutDirectory()
    {
        // A Repository-scoped card belongs in callboard/register/; writing it under the
        // change-scoped directory the constructor already created is a genuine layout mismatch,
        // not a lookalike — this proves the guard refuses the ordinary wrong-directory case
        // before the more adversarial suffix-collision case below.
        var path = Path.Combine(_directory, "r-0001.md");
        var card = RepositoryScopedCard("R-0001");

        var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteCard_RefusesADirectoryThatMerelyEndsWithTheExpectedDirectorysCharacters()
    {
        // "evilcallboard/register/" ends with the same characters as the expected
        // "callboard/register/" — a raw string-suffix match would wrongly accept this. The guard
        // has to compare whole path segments, not trailing characters, to refuse it.
        var evilDirectory = Path.Combine(_root, "evilcallboard", "register");
        Directory.CreateDirectory(evilDirectory);
        var path = Path.Combine(evilDirectory, "r-0002.md");
        var card = RepositoryScopedCard("R-0002");

        var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteCard_RefusesACorrectlyShapedTail_UnderTheWrongRepositoryRoot()
    {
        // O-1: the pre-4.5 guard compared only CardLayout.DirectoryFor's relative, trailing
        // segments — a directory that genuinely ends "callboard/register/" passed regardless of
        // which repository root it actually lived under. This card's directory is shaped exactly
        // right; what makes it wrong is that it sits under a different root than the one this
        // call declares.
        var wrongRoot = Path.Combine(Path.GetTempPath(), "callboard-wrong-root-" + Guid.NewGuid().ToString("N"));
        var correctlyShapedDirectory = Path.Combine(wrongRoot, "callboard", "register");
        Directory.CreateDirectory(correctlyShapedDirectory);
        var path = Path.Combine(correctlyShapedDirectory, "r-0004.md");
        var card = RepositoryScopedCard("R-0004");

        try
        {
            var result = CardStore.WriteCard(_root, path, card, TimeSpan.FromSeconds(5));

            var failure = AssertFailure(result);
            Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
            Assert.Contains(_root, failure, StringComparison.Ordinal);
            Assert.False(File.Exists(path));

            // Confirms the directory really was shaped correctly and it was the root that made
            // the difference: the identical path succeeds once cardsRoot names its true root.
            AssertSuccess(CardStore.WriteCard(wrongRoot, path, card, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (Directory.Exists(wrongRoot))
            {
                Directory.Delete(wrongRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AppendComment_RefusesWhenTheFilePathDoesNotLiveInTheDirectoryTheCardsOwnScopeRequires()
    {
        // Write a Repository-scoped card legitimately (in its real directory), then try to append
        // to it from a path outside that directory. AppendComment resolves the expected directory
        // from the card's own on-disk scope, so this must refuse just as WriteCard does.
        var registerDirectory = Path.Combine(_root, "callboard", "register");
        Directory.CreateDirectory(registerDirectory);
        var realPath = Path.Combine(registerDirectory, "r-0003.md");
        AssertSuccess(CardStore.WriteCard(_root, realPath, RepositoryScopedCard("R-0003"), TimeSpan.FromSeconds(5)));

        var wrongPath = Path.Combine(_directory, "r-0003.md");
        File.Copy(realPath, wrongPath);

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);
        var result = CardStore.AppendComment(_root, wrongPath, comment, TimeSpan.FromSeconds(5));

        var failure = AssertFailure(result);
        Assert.Contains("does not live in the directory", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendComment_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);

        var result = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var failure = AssertFailure(result);
        Assert.Contains(path, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendComment_WhenTheCardFileIsCorrupt_FailsWithoutTouchingTheFile()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);
        var result = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        AssertFailure(result);
        Assert.Equal("not a card file at all", File.ReadAllText(path));
    }

    // process-enforcement (§9 block A3): CardWriteResult's shared "act on that card" bound
    // (work-lifecycle 8a.17) applies to the generic AppendComment surface too — this bound is not
    // scoped to the round-incrementing edges themselves — and the refusal is card-addressed, so it
    // records against the card.
    [Fact]
    public void AppendComment_BlockCardWithDisagreeingRound_Refuses_AndRecordsAgainstTheCard()
    {
        var path = Path.Combine(_directory, "b-0010.md");
        var frontmatter = new CardFrontmatter("B-0010", CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "2", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: 4, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var comment = new CardComment("C-0001", CardOwner.Worker, Created.AddHours(1), "Done.", null, null, null, []);

        var outcome = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardWriteResult.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(4, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(Created.AddHours(1), refusal.Timestamp);
        Assert.Equal(disagreement.RefusingRule, refusal.Rule);
        Assert.Equal(disagreement.Remedy, refusal.Remedy);
    }

    // working-context (§10 block C): "No figure SHALL be hand-entered anywhere in the system" —
    // a card whose frontmatter already carries a reserved derived-state key (only reachable by a
    // hand edit made outside the tool; this build's own parser never assigns 'next_step' a typed
    // home) is refused before AppendComment's write proceeds, and the refusal is card-addressed,
    // so it records against the card.
    [Fact]
    public void AppendComment_CardCarryingAReservedDerivedStateKey_Refuses_AndRecordsAgainstTheCard()
    {
        var path = Path.Combine(_directory, "b-0011.md");
        var frontmatter = new CardFrontmatter("B-0011", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [("next_step", "do the thing")]);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var comment = new CardComment("C-0001", CardOwner.Worker, Created.AddHours(1), "Done.", null, null, null, []);

        var outcome = CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName);

        var handEntered = Assert.IsType<CardWriteResult.HandEnteredDerivedState>(outcome);
        Assert.Equal("next_step", handEntered.Key);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Comments);
        Assert.Equal(("next_step", "do the thing"), Assert.Single(read.UnknownFrontmatterFields));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(Created.AddHours(1), refusal.Timestamp);
        Assert.Equal(handEntered.RefusingRule, refusal.Rule);
        Assert.Equal(handEntered.Remedy, refusal.Remedy);
    }

    [Fact]
    public void AppendCommentUnderExistingLock_RequiresAHeldLock_NullBypassIsRejectedAtRuntime()
    {
        // O-2: the parameter's own non-nullable type is the compile-time half of "you must hold
        // a lock to call this" — writing the mistake (calling with no CardLock at all) is exactly
        // the CS7036 "there is no argument given" this signature now forces, which this test
        // cannot itself express as a passing xUnit case since it would fail to compile at all.
        // What a nullable-reference-types signature cannot stop is a caller defeating the compile
        // hint with `null!`; ArgumentNullException.ThrowIfNull is what closes that residual gap,
        // asserted here directly.
        var path = Path.Combine(_directory, "b-0007.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0007"), TimeSpan.FromSeconds(5), ChangeName));
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);

        Assert.Throws<ArgumentNullException>(() =>
            CardStore.AppendCommentUnderExistingLock(null!, _root, comment, ChangeName));
    }

    [Fact]
    public void TransferOwnershipUnderExistingLock_RequiresAHeldLock_NullBypassIsRejectedAtRuntime()
    {
        var path = Path.Combine(_directory, "b-0008.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0008"), TimeSpan.FromSeconds(5), ChangeName));

        Assert.Throws<ArgumentNullException>(() =>
            CardStore.TransferOwnershipUnderExistingLock(null!, _root, CardOwner.Reviewer, CardOwner.Architect, Created, ChangeName));
    }

    [Fact]
    public void AppendCommentUnderExistingLock_ActsOnlyOnTheLockedCard_ThereIsNoSeparatePathToDisagreeWith()
    {
        // Reviewer round 1, finding 1: the first shape took CardLock heldLock *and* a separate
        // filePath, so a lock held for card X and a filePath naming card Y both compiled — "lock
        // X, write Y" ran clean. The fix removed the second parameter entirely; the target is
        // heldLock.CardPath, so the mismatched-path probe the reviewer wrote by hand can no longer
        // even be expressed as a call — there is nothing left to pass a mismatched path as. This
        // test is the positive half: append under a lock acquired for `path` lands on `path`.
        var path = Path.Combine(_directory, "b-0009.md");
        AssertSuccess(CardStore.WriteCard(_root, path, SampleCard("B-0009"), TimeSpan.FromSeconds(5), ChangeName));
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Done.", null, null, null, []);

        var lockResult = CardLock.Acquire(path, TimeSpan.FromSeconds(5));
        var held = lockResult.Match(
            onAcquired: acquired => acquired.Lock,
            onTimedOut: timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

        try
        {
            Assert.Equal(path, held.CardPath);
            AssertSuccess(CardStore.AppendCommentUnderExistingLock(held, _root, comment, ChangeName));
        }
        finally
        {
            held.Dispose();
        }

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(comment, Assert.Single(read.Comments));
    }

    private static NewCardFile SampleCard(string id, string body = "Body.") =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Created),
            body);

    private static NewCardFile RepositoryScopedCard(string id) =>
        new(
            new CardFrontmatter(id, CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Repository, string.Empty, Created, Created),
            "Body.");

    private static void AssertSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected write success, got HandEnteredDerivedState: '{handEntered.Key}'"));

    private static string AssertFailure(CardWriteResult result) =>
        result.Match(
            onSuccess: static _ => throw new Xunit.Sdk.XunitException("expected write failure, got success."),
            onNotFound: notFound => $"no card file exists at '{notFound.FilePath}'.",
            onAlreadyExists: alreadyExists => $"a card already exists at '{alreadyExists.FilePath}'.",
            onLayoutMismatch: layoutMismatch => layoutMismatch.Reason,
            onCorrupt: corrupt => $"the card file is corrupt: {corrupt.Reason}",
            onToolFailure: toolFailure => toolFailure.Reason,
            onRoundDisagreesWithHistory: disagreement => $"stored round {disagreement.StoredRound}, but history implies round {disagreement.ExpectedRound}.",
            onHandEnteredDerivedState: handEntered => $"hand-entered derived-state field '{handEntered.Key}'.");

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
