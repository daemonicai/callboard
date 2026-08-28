using System.Linq;
using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §11 block B, task 11.1 — <c>card show &lt;id&gt;</c> (record-retrieval: "the system SHALL
/// return a card's full content, including every comment on it, given the card's identity").
/// Kind-agnostic retrieval by identity, resolved through <see cref="Cards.CardIdentityResolver"/>
/// (never the derived index — ADR-0004), reported rather than recorded on every failure path (§9
/// ruling 1: a pure read asserts nothing about the record), unbounded (no character budget applies
/// — that is <c>context</c>'s own requirement, D6), and with no liveness filter at all — a closed
/// card resolves exactly as a live one does.
/// </summary>
public sealed class CardShowTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingId_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void MissingSubcommand_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnknownSubcommand_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "delete", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unknown-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnrecognisedFlag_Refuses_AtTheDoor()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001", "--role", "worker"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void OutsideAnyGitRepository_Refuses_WithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: directory.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnresolvableId_NotFound_Reports_WithoutRecordingAnything()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-9999"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());

        // A pure read that finds nothing has nothing to record against — no Rule/Remedy, the same
        // bare-refusal shape ResolveAnyCardReference already gives the comment verbs (§9 ruling 1).
        Assert.False(refusal.TryGetProperty("rule", out _));
        Assert.False(refusal.TryGetProperty("remedy", out _));
    }

    [Fact]
    public void DuplicateId_Refuses_WithDuplicateCardIdCode()
    {
        using var repo = new TempGitRepo();
        WriteBlock(repo, "b-dup-1", "B-0007", CardOwner.Worker, "briefed", "S-0001");
        WriteBlock(repo, "b-dup-2", "B-0007", CardOwner.Worker, "briefed", "S-0001");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0007"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("duplicate-card-id", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void UnreadableFileElsewhere_WithNoMatchFound_Refuses_WithCardIdUnresolvableCode()
    {
        using var repo = new TempGitRepo();
        File.WriteAllText(Path.Combine(repo.ChangesDirectory, "corrupt.md"), "not a card file at all");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-unresolvable", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §13.6 — the file claiming the requested id is sitting right there, unparseable: the resolver
    // must say "corrupt", not "unresolvable", because the two name different remedies (open the
    // file vs. hunt for a typo). Asserted on the parse reason and the path, not merely the code —
    // §11's ruling that a test can cover a content class and still not cover what makes it content.
    [Fact]
    public void CorruptFileElsewhere_DeclaringTheRequestedId_Refuses_WithCardCorruptCode_NamingFileAndReason()
    {
        using var repo = new TempGitRepo();
        var corruptPath = WriteCorruptBlock(repo, "b-corrupt", "B-0001", "S-0001");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString()!;
        Assert.Contains("B-0001", message, StringComparison.Ordinal);
        Assert.Contains(corruptPath, message, StringComparison.Ordinal);
        Assert.Contains($"unrecognised status: '{CorruptStatus}'", message, StringComparison.Ordinal);
    }

    // The negative half of the case above: a corrupt file elsewhere that does NOT declare the
    // requested id must not be attributed to it — the resolver has no evidence it is the target,
    // so this stays the honest "cannot confirm or rule out" answer, not a wrong-remedy "corrupt".
    [Fact]
    public void CorruptFileElsewhere_DeclaringADifferentId_StillRefuses_WithCardIdUnresolvableCode()
    {
        using var repo = new TempGitRepo();
        WriteCorruptBlock(repo, "b-corrupt", "B-9999", "S-0001");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-unresolvable", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // A file with no intact frontmatter fence at all has nothing to recover an id from — it must
    // degrade to card-id-unresolvable, never a wrong attribution manufactured from body text.
    [Fact]
    public void FileWithNoFrontmatterFenceAtAll_NeverAttributed_StaysCardIdUnresolvable()
    {
        using var repo = new TempGitRepo();
        File.WriteAllText(Path.Combine(repo.ChangesDirectory, "no-fence.md"), "id: B-0001\nnot a real card file");
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["card", "show", "B-0001"], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-unresolvable", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // A parsed match wins over a corrupt file also claiming the same id — recovery is evidence,
    // never a second record. Getting this backwards would let a stale corrupt duplicate shadow the
    // card that actually owns the id.
    [Fact]
    public void ParsedMatch_TakesPrecedenceOverACorruptFileAlsoClaimingTheId()
    {
        using var repo = new TempGitRepo();
        WriteBlock(repo, "b-real", "B-0001", CardOwner.Worker, "briefed", "S-0001");
        WriteCorruptBlock(repo, "b-corrupt", "B-0001", "S-0001");

        var result = Show(repo, "B-0001");

        Assert.Equal("B-0001", result.GetProperty("id").GetString());
    }

    // record-retrieval: "This material SHALL be retrievable and quotable, and SHALL NOT appear on
    // any default read path" — the retrieval half, exercised against every group CardFile carries.
    [Fact]
    public void BlockCard_ReturnsEveryGroup_InFull()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.ChangesDirectory, "b-0001.md");
        var frontmatter = new CardFrontmatter("B-0001", CardKind.Block, "A block card", "in-review", CardOwner.Reviewer, CardScope.Change, "S-0001", Earlier, FixedNow);
        var handovers = new[] { new CardHandover(CardOwner.Architect, CardOwner.Worker, Earlier, []) };
        var blockFields = new BlockCardFields(
            Base: "base-sha",
            ReviewedState: "reviewed-sha",
            Tasks: ["11.1", "11.2"],
            Round: 1,
            BlockedBy: ["Q-0001"],
            GateResults: [new GateResult("build", 0, 1)],
            FindingKey: "F-key-1");
        var transitions = new[] { new CardBlockTransitionEntry(CardOwner.Worker, "submit-for-review", BlockFlowState.Building, BlockFlowState.InReview, Earlier, []) };
        var claims = new[] { new CardApprovalClaim("claim-1", 1, "The block builds and tests pass.", []) };
        var limits = new[] { new CardApprovalLimit(1, "Does not cover the human view.", []) };
        var refusals = new[] { new CardRefusalEntry(CardOwner.Worker, "work-lifecycle: some rule", "do the thing", Earlier, []) };
        var comments = new[]
        {
            new CardComment("c-1", CardOwner.Reviewer, Earlier, "A nit.", null, CardOwner.Worker, null, [("x-future", "value")], IsNit: true, Required: true, Sites: ["src/Foo.cs:10"]),
            new CardComment("c-2", CardOwner.Worker, FixedNow, "Fixed.", "c-1", CardOwner.Reviewer, "c-1", [], Disposition: NitDisposition.FixBeforeLand),
        };
        var card = new CardFile(
            frontmatter,
            "The block's own body.",
            comments,
            [("x-unknown", "raw value")],
            Handovers: handovers,
            BlockFields: blockFields,
            Transitions: transitions,
            Claims: claims,
            Limits: limits,
            Refusals: refusals);
        WriteCard(path, card);

        var result = Show(repo, "B-0001");

        Assert.Equal("B-0001", result.GetProperty("id").GetString());
        Assert.Equal("block", result.GetProperty("kind").GetString());
        Assert.Equal(path, result.GetProperty("filePath").GetString());
        Assert.Equal("A block card", result.GetProperty("title").GetString());
        Assert.Equal("in-review", result.GetProperty("status").GetString());
        Assert.Equal("reviewer", result.GetProperty("owner").GetString());
        Assert.Equal("change", result.GetProperty("scope").GetString());
        Assert.Equal("S-0001", result.GetProperty("section").GetString());
        Assert.Equal("The block's own body.", result.GetProperty("body").GetString());

        var unknownFrontmatter = Assert.Single(result.GetProperty("unknownFrontmatterFields").EnumerateArray());
        Assert.Equal("x-unknown", unknownFrontmatter.GetProperty("key").GetString());
        Assert.Equal("raw value", unknownFrontmatter.GetProperty("rawValue").GetString());

        var handover = Assert.Single(result.GetProperty("handovers").EnumerateArray());
        Assert.Equal("architect", handover.GetProperty("by").GetString());
        Assert.Equal("worker", handover.GetProperty("to").GetString());

        var block = result.GetProperty("blockFields");
        Assert.Equal("base-sha", block.GetProperty("base").GetString());
        Assert.Equal("reviewed-sha", block.GetProperty("reviewedState").GetString());
        Assert.Equal(["11.1", "11.2"], block.GetProperty("tasks").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(1, block.GetProperty("round").GetInt32());
        Assert.Equal(["Q-0001"], block.GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("F-key-1", block.GetProperty("findingKey").GetString());
        var gate = Assert.Single(block.GetProperty("gateResults").EnumerateArray());
        Assert.Equal("build", gate.GetProperty("label").GetString());
        Assert.Equal(0, gate.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, gate.GetProperty("round").GetInt32());

        var transition = Assert.Single(result.GetProperty("transitions").EnumerateArray());
        Assert.Equal("worker", transition.GetProperty("by").GetString());
        Assert.Equal("submit-for-review", transition.GetProperty("name").GetString());
        Assert.Equal("building", transition.GetProperty("from").GetString());
        Assert.Equal("in-review", transition.GetProperty("to").GetString());

        var claim = Assert.Single(result.GetProperty("claims").EnumerateArray());
        Assert.Equal("claim-1", claim.GetProperty("id").GetString());
        Assert.Equal(1, claim.GetProperty("round").GetInt32());
        Assert.Equal("The block builds and tests pass.", claim.GetProperty("text").GetString());

        var limit = Assert.Single(result.GetProperty("limits").EnumerateArray());
        Assert.Equal("Does not cover the human view.", limit.GetProperty("text").GetString());

        var refusal = Assert.Single(result.GetProperty("refusals").EnumerateArray());
        Assert.Equal("worker", refusal.GetProperty("by").GetString());
        Assert.Equal("work-lifecycle: some rule", refusal.GetProperty("rule").GetString());
        Assert.Equal("do the thing", refusal.GetProperty("remedy").GetString());

        var returnedComments = result.GetProperty("comments").EnumerateArray().ToArray();
        Assert.Equal(2, returnedComments.Length);
        var nit = returnedComments[0];
        Assert.Equal("c-1", nit.GetProperty("id").GetString());
        Assert.Equal("reviewer", nit.GetProperty("author").GetString());
        Assert.Equal("A nit.", nit.GetProperty("body").GetString());
        Assert.Equal("worker", nit.GetProperty("to").GetString());
        Assert.True(nit.GetProperty("isNit").GetBoolean());
        Assert.True(nit.GetProperty("required").GetBoolean());
        Assert.Equal(["src/Foo.cs:10"], nit.GetProperty("sites").EnumerateArray().Select(e => e.GetString()).ToArray());
        var nitUnknown = Assert.Single(nit.GetProperty("unknownHeaderFields").EnumerateArray());
        Assert.Equal("x-future", nitUnknown.GetProperty("key").GetString());

        var disposition = returnedComments[1];
        Assert.Equal("c-2", disposition.GetProperty("id").GetString());
        Assert.Equal("c-1", disposition.GetProperty("replyTo").GetString());
        Assert.Equal("c-1", disposition.GetProperty("resolves").GetString());
        Assert.Equal("fix-before-land", disposition.GetProperty("disposition").GetString());
        Assert.False(disposition.GetProperty("isNit").GetBoolean());

        // record-retrieval: never truncated, no budget applies to this response at all (§11 block
        // B: the character budget is a requirement of context's response specifically).
        Assert.False(result.TryGetProperty("budget", out _));
    }

    [Fact]
    public void SectionCard_ReturnsVerdictsAndAuthorisations()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.ChangesDirectory, "s-0001.md");
        var frontmatter = new CardFrontmatter("S-0001", CardKind.Section, "A section", "closed", CardOwner.Architect, CardScope.Change, string.Empty, Earlier, FixedNow);
        var sectionFields = new SectionCardFields(
            Base: "base-sha",
            ClosedBy: CardOwner.Architect,
            ClosedAt: FixedNow,
            Verdicts: [new SectionVerdictEntry(CardOwner.Supervisor, SectionVerdict.Approve, "a1b2c3", "d4e5f6", Earlier, [])],
            Authorisations: [new SectionAuthorisationEntry(CardOwner.ProductOwner, "a third remediation round is warranted", Earlier, [])]);
        var card = new CardFile(frontmatter, "Section body.", [], [], SectionFields: sectionFields);
        WriteCard(path, card);

        var result = Show(repo, "S-0001");

        var section = result.GetProperty("sectionFields");
        Assert.Equal("base-sha", section.GetProperty("base").GetString());
        Assert.Equal("architect", section.GetProperty("closedBy").GetString());

        var verdict = Assert.Single(section.GetProperty("verdicts").EnumerateArray());
        Assert.Equal("supervisor", verdict.GetProperty("by").GetString());
        Assert.Equal("approve", verdict.GetProperty("verdict").GetString());
        Assert.Equal("a1b2c3", verdict.GetProperty("rangeFrom").GetString());
        Assert.Equal("d4e5f6", verdict.GetProperty("rangeTo").GetString());

        var authorisation = Assert.Single(section.GetProperty("authorisations").EnumerateArray());
        Assert.Equal("product-owner", authorisation.GetProperty("by").GetString());
        Assert.Equal("a third remediation round is warranted", authorisation.GetProperty("reason").GetString());
    }

    [Fact]
    public void FindingCard_ReturnsExtentBlindSpotFingerprintAndDisposition()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.ChangesDirectory, "f-0001.md");
        var frontmatter = new CardFrontmatter("F-0001", CardKind.Finding, "A finding", "open", CardOwner.Reviewer, CardScope.Change, "S-0001", Earlier, FixedNow);
        var fingerprint = new FindingExtentFingerprint([new FindingExtentFileFingerprint("src/Foo.cs", "abc123")]);
        var findingFields = new FindingCardFields(
            Instrument: "make gates",
            Extent: FindingExtent.Explicit(["src/Foo.cs"]),
            VerifiedAt: "6b6468c",
            BlindSpot: FindingBlindSpotDeclaration.RaisedAs("O-0001"),
            ExtentFingerprint: fingerprint,
            Disposition: FindingDisposition.ArguedClean);
        var card = new CardFile(frontmatter, "Finding body.", [], [], FindingFields: findingFields);
        WriteCard(path, card);

        var result = Show(repo, "F-0001");

        var finding = result.GetProperty("findingFields");
        Assert.Equal("make gates", finding.GetProperty("instrument").GetString());
        Assert.Equal("explicit", finding.GetProperty("extentKind").GetString());
        Assert.Equal(["src/Foo.cs"], finding.GetProperty("extentItems").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(finding.TryGetProperty("extentInstrument", out var extentInstrument) && extentInstrument.ValueKind != JsonValueKind.Null);
        Assert.Equal("6b6468c", finding.GetProperty("verifiedAt").GetString());
        Assert.Equal("raisedAs", finding.GetProperty("blindSpotKind").GetString());
        Assert.Equal("O-0001", finding.GetProperty("blindSpotRaisedAsId").GetString());
        Assert.Equal("arguedClean", finding.GetProperty("disposition").GetString());

        var fingerprintFile = Assert.Single(finding.GetProperty("extentFingerprintFiles").EnumerateArray());
        Assert.Equal("src/Foo.cs", fingerprintFile.GetProperty("relativePath").GetString());
        Assert.Equal("abc123", fingerprintFile.GetProperty("contentHash").GetString());
    }

    [Fact]
    public void ObligationCard_ReturnsRegisterFields()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "o-0001.md");
        var frontmatter = new CardFrontmatter("O-0001", CardKind.Obligation, "An obligation", "discharged", CardOwner.Architect, CardScope.Repository, string.Empty, Earlier, FixedNow);
        var registerFields = new RegisterCardFields(
            Condition: null,
            Cadence: null,
            DischargedBy: CardOwner.Architect,
            DischargedAt: FixedNow,
            OwedBy: "S-0001",
            DeclinedReason: "superseded by a later ruling");
        var card = new CardFile(frontmatter, "Obligation body.", [], [], RegisterFields: registerFields);
        WriteCard(path, card);

        var result = Show(repo, "O-0001");

        var register = result.GetProperty("registerFields");
        Assert.Equal("architect", register.GetProperty("dischargedBy").GetString());
        Assert.Equal("S-0001", register.GetProperty("owedBy").GetString());
        Assert.Equal("superseded by a later ruling", register.GetProperty("declinedReason").GetString());
    }

    [Fact]
    public void QuestionCard_ReturnsQuestionFields()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "q-0001.md");
        var frontmatter = new CardFrontmatter("Q-0001", CardKind.Question, "A question", "deferred", CardOwner.ProductOwner, CardScope.Repository, string.Empty, Earlier, FixedNow);
        var questionFields = new QuestionCardFields
        {
            DeferredBy = CardOwner.ProductOwner,
            DeferredAt = FixedNow,
            DeferredTarget = "section 13",
        };
        var card = new CardFile(frontmatter, "Question body.", [], [], QuestionFields: questionFields);
        WriteCard(path, card);

        var result = Show(repo, "Q-0001");

        var question = result.GetProperty("questionFields");
        Assert.Equal("product-owner", question.GetProperty("deferredBy").GetString());
        Assert.Equal("section 13", question.GetProperty("deferredTarget").GetString());
    }

    // record-retrieval / §11 block B brief: "No liveness filter at all — a closed card is
    // retrievable by identity." Pinned here so §11 block D's default-query liveness filter never
    // regresses onto this path.
    [Fact]
    public void ClosedCard_IsStillRetrievable()
    {
        using var repo = new TempGitRepo();
        WriteBlock(repo, "b-closed", "B-0002", CardOwner.Worker, "closed", "S-0001");

        var result = Show(repo, "B-0002");

        Assert.Equal("B-0002", result.GetProperty("id").GetString());
        Assert.Equal("closed", result.GetProperty("status").GetString());
    }

    [Fact]
    public void Show_TakesNoLock_AndAppendsNothingToTheCard()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.ChangesDirectory, "b-0003.md");
        WriteBlock(repo, "b-0003", "B-0003", CardOwner.Worker, "briefed", "S-0001");
        var before = File.ReadAllText(path);

        Show(repo, "B-0003");

        var after = File.ReadAllText(path);
        Assert.Equal(before, after);
    }

    private static void WriteBlock(TempGitRepo repo, string fileStem, string id, CardOwner owner, string status, string section)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, section, Earlier, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
    }

    private const string CorruptStatus = "not-a-real-status";

    // A file whose leading frontmatter fence is intact — so §13.6 recovery can read its declared
    // 'id' — but whose status the parser's own vocabulary check rejects (§12 block A), so the file
    // as a whole still fails to parse. This is the specific corruption class §13.6 is about: the id
    // is genuinely there to recover, and the rest of the file genuinely is not readable.
    private static string WriteCorruptBlock(TempGitRepo repo, string fileStem, string id, string section)
    {
        var path = Path.Combine(repo.ChangesDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A corrupt block", CorruptStatus, CardOwner.Worker, CardScope.Change, section, Earlier, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
        return path;
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static JsonElement Show(TempGitRepo repo, string id)
    {
        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["card", "show", id], output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-card-show-cli-nongit-{Guid.NewGuid():N}");

        internal TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string ChangesDirectory { get; }

        internal string RegisterDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-card-show-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            ChangesDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory("establish-callboard").Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(ChangesDirectory);
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
