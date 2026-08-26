using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 8a.15 — recording a Product Owner authorisation under lock (§8a block C, work-lifecycle:
/// "Remediation beyond the second round requires recorded authorisation"). Same read-decide-write
/// shape <see cref="CardSectionVerdictTests"/> already proves for <see cref="CardStore.
/// RecordSectionVerdict"/>, plus the role check §8 block A's <c>RecordApproval</c> established
/// first: only <see cref="CardOwner.ProductOwner"/> may record one.
/// </summary>
public sealed class CardSectionAuthorisationTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-authorisation-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardSectionAuthorisationTests()
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
    public void RecordSectionAuthorisation_ByProductOwner_CarriesReasonRoleAndTimestamp()
    {
        var path = WriteInitialSectionCard("s-0001", "S-0001");
        BringToBound(path, "c-0001");

        var outcome = CardStore.RecordSectionAuthorisation(
            _root, path, "The section keeps failing to converge on a spec question, not the work.", CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Equal("The section keeps failing to converge on a spec question, not the work.", recorded.Entry.Reason);
        Assert.Equal(CardOwner.ProductOwner, recorded.Entry.By);
        Assert.Equal(Created.AddDays(1), recorded.Entry.Timestamp);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.SectionFields.Authorisations);
        Assert.Equal("The section keeps failing to converge on a spec question, not the work.", only.Reason);
        Assert.Equal(CardOwner.ProductOwner, only.By);
    }

    // work-lifecycle: "The authorisation SHALL be part of the record, not a permission granted out
    // of band" — the one permission that exists to be granted from outside the agents. Every other
    // role is refused, proven for each one so an unhandled case cannot slip through as a pass.
    // §9 remediation S3: RoleNotPermitted now records against the card, the same disposition §9
    // block B's ruling gives CardApprovalOutcome.RoleNotPermitted — an agent attempting the
    // Product-Owner-only authorisation verb is exactly the pattern this project's premise requires
    // to leave a mark.
    [Theory]
    [InlineData(nameof(CardOwner.Architect))]
    [InlineData(nameof(CardOwner.Worker))]
    [InlineData(nameof(CardOwner.Reviewer))]
    [InlineData(nameof(CardOwner.Supervisor))]
    public void RecordSectionAuthorisation_ByAnyRoleOtherThanProductOwner_Refuses_AndRecordsTheRefusal(string roleName)
    {
        var path = WriteInitialSectionCard("s-0002-" + roleName, "S-0002-" + roleName);
        var role = roleName switch
        {
            nameof(CardOwner.Architect) => CardOwner.Architect,
            nameof(CardOwner.Worker) => CardOwner.Worker,
            nameof(CardOwner.Reviewer) => CardOwner.Reviewer,
            nameof(CardOwner.Supervisor) => CardOwner.Supervisor,
            _ => throw new InvalidOperationException($"unhandled role in test data: '{roleName}'"),
        };

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Attempted self-authorisation.", role, Created, TimeSpan.FromSeconds(5), ChangeName);

        var roleNotPermitted = Assert.IsType<CardSectionAuthorisationOutcome.RoleNotPermitted>(outcome);
        Assert.Equal(role, roleNotPermitted.AttemptedRole);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Authorisations);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(role, recorded.By);
        Assert.Equal(roleNotPermitted.RefusingRule, recorded.Rule);
        Assert.Equal(roleNotPermitted.Remedy, recorded.Remedy);
    }

    // §9 remediation S3: the role check now runs immediately after a successful ReadCard, not
    // ahead of File.Exists — the same ordering RecordApprovalUnderExistingLock's own doc comment
    // establishes (§9 block B ruling). A wrong role attempted against a nonexistent card must
    // therefore refuse as CardNotFound, not RoleNotPermitted — pinned here so a future reorder of
    // the two checks fails a test rather than only failing open silently.
    [Fact]
    public void RecordSectionAuthorisation_CardDoesNotExist_AndRoleIsWrong_RefusesAsCardNotFound()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardSectionAuthorisationOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    // Each authorisation must itself be recorded at the bound (§8a block C remediation): the first
    // is recorded once two request-changes verdicts are in, spent by a third, and the second is
    // recorded once the section is back at the bound with none unspent — proving the sequence is a
    // second entry, not an upsert, across that spend-and-recharge cycle.
    [Fact]
    public void RecordSectionAuthorisation_SecondRecording_AppendsRatherThanReplacing()
    {
        var path = WriteInitialSectionCard("s-0003", "S-0003");
        BringToBound(path, "c-0003");

        AssertRecorded(CardStore.RecordSectionAuthorisation(_root, path, "First round pushed further.", CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c-0003-2", "c-0003-3", CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionAuthorisation(_root, path, "Second round pushed further.", CardOwner.ProductOwner, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.SectionFields.Authorisations.Length);
        Assert.Equal("First round pushed further.", read.SectionFields.Authorisations[0].Reason);
        Assert.Equal("Second round pushed further.", read.SectionFields.Authorisations[1].Reason);
    }

    [Fact]
    public void RecordSectionAuthorisation_TargetIsNotASectionCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "8a", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notASection = Assert.IsType<CardSectionAuthorisationOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);

        // process-enforcement (§9 block A2): card-addressed — recorded against the question card
        // the authorisation was actually pointed at.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(notASection.RefusingRule, recorded.Rule);
        Assert.Equal(notASection.Remedy, recorded.Remedy);
    }

    [Fact]
    public void RecordSectionAuthorisation_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing-2.md");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardSectionAuthorisationOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void RecordSectionAuthorisation_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialSectionCard("s-0004", "S-0004");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardSectionAuthorisationOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void RecordSectionAuthorisation_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardSectionAuthorisationOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void RecordSectionAuthorisation_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialSectionCard("s-0005", "S-0005");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Reason.", CardOwner.ProductOwner, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardSectionAuthorisationOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // work-lifecycle scenario "Authorisation ahead of need is refused" (§8a block C remediation,
    // Architect ruling): banking authorisations before the section is even at the bound satisfies
    // the one-for-one count literally while defeating it — the reason would describe a round that
    // has not happened yet. Proven on a brand-new section (zero request-changes verdicts) and again
    // on one carrying exactly one (still short of the bound) — neither is "at the bound with none
    // unspent", so both are refused, and nothing is written either time.
    [Fact]
    public void RecordSectionAuthorisation_OnABrandNewSection_RefusesWithNotAtBound()
    {
        var path = WriteInitialSectionCard("s-0006", "S-0006");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Anticipating a rocky section.", CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notAtBound = Assert.IsType<CardSectionAuthorisationOutcome.NotAtBound>(outcome);
        Assert.Equal(0, notAtBound.PriorRequestChanges);
        Assert.Equal(0, notAtBound.UnspentAuthorisations);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Authorisations);

        // process-enforcement (§9 block A2): recorded against this same section card.
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(notAtBound.RefusingRule, recorded.Rule);
        Assert.Equal(notAtBound.Remedy, recorded.Remedy);
    }

    [Fact]
    public void RecordSectionAuthorisation_WithOnlyOneRequestChangesVerdictRecorded_StillRefusesWithNotAtBound()
    {
        var path = WriteInitialSectionCard("s-0007", "S-0007");
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "c1", "c2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "Anticipating a second round.", CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        var notAtBound = Assert.IsType<CardSectionAuthorisationOutcome.NotAtBound>(outcome);
        Assert.Equal(1, notAtBound.PriorRequestChanges);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Authorisations);
    }

    // The mirror image, at the CardStore layer: once genuinely at the bound (two request-changes
    // verdicts, no unspent authorisation), recording one succeeds — proving the refusal above is
    // about timing, not about authorisation ever being reachable at all.
    [Fact]
    public void RecordSectionAuthorisation_OnceGenuinelyAtTheBound_Succeeds()
    {
        var path = WriteInitialSectionCard("s-0008", "S-0008");
        BringToBound(path, "c-0008");

        var outcome = CardStore.RecordSectionAuthorisation(_root, path, "The section keeps failing to converge.", CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        AssertRecorded(outcome);
    }

    /// <summary>
    /// The same byte-identical-round-trip proof <see cref="CardSectionVerdictTests"/> already gives
    /// its own append-only line: a hand-authored card carrying an authorisation line with awkward
    /// raw values (an escaped space in <c>reason</c>, an unrecognised extra field) round-trips
    /// byte-identically through parse → write.
    /// </summary>
    [Fact]
    public void HandAuthoredCard_WithAnAwkwardAuthorisationLine_RoundTripsByteIdentically()
    {
        const string raw =
            "---\n" +
            "id: S-0301\n" +
            "kind: section\n" +
            "title: Byte-identical authorisation\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: \n" +
            "created: 2026-08-25T09:00:00.0000000+00:00\n" +
            "updated: 2026-08-25T09:00:00.0000000+00:00\n" +
            "base: e055e5b\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:authorisation by=product-owner reason=pushing\\sfurther timestamp=2026-08-25T10:00:00.0000000+00:00 future-field=kept -->\n";

        var parsed = AssertParseSuccess(CardFileParser.Parse(raw));

        var only = Assert.Single(parsed.SectionFields.Authorisations);
        Assert.Equal(CardOwner.ProductOwner, only.By);
        Assert.Equal("pushing further", only.Reason);
        Assert.Equal(("future-field", "kept"), Assert.Single(only.UnknownFields));

        var reserialized = CardFileWriter.Serialize(parsed);

        Assert.Equal(Encoding.UTF8.GetBytes(raw), Encoding.UTF8.GetBytes(reserialized));
    }

    [Fact]
    public void HandAuthoredCard_WithAnEmptyReason_RefusesToParse()
    {
        const string raw =
            "---\n" +
            "id: S-0302\n" +
            "kind: section\n" +
            "title: Empty reason\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: \n" +
            "created: 2026-08-25T09:00:00.0000000+00:00\n" +
            "updated: 2026-08-25T09:00:00.0000000+00:00\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:authorisation by=product-owner reason= timestamp=2026-08-25T10:00:00.0000000+00:00 -->\n";

        var result = CardFileParser.Parse(raw);

        result.Match<object?>(
            onSuccess: static success => throw new Xunit.Sdk.XunitException($"expected parse failure, got success: {success.Card.Frontmatter.Id}"),
            onFailure: static failure =>
            {
                Assert.Contains("reason", failure.Reason, StringComparison.Ordinal);
                return null;
            });
    }

    // Records exactly two request-changes verdicts against the given section, using
    // rangeStem/rangeStem-2/rangeStem-3 as the three range endpoints — "at the bound with none
    // unspent" for every test that needs to get past RecordSectionAuthorisationUnderExistingLock's
    // own precondition without that setup being the thing under test.
    private void BringToBound(string path, string rangeStem)
    {
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, rangeStem, rangeStem + "-2", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, rangeStem + "-2", rangeStem + "-3", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName, [], []));
    }

    private static void AssertRecorded(CardSectionVerdictOutcome outcome) =>
        outcome.Match<object?>(
            onRecorded: static _ => null,
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onRecurringTargetNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringTargetNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onRecurringFindingNotApproved: static notApproved => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingNotApproved: '{notApproved.CardId}'"),
            onRecurringFindingTargetsTaskImplementingBlock: static taskImplementing => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingTargetsTaskImplementingBlock: '{taskImplementing.CardId}'"),
            onFindingAlreadyOwned: static alreadyOwned => throw new Xunit.Sdk.XunitException($"expected Recorded, got FindingAlreadyOwned: '{alreadyOwned.Key}'"),
            onNewFindingCardAlreadyExists: static alreadyExists => throw new Xunit.Sdk.XunitException($"expected Recorded, got NewFindingCardAlreadyExists: '{alreadyExists.FilePath}'"),
            onRemediationBoundExceeded: static boundExceeded => throw new Xunit.Sdk.XunitException($"expected Recorded, got RemediationBoundExceeded: verdict #{boundExceeded.VerdictNumber}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Recorded, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "8a", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardSectionAuthorisationOutcome.Recorded AssertRecorded(CardSectionAuthorisationOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onRoleNotPermitted: static roleNotPermitted => throw new Xunit.Sdk.XunitException($"expected Recorded, got RoleNotPermitted: '{roleNotPermitted.AttemptedRole.ToWireString()}'"),
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onNotAtBound: static notAtBound => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotAtBound: {notAtBound.PriorRequestChanges} prior request-changes, {notAtBound.UnspentAuthorisations} unspent"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"));

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
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
