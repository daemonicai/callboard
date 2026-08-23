using System.Text;
using Callboard.Cards;
using Callboard.Index;
using Microsoft.Data.Sqlite;

namespace Callboard.Tests;

/// <summary>
/// 7.3/7.4 — <see cref="CardStore.ArchiveChange"/>: archive as a directory-level filter with
/// nothing in transit. Register: "Repository-scoped cards SHALL belong to the repository and
/// SHALL NOT be owned by any change. Archiving a change SHALL act as a filter that closes its
/// change-scoped cards and leaves cards of wider scope untouched."
///
/// <para>
/// <b>"Unmoved" is proven on the bytes, not on readability</b> (the block D brief's own binding
/// item 2): every repository-scoped assertion below compares the exact byte content and the
/// filesystem's own <see cref="File.GetLastWriteTimeUtc(string)"/> before and after the call — a
/// rewrite that happened to reproduce identical content would still move
/// <see cref="File.GetLastWriteTimeUtc(string)"/> forward, and the absence of a <c>.lock</c> file
/// afterwards is checked too, since <see cref="CardStore"/> never writes a card without first
/// acquiring its lock.
/// </para>
/// </summary>
public sealed class CardChangeArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ArchivedAt = Created.AddDays(3);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-change-archive-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;
    private readonly string _registerDirectory;

    public CardChangeArchiveTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
        Directory.CreateDirectory(_registerDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ArchiveChange_SettlesEveryOpenObligation_AndLeavesOtherChangeScopedCardsUntouched()
    {
        var obligationPath = WriteObligation("o-0001", "O-0001", RegisterLifecycleState.Open);
        var blockPath = WriteBlockCard("b-0001", "B-0001");
        var blockBytesBefore = File.ReadAllBytes(blockPath);

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var archived = AssertArchived(outcome);
        Assert.Equal("O-0001", Assert.Single(archived.SettledObligationIds));
        Assert.False(Directory.Exists(_changeDirectory));
        Assert.True(Directory.Exists(archived.ArchivedDirectory));

        var archivedObligationPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(obligationPath));
        var obligationCard = AssertParseSuccess(CardStore.ReadCard(archivedObligationPath));
        Assert.Equal("discharged", obligationCard.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, obligationCard.RegisterFields.DischargedBy);
        Assert.Equal(ArchivedAt, obligationCard.RegisterFields.DischargedAt);

        // The block card moved (it is inside the archived change directory now) but was never
        // rewritten: same bytes, unlike the obligation this call deliberately settled.
        var archivedBlockPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(blockPath));
        Assert.Equal(blockBytesBefore, File.ReadAllBytes(archivedBlockPath));
    }

    [Fact]
    public void ArchiveChange_RepositoryScopedRule_NeverOpened_ProvenOnTheBytesAndTheModificationTime()
    {
        WriteObligation("o-0002", "O-0002", RegisterLifecycleState.Open);
        var rulePath = WriteRegisterCard("r-0001", "R-0001", CardKind.Rule);
        var bytesBefore = File.ReadAllBytes(rulePath);
        var writeTimeBefore = File.GetLastWriteTimeUtc(rulePath);

        AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));

        Assert.Equal(bytesBefore, File.ReadAllBytes(rulePath));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(rulePath));
        Assert.False(File.Exists(rulePath + ".lock"));
    }

    [Fact]
    public void ArchiveChange_RepositoryScopedHazard_NeverOpened_ProvenOnTheBytesAndTheModificationTime()
    {
        var hazardPath = WriteRegisterCard("h-0001", "H-0001", CardKind.Hazard);
        var bytesBefore = File.ReadAllBytes(hazardPath);
        var writeTimeBefore = File.GetLastWriteTimeUtc(hazardPath);

        AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));

        Assert.Equal(bytesBefore, File.ReadAllBytes(hazardPath));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(hazardPath));
        Assert.False(File.Exists(hazardPath + ".lock"));
    }

    // register: "Scenario: Question outlives its change" — question is already repository-scoped
    // (CardScopeRules), so this falls out of the layout the same way rule/hazard do; nothing in
    // ArchiveChange treats a question specially.
    [Fact]
    public void ArchiveChange_OpenQuestion_OutlivesItsChange_UnmovedAndUnmodified()
    {
        var questionPath = WriteRegisterCard("q-0001", "Q-0001", CardKind.Question);
        var bytesBefore = File.ReadAllBytes(questionPath);
        var writeTimeBefore = File.GetLastWriteTimeUtc(questionPath);

        AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));

        Assert.True(File.Exists(questionPath));
        Assert.Equal(bytesBefore, File.ReadAllBytes(questionPath));
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(questionPath));
    }

    // Block B's resolver, end to end (block D brief item 4): a card resolves by id while its
    // change is live, the change archives, and the same id still resolves — without ArchiveChange
    // ever rewriting the card to keep it findable.
    [Fact]
    public void ArchiveChange_CardIdentityStillResolves_ByIdAfterArchive()
    {
        var rulePath = WriteChangeScopedRule("r-0002", "R-0002");

        AssertFound(CardIdentityResolver.Resolve(_root, "R-0002"));

        AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));

        var foundFilePath = AssertFound(CardIdentityResolver.Resolve(_root, "R-0002"));
        Assert.StartsWith(CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), Path.GetRelativePath(_root, foundFilePath), StringComparison.Ordinal);
        Assert.False(File.Exists(rulePath), "the live-directory copy must be gone — archive is a move, not a copy.");
    }

    // Block D brief item 4's second half: index rebuild still populates from an archived change.
    [Fact]
    public void ArchiveChange_IndexRebuild_StillPopulatesTheArchivedChangesCards()
    {
        WriteBlockCard("b-0002", "B-0002");
        AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));

        var databasePath = IndexPaths.DatabasePath(_root);
        var result = IndexPopulator.Populate(_root, databasePath);

        Assert.Equal(1, result.IndexedCardCount);
        Assert.Empty(result.Failures);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM cards WHERE id = 'B-0002';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
    }

    [Fact]
    public void ArchiveChange_NoLiveChangeDirectory_RefusesAsChangeNotFound()
    {
        var outcome = CardStore.ArchiveChange(_root, "never-existed", CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var notFound = Assert.IsType<ChangeArchiveOutcome.ChangeNotFound>(outcome);
        Assert.Equal("never-existed", notFound.ChangeName);
    }

    [Fact]
    public void ArchiveChange_AlreadyArchived_Refuses_AndDoesNotTouchTheArchivedCopy()
    {
        var blockPath = WriteBlockCard("b-0003", "B-0003");
        var archived = AssertArchived(CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5)));
        var archivedBlockPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(blockPath));
        var bytesBefore = File.ReadAllBytes(archivedBlockPath);

        // Re-create a live directory under the same name — a second attempt to archive it must
        // refuse, not merge into or overwrite the archive already there.
        Directory.CreateDirectory(_changeDirectory);
        var second = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt.AddDays(1), TimeSpan.FromSeconds(5));

        var alreadyArchived = Assert.IsType<ChangeArchiveOutcome.AlreadyArchived>(second);
        Assert.Equal(ChangeName, alreadyArchived.ChangeName);
        Assert.Equal(bytesBefore, File.ReadAllBytes(archivedBlockPath));
    }

    [Fact]
    public void ArchiveChange_ReservedNameArchive_RefusesAsInvalidChangeName()
    {
        var outcome = CardStore.ArchiveChange(_root, "archive", CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var invalid = Assert.IsType<ChangeArchiveOutcome.InvalidChangeName>(outcome);
        Assert.Contains("reserved", invalid.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchiveChange_UnreadableCardInTheDirectory_RefusesWithoutMovingAnything()
    {
        WriteBlockCard("b-0004", "B-0004");
        File.WriteAllText(Path.Combine(_changeDirectory, "corrupt.md"), "not a card file at all");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var unreadable = Assert.IsType<ChangeArchiveOutcome.CardsUnreadable>(outcome);
        Assert.Contains(unreadable.FilePaths, path => path.EndsWith("corrupt.md", StringComparison.Ordinal));
        Assert.True(Directory.Exists(_changeDirectory), "an unreadable card must refuse before the directory move, not after.");
    }

    // Atomicity: phase one (settling obligations) stops the instant one settle fails, and the
    // directory is never moved — a lock held by another caller on the one open obligation must
    // leave the change fully live, not half-settled-half-moved.
    [Fact]
    public void ArchiveChange_LockTimeoutSettlingAnObligation_LeavesTheChangeFullyLive_NotHalfMoved()
    {
        var obligationPath = WriteObligation("o-0003", "O-0003", RegisterLifecycleState.Open);
        WriteBlockCard("b-0005", "B-0005");
        var holder = AssertAcquired(CardLock.Acquire(obligationPath, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromMilliseconds(200));

            Assert.IsType<ChangeArchiveOutcome.ToolFailure>(outcome);
            Assert.True(Directory.Exists(_changeDirectory), "the live directory must still exist — nothing moved.");
            Assert.False(Directory.Exists(Path.Combine(_root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar), ChangeName)));

            var stillOpen = AssertParseSuccess(CardStore.ReadCard(obligationPath));
            Assert.Equal(RegisterLifecycleState.Open.ToWireString(), stillOpen.Frontmatter.Status);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // Reviewer's own reproduction (block D nit): phase two's Directory.Move can fail even
    // though the pre-check found nothing at the destination — a non-directory entry placed at the
    // exact target path passes Directory.Exists (false, since it's a file, not a directory) and
    // then makes Directory.Move itself throw. Settle-then-move means phase one has already run by
    // the time this happens: the obligation this test settles is discharged, not left open, which
    // is the real documented contract — not the idealised "nothing happened" shape the lock-timeout
    // test (phase one failing) proves instead.
    [Fact]
    public void ArchiveChange_PhaseTwoMoveFails_LeavesTheChangeLive_WithPhaseOnesSettlementAlreadyDurable()
    {
        var obligationPath = WriteObligation("o-0004", "O-0004", RegisterLifecycleState.Open);
        var blockPath = WriteBlockCard("b-0006", "B-0006");
        var blockBytesBefore = File.ReadAllBytes(blockPath);

        var archiveContainer = Path.Combine(_root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(archiveContainer);
        var sabotagePath = Path.Combine(archiveContainer, ChangeName);
        File.WriteAllText(sabotagePath, "not a directory — sabotages Directory.Move's destination.");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        Assert.IsType<ChangeArchiveOutcome.ToolFailure>(outcome);

        // The guarantee that matters: the record is not half-moved. The change directory is
        // exactly where it was, both its cards are still found there (not vanished, not
        // duplicated into a partially-created archive directory), and the sabotage file is
        // undisturbed — proving Directory.Move never got far enough to touch it.
        Assert.True(Directory.Exists(_changeDirectory), "the live directory must still exist — the move never completed.");
        Assert.True(File.Exists(obligationPath));
        Assert.True(File.Exists(blockPath));
        Assert.Equal(blockBytesBefore, File.ReadAllBytes(blockPath));
        Assert.False(Directory.Exists(sabotagePath), "the sabotage path must still be the plain file it was — Directory.Move must not have replaced or entered it.");
        Assert.Equal("not a directory — sabotages Directory.Move's destination.", File.ReadAllText(sabotagePath));

        // Settle-then-move's real contract, pinned exactly: phase one already ran and is durable
        // even though phase two failed. An idealised "nothing happened at all" assertion here
        // (status still "open") would be wrong and would mask a regression that skipped the move
        // guard entirely.
        var settled = AssertParseSuccess(CardStore.ReadCard(obligationPath));
        Assert.Equal("discharged", settled.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, settled.RegisterFields.DischargedBy);
        Assert.Equal(ArchivedAt, settled.RegisterFields.DischargedAt);

        // Discriminates against a regression that moved the directory anyway despite the throw
        // (e.g. a caught-and-swallowed exception after a partial native move): the id must still
        // resolve at the *live* path, never at an archived one.
        var found = AssertFound(CardIdentityResolver.Resolve(_root, "B-0006"));
        Assert.Equal(blockPath, found);
    }

    private string WriteObligation(string fileStem, string id, RegisterLifecycleState state)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Obligation, "Settle the migration", state.ToWireString(), CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var fields = new RegisterCardFields(null, null, null, null, OwedBy: "S-0001");
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteChangeScopedRule(string fileStem, string id)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteRegisterCard(string fileStem, string id, CardKind kind)
    {
        var path = Path.Combine(_registerDirectory, fileStem + ".md");
        var fields = kind == CardKind.Hazard
            ? new RegisterCardFields("The condition holds", "monthly", null, null)
            : RegisterCardFields.Empty;
        var frontmatter = new CardFrontmatter(
            id, kind, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteBlockCard(string fileStem, string id)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: BlockCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static ChangeArchiveOutcome.Archived AssertArchived(ChangeArchiveOutcome outcome) =>
        outcome.Match(
            onArchived: static archived => archived,
            onChangeNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Archived, got ChangeNotFound: '{notFound.ChangeName}'"),
            onAlreadyArchived: static already => throw new Xunit.Sdk.XunitException($"expected Archived, got AlreadyArchived: '{already.ChangeName}'"),
            onInvalidChangeName: static invalid => throw new Xunit.Sdk.XunitException($"expected Archived, got InvalidChangeName: {invalid.Reason}"),
            onCardsUnreadable: static unreadable => throw new Xunit.Sdk.XunitException($"expected Archived, got CardsUnreadable: {string.Join(", ", unreadable.FilePaths)}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Archived, got ToolFailure: {toolFailure.Reason}"));

    private static string AssertFound(CardIdentityResolution resolution) =>
        resolution.Match(
            onFound: static (filePath, _) => filePath,
            onNotFound: static id => throw new Xunit.Sdk.XunitException($"expected Found, got NotFound: '{id}'"),
            onDuplicate: static (id, paths) => throw new Xunit.Sdk.XunitException($"expected Found, got Duplicate('{id}'): {string.Join(", ", paths)}"),
            onUnreadable: static (id, paths) => throw new Xunit.Sdk.XunitException($"expected Found, got Unreadable('{id}'): {string.Join(", ", paths)}"));

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
