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
///
/// <para>
/// <b>§9 block F: archive no longer discharges anything of its own.</b> An earlier version of this
/// suite proved the opposite — that every open obligation in the directory was silently discharged
/// on the way to the move. process-enforcement's "Archive settles orphaned obligations" requirement
/// replaced that behaviour with a refusal, because discharge asserts the work was <em>met</em>, and
/// a gate whose only exit manufactures that assertion is worse than no gate. The tests below now
/// prove the three-way split this method actually makes: an obligation owed by a section still open
/// carries into the archive untouched (not orphaned — 9.4 already guards that section's own close);
/// an obligation owed by a closed section, or by no section card at all, refuses the whole archive
/// as <see cref="ChangeArchiveOutcome.OrphanedObligations"/>; and an obligation already discharged,
/// promoted or declined before archive time is not "open" and so is never examined at all.
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
    public void ArchiveChange_ObligationOwedByAStillOpenSection_CarriesIntoTheArchiveUntouched_AndLeavesOtherChangeScopedCardsUntouched()
    {
        WriteSectionCard("s-0001", "S-0001", closed: false);
        var obligationPath = WriteObligation("o-0001", "O-0001", RegisterLifecycleState.Open, owedBy: "S-0001");
        var obligationBytesBefore = File.ReadAllBytes(obligationPath);
        var blockPath = WriteBlockCard("b-0001", "B-0001");
        var blockBytesBefore = File.ReadAllBytes(blockPath);

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var archived = AssertArchived(outcome);
        Assert.False(Directory.Exists(_changeDirectory));
        Assert.True(Directory.Exists(archived.ArchivedDirectory));

        // Not orphaned (its section is still open) — moved exactly as written, same bytes, never
        // discharged: this is the "no carry-forward step" scenario register gives an open question,
        // applied here to an obligation instead.
        var archivedObligationPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(obligationPath));
        Assert.Equal(obligationBytesBefore, File.ReadAllBytes(archivedObligationPath));
        var obligationCard = AssertParseSuccess(CardStore.ReadCard(archivedObligationPath));
        Assert.Equal(RegisterLifecycleState.Open.ToWireString(), obligationCard.Frontmatter.Status);
        Assert.Null(obligationCard.RegisterFields.DischargedBy);
        Assert.Null(obligationCard.RegisterFields.DischargedAt);

        // The block card moved (it is inside the archived change directory now) but was never
        // rewritten: same bytes.
        var archivedBlockPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(blockPath));
        Assert.Equal(blockBytesBefore, File.ReadAllBytes(archivedBlockPath));
    }

    [Fact]
    public void ArchiveChange_OpenObligationOwedByAClosedSection_RefusesAsOrphaned_AndNamesTheThreeDispositions()
    {
        WriteSectionCard("s-0002", "S-0002", closed: true);
        WriteObligation("o-0005", "O-0005", RegisterLifecycleState.Open, owedBy: "S-0002");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var orphaned = AssertOrphanedObligations(outcome);
        Assert.Equal(ChangeName, orphaned.ChangeName);
        var obligation = Assert.Single(orphaned.Obligations);
        Assert.Equal("O-0005", obligation.Id);
        Assert.Contains("discharge", orphaned.Remedy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("promote", orphaned.Remedy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decline", orphaned.Remedy, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_changeDirectory), "a refused archive must not have moved anything.");
    }

    [Fact]
    public void ArchiveChange_OpenObligationOwedByNoSectionCardAtAll_RefusesAsOrphaned()
    {
        // No section card of id "S-9999" exists anywhere in the directory — "owed by no remaining
        // section" reads the same whether the section closed or never existed here at all.
        WriteObligation("o-0006", "O-0006", RegisterLifecycleState.Open, owedBy: "S-9999");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        var orphaned = AssertOrphanedObligations(outcome);
        Assert.Equal("O-0006", Assert.Single(orphaned.Obligations).Id);
    }

    [Fact]
    public void ArchiveChange_DischargedObligationOwedByAClosedSection_DoesNotBlockArchive()
    {
        // Only *open* obligations are ever examined — a discharged one owed by a closed section is
        // exactly the register's own "settled" case, not this gate's concern.
        WriteSectionCard("s-0003", "S-0003", closed: true);
        WriteObligation("o-0007", "O-0007", RegisterLifecycleState.Discharged, owedBy: "S-0003");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        AssertArchived(outcome);
    }

    [Fact]
    public void ArchiveChange_RepositoryScopedRule_NeverOpened_ProvenOnTheBytesAndTheModificationTime()
    {
        WriteSectionCard("s-0004", "S-0004", closed: false);
        WriteObligation("o-0002", "O-0002", RegisterLifecycleState.Open, owedBy: "S-0004");
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

    // §12 block A round two, item 2: "change archive (:4092) and state now agreeing about whether
    // the same obligation is owed." Before §12 block A's parse door, this obligation's own
    // hand-edited bad status made the two disagree — `ArchiveChange`'s :4092 `TryParse` failed
    // closed (not "Open", so not counted as owed; the archive would have proceeded) while `state`'s
    // route through `CardLifecycle.IsClosed` → `IsRegisterDischarged` failed open (not
    // `Discharged`, so treated as still live and owed) — the same obligation, two contradictory
    // answers, from the same command surface a caller would run one after the other. The parse door
    // removes the disagreement by removing the card before either site ever runs its own check: both
    // now see the same read failure, so neither can claim a disposition the other one contradicts.
    [Fact]
    public void ArchiveChange_ObligationWithAHandEditedBadStatus_AgreesWithState_NeitherClaimsItIsSettled()
    {
        WriteSectionCard("s-corrupt-obligation", "S-CORRUPT", closed: false);
        var obligationPath = Path.Combine(_changeDirectory, "o-corrupt.md");
        var frontmatter = new CardFrontmatter(
            "O-CORRUPT", CardKind.Obligation, "Settle the migration", "briefed", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var fields = new RegisterCardFields(null, null, null, null, OwedBy: "S-CORRUPT");
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(obligationPath, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Archive's side: refuses, does not silently treat the obligation as settled and move on.
        var archiveOutcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));
        var unreadable = Assert.IsType<ChangeArchiveOutcome.CardsUnreadable>(archiveOutcome);
        Assert.Contains(unreadable.FilePaths, path => string.Equals(path, obligationPath, StringComparison.Ordinal));
        Assert.True(Directory.Exists(_changeDirectory), "a card the archive cannot read must refuse before the move, not after.");

        // State's side: does not silently claim the same obligation is live/owed either — a card
        // that fails to parse is excluded from both dispositions, not asserted into one of them.
        var state = DerivedStateAssembler.Build(_root);
        Assert.DoesNotContain(state.LiveObligations, entry => entry.Card.Frontmatter.Id == "O-CORRUPT");
    }

    // §9 block F: ArchiveChange no longer writes to any obligation, so a lock held elsewhere on one
    // no longer blocks the archive at all — this is the direct behavioural consequence of removing
    // the old settle-then-move two-phase write, pinned as its own test rather than left implicit.
    [Fact]
    public void ArchiveChange_LockHeldOnAnOpenObligation_DoesNotBlockArchive_BecauseNothingIsWrittenToIt()
    {
        WriteSectionCard("s-0005", "S-0005", closed: false);
        var obligationPath = WriteObligation("o-0003", "O-0003", RegisterLifecycleState.Open, owedBy: "S-0005");
        WriteBlockCard("b-0005", "B-0005");
        var holder = AssertAcquired(CardLock.Acquire(obligationPath, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromMilliseconds(200));

            var archived = AssertArchived(outcome);
            var archivedObligationPath = Path.Combine(archived.ArchivedDirectory, Path.GetFileName(obligationPath));
            var stillOpen = AssertParseSuccess(CardStore.ReadCard(archivedObligationPath));
            Assert.Equal(RegisterLifecycleState.Open.ToWireString(), stillOpen.Frontmatter.Status);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // Atomicity: the directory move either lands whole or throws having moved nothing — a
    // non-directory entry sabotaging the destination must leave the live directory exactly as it
    // was, with nothing rewritten (§9 block F: there is no longer a settled-then-move split to
    // discriminate against, since this method makes no write of its own before the move).
    [Fact]
    public void ArchiveChange_DirectoryMoveFails_LeavesTheChangeLive_WithNothingWritten()
    {
        WriteSectionCard("s-0006", "S-0006", closed: false);
        var obligationPath = WriteObligation("o-0004", "O-0004", RegisterLifecycleState.Open, owedBy: "S-0006");
        var blockPath = WriteBlockCard("b-0006", "B-0006");
        var blockBytesBefore = File.ReadAllBytes(blockPath);
        var obligationBytesBefore = File.ReadAllBytes(obligationPath);

        var archiveContainer = Path.Combine(_root, CardLayout.ArchiveDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(archiveContainer);
        var sabotagePath = Path.Combine(archiveContainer, ChangeName);
        File.WriteAllText(sabotagePath, "not a directory — sabotages Directory.Move's destination.");

        var outcome = CardStore.ArchiveChange(_root, ChangeName, CardOwner.Architect, ArchivedAt, TimeSpan.FromSeconds(5));

        Assert.IsType<ChangeArchiveOutcome.ToolFailure>(outcome);

        // The guarantee that matters: the record is not half-moved, and nothing was written —
        // both cards are exactly as they were.
        Assert.True(Directory.Exists(_changeDirectory), "the live directory must still exist — the move never completed.");
        Assert.True(File.Exists(obligationPath));
        Assert.True(File.Exists(blockPath));
        Assert.Equal(blockBytesBefore, File.ReadAllBytes(blockPath));
        Assert.Equal(obligationBytesBefore, File.ReadAllBytes(obligationPath));
        Assert.False(Directory.Exists(sabotagePath), "the sabotage path must still be the plain file it was — Directory.Move must not have replaced or entered it.");
        Assert.Equal("not a directory — sabotages Directory.Move's destination.", File.ReadAllText(sabotagePath));

        // Discriminates against a regression that moved the directory anyway despite the throw
        // (e.g. a caught-and-swallowed exception after a partial native move): the id must still
        // resolve at the *live* path, never at an archived one.
        var found = AssertFound(CardIdentityResolver.Resolve(_root, "B-0006"));
        Assert.Equal(blockPath, found);
    }

    private string WriteObligation(string fileStem, string id, RegisterLifecycleState state, string owedBy = "S-0001")
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Obligation, "Settle the migration", state.ToWireString(), CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var fields = new RegisterCardFields(null, null, null, null, OwedBy: owedBy);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteSectionCard(string fileStem, string id, bool closed)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var sectionFields = new SectionCardFields(
            Base: null,
            ClosedBy: closed ? CardOwner.Architect : null,
            ClosedAt: closed ? Created : null,
            Verdicts: [],
            Authorisations: []);
        var card = new CardFile(frontmatter, "Body.", [], [], SectionFields: sectionFields);
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
            onOrphanedObligations: static orphaned => throw new Xunit.Sdk.XunitException($"expected Archived, got OrphanedObligations: {orphaned.Remedy}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Archived, got ToolFailure: {toolFailure.Reason}"));

    private static ChangeArchiveOutcome.OrphanedObligations AssertOrphanedObligations(ChangeArchiveOutcome outcome) =>
        outcome.Match(
            onArchived: static archived => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got Archived: '{archived.ArchivedDirectory}'"),
            onChangeNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got ChangeNotFound: '{notFound.ChangeName}'"),
            onAlreadyArchived: static already => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got AlreadyArchived: '{already.ChangeName}'"),
            onInvalidChangeName: static invalid => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got InvalidChangeName: {invalid.Reason}"),
            onCardsUnreadable: static unreadable => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got CardsUnreadable: {string.Join(", ", unreadable.FilePaths)}"),
            onOrphanedObligations: static orphaned => orphaned,
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected OrphanedObligations, got ToolFailure: {toolFailure.Reason}"));

    private static string AssertFound(CardIdentityResolution resolution) =>
        resolution.Match(
            onFound: static (filePath, _) => filePath,
            onNotFound: static id => throw new Xunit.Sdk.XunitException($"expected Found, got NotFound: '{id}'"),
            onDuplicate: static (id, paths) => throw new Xunit.Sdk.XunitException($"expected Found, got Duplicate('{id}'): {string.Join(", ", paths)}"),
            onCorrupt: static (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Corrupt('{id}'): {string.Join(", ", files)}"),
            onUnreadable: static (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Unreadable('{id}'): {string.Join(", ", files)}"));

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
