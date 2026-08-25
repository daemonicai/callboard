using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 5.8 at the CLI boundary: <c>section verdict</c>, <c>section close</c> and <c>section status</c>
/// (§5 block E). Same "own refusal code, own test, verified by reverting the exact line it guards"
/// discipline §5 block C's fourth remediation round established. Every refusal code this block
/// mints — <c>wrong-card-kind</c> (three of this block's own construction sites: verdict, close,
/// status — three more live in <c>block transition</c>/<c>block gate</c>/the shared
/// <c>blocked_by</c> mapping, collapsed with <c>not-a-block-card</c> into one code, §5 remediation,
/// finding N3), <c>already-closed</c>, <c>unrecognised-verdict</c> — gets its own test here; the
/// codes this block only reuses (<c>card-not-found</c>, <c>missing-argument</c>,
/// <c>missing-flag-value</c>, <c>unrecognised-role</c>, <c>card-layout-mismatch</c>) already have
/// their construction sites proven for <c>block gate</c>/<c>block transition</c> and are exercised
/// here only enough to prove this verb's own parse arm actually reaches them.
/// </summary>
public sealed class CommandDispatcherSectionTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SectionVerdict_Recording_Succeeds_AndTheEnvelopeReportsWhatWasRecorded()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0001", "S-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "e055e5b", "--range-to", "a52cd7a", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("request-changes", result.GetProperty("verdict").GetString());
        Assert.Equal("e055e5b", result.GetProperty("rangeFrom").GetString());
        Assert.Equal("a52cd7a", result.GetProperty("rangeTo").GetString());
        Assert.Equal("supervisor", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.SectionFields.Verdicts);
        Assert.Equal(SectionVerdict.RequestChanges, only.Verdict);
    }

    // Reverting SectionVerdictWireFormat.TryParse's failure branch in ParseSectionVerdict (or the
    // "unrecognised-verdict" construction it guards) is what this test would go red against.
    [Fact]
    public void SectionVerdict_UnrecognisedVerdict_RefusesWithUnrecognisedVerdictCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0002", "S-0002");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "maybe", "--range-from", "a", "--range-to", "b", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-verdict", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Construction site 1 of 3 for "wrong-card-kind": section verdict.
    [Fact]
    public void SectionVerdict_TargetIsNotASectionCard_RefusesWithNotASectionCardCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "approve", "--range-from", "a", "--range-to", "b", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void SectionVerdict_MissingRangeFrom_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0003", "S-0003");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "approve", "--range-to", "b", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Reviewer finding, §5 block E remediation, demonstrated live against the unmutated binary:
    // `--range-from ""` used to succeed at parse time, write `range-from=` to the card, and then
    // fail every subsequent read of that card with a corrupt/tool-failure disposition — the tool's
    // own write path producing a card the tool's own read path then refused. This is the guard that
    // now closes it: an empty range-from is refused before CardStore.RecordSectionVerdict is ever
    // called, so no write happens at all. What would have to break for this to go red: `--range-from
    // ""` reaching CardStore.RecordSectionVerdict and being written to the card.
    [Fact]
    public void SectionVerdict_EmptyRangeFrom_RefusesWithInvalidRangeCode_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0007", "S-0007");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "approve", "--range-from", "", "--range-to", "abc123", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-range", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // The reviewer's own detail: whitespace-only must trigger the same guard as empty — the file
    // parser's original bug distinguished "zero-length" from "whitespace-only", so a test that only
    // covers empty would leave that half of the gap open. What would have to break for this to go
    // red: `--range-to` accepting a whitespace-only value because the guard checks `Length == 0`
    // instead of `IsNullOrWhiteSpace`.
    [Fact]
    public void SectionVerdict_WhitespaceOnlyRangeTo_RefusesWithInvalidRangeCode_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0008", "S-0008");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "approve", "--range-from", "abc123", "--range-to", "   ", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-range", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // The proposition this section's remediation exists to establish, proven end to end rather than
    // just at the domain layer: anything the CLI writes, the CLI can read back. Write via `section
    // verdict`, then read via `section status`, and assert the read succeeds rather than reporting
    // tool-failure/corrupt — this is exactly the sequence the reviewer demonstrated failing against
    // the unmutated binary.
    [Fact]
    public void SectionVerdict_WrittenThroughTheCli_IsThenReadableThroughTheCli_ViaSectionStatus()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0009", "S-0009");

        var writeOutput = new StringWriter();
        var writeExitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "approve", "--range-from", "e055e5b", "--range-to", "a52cd7a", "--role", "supervisor", "--change", ChangeName],
            writeOutput, repo.Path);
        Assert.Equal(CommandDispatcher.SuccessExitCode, writeExitCode);

        var readOutput = new StringWriter();
        var readExitCode = RunInRepo(["section", "status", path], readOutput, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, readExitCode);
        using var doc = JsonDocument.Parse(readOutput.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("result").GetProperty("verdictCount").GetInt32());
    }

    [Fact]
    public void SectionAuthorise_ByProductOwner_Succeeds_AndTheEnvelopeReportsWhatWasRecorded()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0010", "S-0010");

        // Recording an authorisation is only permitted once the section is genuinely at the bound
        // (§8a block C remediation) — two request-changes verdicts first.
        AssertSuccess(RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c1", "--range-to", "c2", "--role", "supervisor", "--change", ChangeName],
            new StringWriter(), repo.Path));
        AssertSuccess(RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c2", "--range-to", "c3", "--role", "supervisor", "--change", ChangeName],
            new StringWriter(), repo.Path));

        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "authorise", path, "--reason", "Pushing a third round.", "--role", "product-owner", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("Pushing a third round.", result.GetProperty("reason").GetString());
        Assert.Equal("product-owner", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.SectionFields.Authorisations);
        Assert.Equal("Pushing a third round.", only.Reason);
    }

    // work-lifecycle: "The authorisation SHALL be part of the record, not a permission granted out
    // of band" — CardStore.IsAuthorisingRole's own refusal, reached through the CLI, routed through
    // the shared 'role-not-permitted' code rather than a bespoke one.
    [Fact]
    public void SectionAuthorise_ByANonProductOwnerRole_RefusesWithRoleNotPermittedCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0011", "S-0011");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "authorise", path, "--reason", "Attempted self-authorisation.", "--role", "supervisor", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("role-not-permitted", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.SectionFields.Authorisations);
    }

    [Fact]
    public void SectionAuthorise_EmptyReason_RefusesWithReasonRequiredCode_AndWritesNothing()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0012", "S-0012");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "authorise", path, "--reason", "   ", "--role", "product-owner", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("reason-required", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // work-lifecycle scenario "Authorisation ahead of need is refused" (§8a block C remediation),
    // at the CLI boundary: a brand-new section carries no request-changes verdicts at all, so it is
    // nowhere near the bound.
    [Fact]
    public void SectionAuthorise_OnASectionNotAtTheBound_RefusesWithAuthorisationNotAtBoundCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0014", "S-0014");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "authorise", path, "--reason", "Anticipating trouble.", "--role", "product-owner", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("authorisation-not-at-bound", refusal.GetProperty("code").GetString());

        // process-enforcement (§9 block A2): this refusal is card-addressed — recorded against the
        // section card under the acting role and the time, with the same rule/remedy the envelope
        // carries. No other field on the card changes (no authorisation is appended).
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
        Assert.Empty(read.SectionFields.Authorisations);
    }

    // Construction site for "wrong-card-kind": section authorise.
    [Fact]
    public void SectionAuthorise_TargetIsNotASectionCard_RefusesWithNotASectionCardCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0004", "B-0004");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "authorise", path, "--reason", "Reason.", "--role", "product-owner", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // 8a.13 at the CLI boundary: two request-changes verdicts land, the third is refused with the
    // rule/count/satisfying-command stated, and an authorisation recorded through the CLI's own
    // 'section authorise' then lets the third through.
    [Fact]
    public void SectionVerdict_ThirdRequestChangesThroughTheCli_RefusesUntilAnAuthorisationIsRecordedThroughTheCli()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0013", "S-0013");

        AssertSuccess(RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c1", "--range-to", "c2", "--role", "supervisor", "--change", ChangeName],
            new StringWriter(), repo.Path));
        AssertSuccess(RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c2", "--range-to", "c3", "--role", "supervisor", "--change", ChangeName],
            new StringWriter(), repo.Path));

        var refusedOutput = new StringWriter();
        var refusedExitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c3", "--range-to", "c4", "--role", "supervisor", "--change", ChangeName],
            refusedOutput, repo.Path);
        Assert.Equal(CommandDispatcher.RefusalExitCode, refusedExitCode);
        using (var doc = JsonDocument.Parse(refusedOutput.ToString()))
        {
            var refusal = doc.RootElement.GetProperty("refusal");
            Assert.Equal("remediation-bound-exceeded", refusal.GetProperty("code").GetString());
            Assert.Contains("section authorise", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        AssertSuccess(RunInRepo(
            ["section", "authorise", path, "--reason", "Pushing further.", "--role", "product-owner", "--change", ChangeName],
            new StringWriter(), repo.Path));

        var proceedsExitCode = RunInRepo(
            ["section", "verdict", path, "--verdict", "request-changes", "--range-from", "c3", "--range-to", "c4", "--role", "supervisor", "--change", ChangeName],
            new StringWriter(), repo.Path);
        Assert.Equal(CommandDispatcher.SuccessExitCode, proceedsExitCode);
    }

    [Fact]
    public void SectionClose_Closing_Succeeds_AndTheEnvelopeReportsWhoAndWhen()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0004", "S-0004");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "close", path, "--role", "architect", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("architect", result.GetProperty("closedBy").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("closed", read.Frontmatter.Status);
    }

    // Reverting CloseSectionUnderExistingLock's already-closed check is what this test would go
    // red against — the second close would otherwise silently re-record a new acting role/time.
    [Fact]
    public void SectionClose_AlreadyClosed_RefusesWithAlreadyClosedCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0005", "S-0005");
        AssertSuccess(RunInRepo(["section", "close", path, "--role", "architect", "--change", ChangeName], new StringWriter(), repo.Path));

        var output = new StringWriter();
        var exitCode = RunInRepo(["section", "close", path, "--role", "supervisor", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("already-closed", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Construction site 2 of 3 for "wrong-card-kind": section close.
    [Fact]
    public void SectionClose_TargetIsNotASectionCard_RefusesWithNotASectionCardCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "close", path, "--role", "architect", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void SectionClose_WhenNoCardExistsAtThatPath_RefusesWithCardNotFoundCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.Path, "missing.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "close", path, "--role", "architect", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §8a block A end to end through the CLI: an approved block in the section lands when the
    // section closes, and the envelope reports its id.
    [Fact]
    public void SectionClose_LandsApprovedBlocksInTheSection_AndReportsTheirIds()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0008", "S-0008");
        WriteApprovedBlockCard(repo.Path, "b-0004", "B-0004", "S-0008", "current-state");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "close", sectionPath, "--role", "architect", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var landedIds = result.GetProperty("landedBlockIds").EnumerateArray().Select(static e => e.GetString()).ToArray();
        Assert.Equal(["B-0004"], landedIds);
    }

    [Fact]
    public void SectionStatus_ReadingAnOpenSection_Succeeds_AndReportsItsOwnFields()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialSectionCard(repo.Path, "s-0006", "S-0006");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "status", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("open", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("verdictCount").GetInt32());
        Assert.Empty(result.GetProperty("ageingThreads").EnumerateArray());
    }

    // §9 block E, architect ruling on 9.6's ageing-thread prompt: 'section status' is the surface
    // that reads it, not 'section close' — end to end through the CLI envelope.
    [Fact]
    public void SectionStatus_ABlockWithAnAddressedCommentThatSurvivedARoundBoundary_SurfacesItAsAgeing()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0015", "S-0015");
        var blockPath = WriteBlockCardWithAgeingComment(repo.Path, "b-0011", "B-0011", "S-0015", "C-0001");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "status", sectionPath], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        var ageing = Assert.Single(result.GetProperty("ageingThreads").EnumerateArray());
        Assert.Equal("B-0011", ageing.GetProperty("blockId").GetString());
        Assert.Equal(blockPath, ageing.GetProperty("blockFilePath").GetString());
        Assert.Equal("C-0001", ageing.GetProperty("threadId").GetString());
        Assert.Equal("reviewer", ageing.GetProperty("addressedTo").GetString());
    }

    // Construction site 3 of 3 for "wrong-card-kind": section status.
    [Fact]
    public void SectionStatus_TargetIsNotASectionCard_RefusesWithNotASectionCardCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "status", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void SectionStatus_WhenNoCardExistsAtThatPath_RefusesWithCardNotFoundCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.Path, "missing.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "status", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void SectionStatus_MissingFilePath_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["section", "status"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Section_MissingSubcommand_RefusesWithMissingSubcommandCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["section"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    private static string WriteInitialSectionCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteApprovedBlockCard(string repoRoot, string fileStem, string id, string sectionId, string reviewedState)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: reviewedState, Tasks: ["5.1"], Round: null, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // §9 block E — a block card carrying one addressed comment posted before a round-incrementing
    // transition, still unresolved: CardCommentRouting.AgeingAddressedThreadIds' own "aged" shape.
    private static string WriteBlockCardWithAgeingComment(string repoRoot, string fileStem, string id, string sectionId, string commentId)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: 2, BlockedBy: [], GateResults: []);
        var comment = new CardComment(commentId, CardOwner.Worker, FixedNow, "a question", null, To: CardOwner.Reviewer, null, []);
        var changesRequested = new CardBlockTransitionEntry(
            CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, FixedNow.AddHours(1), []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], [], blockFields, [changesRequested]);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void AssertSuccess(int exitCode) =>
        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-section-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
