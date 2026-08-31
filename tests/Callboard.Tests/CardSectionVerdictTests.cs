using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.8 — recording a supervisor verdict under lock (§5 block E, the same read-decide-write shape
/// §5 block C's <see cref="CardStore.ApplyBlockTransition"/> established). Owed evidence 2: a
/// recorded verdict carries its range and acting role — proven here by reading the field back off
/// the entry actually appended, not the argument the test happened to pass.
/// </summary>
public sealed class CardSectionVerdictTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-verdict-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardSectionVerdictTests()
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

    // Owed evidence 2: the recorded entry carries the range (both endpoints) and the acting role —
    // not just that a verdict landed. What would have to break for this to go red: RecordGateResult
    // recording the verdict value but dropping RangeFrom/RangeTo/By, or the CLI-level test's
    // equivalent silently substituting a default.
    [Fact]
    public void RecordSectionVerdict_FirstRecording_CarriesRangeAndActingRole()
    {
        var path = WriteInitialSectionCard("s-0001", "S-0001");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "e055e5b", "a52cd7a", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []);

        var recorded = AssertRecorded(outcome);
        Assert.Equal(SectionVerdict.RequestChanges, recorded.Entry.Verdict);
        Assert.Equal("e055e5b", recorded.Entry.RangeFrom);
        Assert.Equal("a52cd7a", recorded.Entry.RangeTo);
        Assert.Equal(CardOwner.Supervisor, recorded.Entry.By);
        Assert.Equal(Created, recorded.Entry.Timestamp);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.SectionFields.Verdicts);
        Assert.Equal(SectionVerdict.RequestChanges, only.Verdict);
        Assert.Equal("e055e5b", only.RangeFrom);
        Assert.Equal("a52cd7a", only.RangeTo);
        Assert.Equal(CardOwner.Supervisor, only.By);
    }

    // A section can accumulate more than one verdict across remediation rounds — a second verdict
    // is a second entry, not an upsert (unlike RecordGateResult's label-keyed replace). What would
    // have to break for this to go red: RecordSectionVerdict replacing the first entry instead of
    // appending, or CardFileWriter/CardFileParser dropping one on the round trip.
    [Fact]
    public void RecordSectionVerdict_SecondRecording_AppendsRatherThanReplacing()
    {
        var path = WriteInitialSectionCard("s-0002", "S-0002");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "e055e5b", "cdcd6fa", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "e055e5b", "a52cd7a", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.SectionFields.Verdicts.Length);
        Assert.Equal(SectionVerdict.RequestChanges, read.SectionFields.Verdicts[0].Verdict);
        Assert.Equal(SectionVerdict.Approve, read.SectionFields.Verdicts[1].Verdict);
    }

    // 8a.12 — retain every verdict, never overwrite: a third recording leaves the first two entries
    // byte-identical and in order, across a write/parse round trip each time. What would have to
    // break for this to go red: RecordSectionVerdict replacing an earlier entry, reordering the
    // sequence, or CardFileWriter/CardFileParser dropping or mutating one on a later write.
    [Fact]
    public void RecordSectionVerdict_ThirdRecording_RetainsEarlierTwoEntriesByteIdentical()
    {
        var path = WriteInitialSectionCard("s-0021", "S-0021");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "commit-1", "commit-2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        var afterFirst = AssertParseSuccess(CardStore.ReadCard(path)).SectionFields.Verdicts;

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "commit-2", "commit-3", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));
        var afterSecond = AssertParseSuccess(CardStore.ReadCard(path)).SectionFields.Verdicts;
        Assert.Equal(afterFirst[0], afterSecond[0]);

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "commit-3", "commit-4", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []));
        var afterThird = AssertParseSuccess(CardStore.ReadCard(path)).SectionFields.Verdicts;

        Assert.Equal(3, afterThird.Length);
        Assert.Equal(afterFirst[0], afterThird[0]);
        Assert.Equal(afterSecond[1], afterThird[1]);
        Assert.Equal(SectionVerdict.RequestChanges, afterThird[0].Verdict);
        Assert.Equal(SectionVerdict.Approve, afterThird[1].Verdict);
        Assert.Equal(SectionVerdict.RequestChanges, afterThird[2].Verdict);
    }

    // 8a.13/8a.14 — two request-changes verdicts land without ceremony; the third is refused. The
    // count is of request-changes verdicts specifically: an intervening approve does not advance
    // the bound (work-lifecycle: "an approve is not a remediation round"), and this test proves that
    // by interleaving one before the refused third.
    [Fact]
    public void RecordSectionVerdict_ThirdRequestChangesWithoutAuthorisation_Refuses_AndApproveDoesNotAdvanceTheBound()
    {
        var path = WriteInitialSectionCard("s-0022", "S-0022");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c1", "c2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c2", "c3", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "c3", "c4", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []));

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c4", "c5", CardOwner.Supervisor, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName, [], []);

        var boundExceeded = Assert.IsType<CardSectionVerdictOutcome.RemediationBoundExceeded>(outcome);
        Assert.Equal(3, boundExceeded.VerdictNumber);
        Assert.Equal(0, boundExceeded.AuthorisationsRecorded);

        // Refusal-shaped: no verdict was written for the attempt — but the refusal itself now is
        // (§9 block B).
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(3, read.SectionFields.Verdicts.Length);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(boundExceeded.RefusingRule, recorded.Rule);
        Assert.Equal(boundExceeded.Remedy, recorded.Remedy);
    }

    // work-lifecycle scenario "A recurring finding counts toward the bound": three request-changes
    // verdicts all reporting the same finding still unresolved (no new card created by any of them)
    // still hits the bound on the third — the count is of verdicts, not of cards.
    [Fact]
    public void RecordSectionVerdict_ThirdRequestChangesReportingOnlyARecurringFinding_StillRefused()
    {
        var path = WriteInitialSectionCard("s-0023", "S-0023");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c1", "c2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c2", "c3", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c3", "c4", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []);

        Assert.IsType<CardSectionVerdictOutcome.RemediationBoundExceeded>(outcome);
    }

    // 8a.13's "Authorised third verdict proceeds" scenario, plus 8a.15's "readable from the section":
    // a recorded authorisation permits the third, and it and its reason are on the section card
    // afterwards.
    [Fact]
    public void RecordSectionVerdict_ThirdRequestChangesWithARecordedAuthorisation_ProceedsAndTheAuthorisationRemains()
    {
        var path = WriteInitialSectionCard("s-0024", "S-0024");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c1", "c2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c2", "c3", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));

        var authorised = CardStore.RecordSectionAuthorisation(
            _root, path, "The section breakdown is wrong, not the work — pushing a third round.", CardOwner.ProductOwner, Created.AddDays(1).AddHours(1), TimeSpan.FromSeconds(5), ChangeName);
        Assert.IsType<CardSectionAuthorisationOutcome.Recorded>(authorised);

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c3", "c4", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []);

        var recorded = AssertRecorded(outcome);
        Assert.Equal(SectionVerdict.RequestChanges, recorded.Entry.Verdict);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(3, read.SectionFields.Verdicts.Length);
        var onlyAuthorisation = Assert.Single(read.SectionFields.Authorisations);
        Assert.Equal(CardOwner.ProductOwner, onlyAuthorisation.By);
        Assert.Equal("The section breakdown is wrong, not the work — pushing a third round.", onlyAuthorisation.Reason);
    }

    // The authorisation discharges exactly one verdict: with one recorded and spent by the third,
    // a fourth request-changes verdict is refused again until a second is recorded.
    [Fact]
    public void RecordSectionVerdict_FourthRequestChangesAfterOneAuthorisationAlreadySpent_RefusesUntilASecondIsRecorded()
    {
        var path = WriteInitialSectionCard("s-0025", "S-0025");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c1", "c2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c2", "c3", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName, [], []));
        Assert.IsType<CardSectionAuthorisationOutcome.Recorded>(CardStore.RecordSectionAuthorisation(
            _root, path, "First push.", CardOwner.ProductOwner, Created.AddDays(1).AddHours(1), TimeSpan.FromSeconds(5), ChangeName));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c3", "c4", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []));

        var stillRefused = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c4", "c5", CardOwner.Supervisor, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName, [], []);
        var boundExceeded = Assert.IsType<CardSectionVerdictOutcome.RemediationBoundExceeded>(stillRefused);
        Assert.Equal(4, boundExceeded.VerdictNumber);
        Assert.Equal(1, boundExceeded.AuthorisationsRecorded);

        Assert.IsType<CardSectionAuthorisationOutcome.Recorded>(CardStore.RecordSectionAuthorisation(
            _root, path, "Second push.", CardOwner.ProductOwner, Created.AddDays(2).AddHours(1), TimeSpan.FromSeconds(5), ChangeName));
        var nowProceeds = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c4", "c5", CardOwner.Supervisor, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName, [], []);
        AssertRecorded(nowProceeds);
    }

    [Fact]
    public void RecordSectionVerdict_TargetIsNotASectionCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []);

        var notASection = Assert.IsType<CardSectionVerdictOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);

        // §9 block B: card-addressed — the target is resolved and parsed before the kind check.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(notASection.RefusingRule, recorded.Rule);
        Assert.Equal(notASection.Remedy, recorded.Remedy);
    }

    [Fact]
    public void RecordSectionVerdict_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []);

        var notFound = Assert.IsType<CardSectionVerdictOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void RecordSectionVerdict_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialSectionCard("s-0003", "S-0003");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), "a-different-change", [], []);

        Assert.IsType<CardSectionVerdictOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void RecordSectionVerdict_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []);

        var corrupt = Assert.IsType<CardSectionVerdictOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void RecordSectionVerdict_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialSectionCard("s-0004", "S-0004");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.RecordSectionVerdict(
                _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromMilliseconds(200), ChangeName, [], []);

            Assert.IsType<CardSectionVerdictOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // §9 block B: RecurringTargetNotFound is split from the pre-lock CardNotFound above precisely
    // because it is card-addressed — the section card is already resolved, anchored and locked by
    // the time a --finding-recurred target is found missing. Reached only by calling CardStore
    // directly with a path CardIdentityResolver/ResolveCardReference never had the chance to refuse
    // first — the CLI's own id resolution always reads the file before this method is ever called,
    // so this is the same "only reachable by calling CardStore directly, bypassing the CLI's own id
    // resolution" shape §9 block A3's CardNitStoreTests already established.
    [Fact]
    public void RecordSectionVerdict_RecurringTargetDoesNotExist_Refuses_AndRecordsAgainstTheSection()
    {
        var path = WriteInitialSectionCard("s-0026", "S-0026");
        var missingRecurringPath = Path.Combine(_directory, "b-does-not-exist.md");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName,
            [missingRecurringPath], []);

        var notFound = Assert.IsType<CardSectionVerdictOutcome.RecurringTargetNotFound>(outcome);
        Assert.Equal(missingRecurringPath, notFound.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Verdicts);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(notFound.RefusingRule, recorded.Rule);
        Assert.Equal(notFound.Remedy, recorded.Remedy);
    }

    // A reachable case, not a theoretical one: a --finding-recurred target whose own stored round
    // disagrees with its transition history. Card-addressed against the section, the same as
    // RecurringTargetNotFound above.
    [Fact]
    public void RecordSectionVerdict_RecurringTargetRoundDisagreesWithHistory_Refuses_AndRecordsAgainstTheSection()
    {
        var path = WriteInitialSectionCard("s-0027", "S-0027");
        var recurringPath = WriteApprovedRemediationCard("b-own-0027", "B-OWN-0027", "S-0027", "finding-x027", storedRound: 3);

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName,
            [recurringPath], []);

        var disagreement = Assert.IsType<CardSectionVerdictOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(recurringPath, disagreement.FilePath);
        Assert.Equal(3, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Verdicts);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(disagreement.RefusingRule, recorded.Rule);
        Assert.Equal(disagreement.Remedy, recorded.Remedy);
        // The recurring card itself is untouched — neither figure is privileged or altered.
        Assert.Equal(3, AssertParseSuccess(CardStore.ReadCard(recurringPath)).BlockFields.Round);
    }

    private string WriteApprovedRemediationCard(string fileStem, string id, string sectionId, string findingKey, int storedRound)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, Created, Created);
        var blockFields = new BlockCardFields(
            Base: "base-commit", ReviewedState: "reviewed-state", Tasks: [], Round: storedRound, BlockedBy: [], GateResults: [], FindingKey: findingKey);
        // No transitions recorded at all, so the expected round (one plus round-incrementing
        // transitions) is 1 regardless of storedRound — deliberately disagreeing for storedRound > 1.
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// The gap the reviewer flagged as missing (§5 block E remediation) — the same hazard block D's
    /// <c>gate_results</c> byte-identical round-trip test closed: a hand-authored card carrying a
    /// verdict block with awkward raw values (an escaped backslash in <c>range-from</c>, an
    /// unrecognised extra field) round-trips byte-identically through parse → write, asserted on
    /// the file's bytes, not the parsed object. §14.2 dropped the escaped-space convention this
    /// test used to exercise — <c>range-from</c> now sits on its own <c>key: value</c> line, so an
    /// interior space is never ambiguous and is never escaped; only the backslash still is.
    /// </summary>
    [Fact]
    public void HandAuthoredCard_WithAnAwkwardVerdictLine_RoundTripsByteIdentically()
    {
        const string raw =
            "---\n" +
            "id: S-0301\n" +
            "kind: section\n" +
            "title: Byte-identical verdict\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: \n" +
            "created: 2026-08-22T09:00:00.0000000+00:00\n" +
            "updated: 2026-08-22T09:00:00.0000000+00:00\n" +
            "base: e055e5b\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:verdict\n" +
            "by: supervisor\n" +
            "verdict: request-changes\n" +
            "range-from: odd\\\\path with spaces\n" +
            "range-to: a52cd7a\n" +
            "timestamp: 2026-08-22T10:00:00.0000000+00:00\n" +
            "future-field: kept\n" +
            "-->\n";

        var parsed = AssertParseSuccess(CardFileParser.Parse(raw));

        var only = Assert.Single(parsed.SectionFields.Verdicts);
        Assert.Equal(CardOwner.Supervisor, only.By);
        Assert.Equal(SectionVerdict.RequestChanges, only.Verdict);
        Assert.Equal("odd\\path with spaces", only.RangeFrom);
        Assert.Equal("a52cd7a", only.RangeTo);
        Assert.Equal(("future-field", "kept"), Assert.Single(only.UnknownFields));

        var reserialized = CardFileWriter.Serialize(parsed);

        Assert.Equal(Encoding.UTF8.GetBytes(raw), Encoding.UTF8.GetBytes(reserialized));
    }

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardSectionVerdictOutcome.Recorded AssertRecorded(CardSectionVerdictOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onRecurringTargetNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringTargetNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onRecurringFindingNotApproved: static notApproved => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingNotApproved: '{notApproved.CardId}'"),
            onRecurringFindingTargetsTaskImplementingBlock: static taskImplementing => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingTargetsTaskImplementingBlock: '{taskImplementing.CardId}'"),
            onFindingAlreadyOwned: static alreadyOwned => throw new Xunit.Sdk.XunitException($"expected Recorded, got FindingAlreadyOwned: '{alreadyOwned.Key}'"),
            onNewFindingCardAlreadyExists: static alreadyExists => throw new Xunit.Sdk.XunitException($"expected Recorded, got NewFindingCardAlreadyExists: '{alreadyExists.FilePath}'"),
            onRemediationBoundExceeded: static boundExceeded => throw new Xunit.Sdk.XunitException($"expected Recorded, got RemediationBoundExceeded: verdict #{boundExceeded.VerdictNumber}, {boundExceeded.AuthorisationsRecorded} authorisation(s) recorded"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Recorded, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: static handEntered => throw new Xunit.Sdk.XunitException($"expected Recorded, got HandEnteredDerivedState: '{handEntered.FilePath}' key '{handEntered.Key}'"));

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static void AssertWriteSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected write success, got HandEnteredDerivedState: '{handEntered.Key}'"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
