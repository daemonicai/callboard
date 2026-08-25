using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §8a block B (8a.7–8a.11, work-lifecycle: "Section remediation follows the finding, not the
/// verdict"): <c>section verdict</c>'s routing of a supervisor's per-finding disposition — a
/// first-time finding creates a new remediation <c>block</c> card, an unresolved finding returns
/// the card that already owns it via <c>finding-recurred</c>, one verdict may do both (or many, §8a
/// block B revision — any number of first-time findings, one manifest file per finding, see
/// <see cref="NewFindingCardManifest"/>), and each of the two misuse refusals (creating a second
/// card for an owned finding; targeting <c>finding-recurred</c> at a task-implementing block) gets
/// its own construction site proven by reverting the exact line it guards and watching the
/// assertion fail.
/// </summary>
public sealed class CommandDispatcherSectionVerdictRemediationTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    // 8a.7: a first-time finding — no card owns 'finding-x001' yet — creates a new block card
    // carrying the finding as its brief, ticking no task, in the section named by the target card.
    [Fact]
    public void FindingNew_FirstTimeFinding_CreatesANewRemediationBlockCard_TickingNoTask()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0001", "S-0001");
        var newCardPath = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), "b-new-0001.md");
        var manifest = WriteManifestFile(repo.Path, "finding-x001", newCardPath, "Fix the X defect", "The reviewer nit about X was not addressed.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var newCardIds = doc.RootElement.GetProperty("result").GetProperty("newCardIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Single(newCardIds);

        var newCard = AssertParseSuccess(CardStore.ReadCard(newCardPath));
        Assert.Equal(CardKind.Block, newCard.Frontmatter.Kind);
        Assert.Equal("briefed", newCard.Frontmatter.Status);
        Assert.Equal("S-0001", newCard.Frontmatter.Section);
        Assert.Equal("finding-x001", newCard.BlockFields.FindingKey);
        Assert.Empty(newCard.BlockFields.Tasks);
        Assert.Equal(1, newCard.BlockFields.Round);
        Assert.Equal("The reviewer nit about X was not addressed.", newCard.Body);
    }

    // §8a block B revision: three new findings in one verdict is the ordinary case, not a corner
    // one — the architect's own scenario for rejecting the one-new-finding cap.
    [Fact]
    public void FindingNew_ThreeNewFindingsInOneVerdict_CreatesAllThree_TheOrdinaryCase()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0010", "S-0010");
        var changeDir = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var path1 = Path.Combine(changeDir, "b-new-0010-1.md");
        var path2 = Path.Combine(changeDir, "b-new-0010-2.md");
        var path3 = Path.Combine(changeDir, "b-new-0010-3.md");
        var manifest1 = WriteManifestFile(repo.Path, "finding-x010-1", path1, "First defect", "Body one.");
        var manifest2 = WriteManifestFile(repo.Path, "finding-x010-2", path2, "Second defect", "Body two.");
        var manifest3 = WriteManifestFile(repo.Path, "finding-x010-3", path3, "Third defect", "Body three.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName,
                "--finding-new", manifest1, "--finding-new", manifest2, "--finding-new", manifest3,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var newCardIds = doc.RootElement.GetProperty("result").GetProperty("newCardIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal(3, newCardIds.Count);
        Assert.Equal(3, newCardIds.Distinct().Count());

        Assert.Equal("finding-x010-1", AssertParseSuccess(CardStore.ReadCard(path1)).BlockFields.FindingKey);
        Assert.Equal("finding-x010-2", AssertParseSuccess(CardStore.ReadCard(path2)).BlockFields.FindingKey);
        Assert.Equal("finding-x010-3", AssertParseSuccess(CardStore.ReadCard(path3)).BlockFields.FindingKey);
    }

    // 8a.8: an unresolved finding returns the card that already owns it — same card, round
    // incremented, no second card created.
    [Fact]
    public void FindingRecurred_UnresolvedFinding_ReturnsTheOwningCard_IncrementsRound_NoSecondCard()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0002", "S-0002");
        var owningPath = WriteApprovedRemediationCard(repo.Path, "b-own-0001", "B-OWN-0001", "S-0002", "finding-x002", round: 2);
        var directory = Path.GetDirectoryName(owningPath)!;
        var filesBefore = Directory.GetFiles(directory).Length;
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-recurred", "B-OWN-0001",
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var recurredIds = doc.RootElement.GetProperty("result").GetProperty("recurredCardIds").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal(["B-OWN-0001"], recurredIds);

        var updated = AssertParseSuccess(CardStore.ReadCard(owningPath));
        Assert.Equal("briefed", updated.Frontmatter.Status);
        Assert.Equal(3, updated.BlockFields.Round);
        // The fixture's own round: 2 is only consistent with its history (8a.17) carrying one
        // prior round-incrementing transition; this call appends the finding-recurred edge on top
        // of that, so two, not one, is now the round's own honest count.
        Assert.Equal(2, updated.Transitions.Count);
        var lastTransition = updated.Transitions[^1];
        Assert.Equal("finding-recurred", lastTransition.Name);
        Assert.Equal(BlockFlowState.Approved, lastTransition.From);
        Assert.Equal(BlockFlowState.Briefed, lastTransition.To);

        Assert.Equal(filesBefore, Directory.GetFiles(directory).Length);
    }

    // 8a.10: one verdict both returns a recurrence and creates a new card.
    [Fact]
    public void SectionVerdict_OneVerdict_BothReturnsARecurrenceAndCreatesANewCard()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0003", "S-0003");
        var owningPath = WriteApprovedRemediationCard(repo.Path, "b-own-0002", "B-OWN-0002", "S-0003", "finding-x003", round: 1);
        var newCardPath = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), "b-new-0003.md");
        var manifest = WriteManifestFile(repo.Path, "finding-x004", newCardPath, "Fix the second defect", "A second, unrelated defect.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName,
                "--finding-recurred", "B-OWN-0002", "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        Assert.Equal("briefed", AssertParseSuccess(CardStore.ReadCard(owningPath)).Frontmatter.Status);
        Assert.Equal("briefed", AssertParseSuccess(CardStore.ReadCard(newCardPath)).Frontmatter.Status);

        var section = AssertParseSuccess(CardStore.ReadCard(sectionPath));
        Assert.Single(section.SectionFields.Verdicts);
    }

    // 8a.9: a --finding-new whose key already names an owned finding is refused, not silently
    // routed or duplicated. Mutation target: CardStore.cs's own FindingAlreadyOwned construction
    // site in RecordSectionVerdictUnderExistingLock's new-finding key scan, on-disk case.
    [Fact]
    public void FindingNew_KeyAlreadyOwnedOnDisk_Refuses_CreatesNoSecondCard()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0004", "S-0004");
        var owningPath = WriteApprovedRemediationCard(repo.Path, "b-own-0003", "B-OWN-0003", "S-0004", "finding-x005", round: 1);
        var newCardPath = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), "b-new-0004.md");
        var manifest = WriteManifestFile(repo.Path, "finding-x005", newCardPath, "Duplicate", "Attempting to re-raise an owned finding as new.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-already-owned", refusal.GetProperty("code").GetString());
        Assert.Contains("B-OWN-0003", refusal.GetProperty("message").GetString());

        Assert.False(File.Exists(newCardPath));
        // All-or-nothing: the section itself carries no verdict either, since the whole call refused.
        var sectionRead = AssertParseSuccess(CardStore.ReadCard(sectionPath));
        Assert.Empty(sectionRead.SectionFields.Verdicts);
        // The owning card is untouched — this was a --finding-new attempt, not a --finding-recurred one.
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(owningPath)).Frontmatter.Status);

        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);
        var recorded = Assert.Single(sectionRead.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // 9.2/9.3 block B coverage: a --finding-new target whose file already exists on disk, for a
    // key nothing owns (the File.Exists(newFinding.FilePath) check, distinct from the
    // key-ownership scan FindingAlreadyOwned tests above). Card-addressed against the section.
    [Fact]
    public void FindingNew_TargetFileAlreadyExistsOnDisk_Refuses_AndRecordsAgainstTheSection()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0018", "S-0018");
        var changeDir = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var collidingPath = Path.Combine(changeDir, "b-new-0018.md");
        // A real, parseable card — not garbage text: the key-ownership scan
        // (RecordSectionVerdictUnderExistingLock) reads every '*.md' file in the section's own
        // directory via ReadAllCards before it ever reaches the File.Exists check this test targets,
        // so an unparseable file at this path would hit CardCorrupt first and mask the refusal this
        // test means to exercise. No FindingKey, so it cannot be mistaken for owning 'finding-x018'.
        var collidingFrontmatter = new CardFrontmatter(
            "B-UNRELATED-0018", CardKind.Block, "An unrelated file", "briefed", CardOwner.Architect, CardScope.Change, "S-0018", FixedNow, FixedNow);
        var collidingCard = new CardFile(collidingFrontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(collidingPath, CardFileWriter.Serialize(collidingCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var manifest = WriteManifestFile(repo.Path, "finding-x018", collidingPath, "Never created", "The target file already exists.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-already-exists", refusal.GetProperty("code").GetString());
        Assert.Contains(collidingPath, refusal.GetProperty("message").GetString());

        var sectionRead = AssertParseSuccess(CardStore.ReadCard(sectionPath));
        Assert.Empty(sectionRead.SectionFields.Verdicts);

        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);
        var recorded = Assert.Single(sectionRead.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // 8a.9's in-batch case (§8a block B revision, once a verdict could carry more than one new
    // finding): two manifests in the same call naming the same key refuses the whole call, and
    // names the earlier manifest's own destination — there is no on-disk owner yet.
    [Fact]
    public void FindingNew_TwoManifestsNameTheSameKeyInOneCall_RefusesTheWholeCall_CreatesNeither()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0011", "S-0011");
        var changeDir = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var path1 = Path.Combine(changeDir, "b-new-0011-1.md");
        var path2 = Path.Combine(changeDir, "b-new-0011-2.md");
        var manifest1 = WriteManifestFile(repo.Path, "finding-x011", path1, "First manifest", "Body one.");
        var manifest2 = WriteManifestFile(repo.Path, "finding-x011", path2, "Second manifest, same key", "Body two.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName,
                "--finding-new", manifest1, "--finding-new", manifest2,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-already-owned", refusal.GetProperty("code").GetString());
        Assert.Contains("pending: this verdict", refusal.GetProperty("message").GetString());
        Assert.Contains(path1, refusal.GetProperty("message").GetString());

        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
        Assert.Empty(AssertParseSuccess(CardStore.ReadCard(sectionPath)).SectionFields.Verdicts);
    }

    // 8a.11: 'finding-recurred' refuses when it targets a task-implementing block (Tasks non-empty)
    // rather than a remediation card. Mutation target: the RecurringFindingTargetsTaskImplementingBlock
    // construction site.
    [Fact]
    public void FindingRecurred_TargetsATaskImplementingBlock_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0005", "S-0005");
        var taskBlockPath = WriteApprovedTaskImplementingBlockCard(repo.Path, "b-task-0001", "B-TASK-0001", "S-0005");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-recurred", "B-TASK-0001",
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("recurring-finding-targets-task-implementing-block", refusal.GetProperty("code").GetString());
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(taskBlockPath)).Frontmatter.Status);

        // §9 block B: card-addressed against the already-resolved section card, not the task block.
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);
        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(sectionPath)).Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // finding-recurred against a card that is not currently approved refuses — the state-table
    // check, independent of the task/remediation-card check above.
    [Fact]
    public void FindingRecurred_TargetNotApproved_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0006", "S-0006");
        var briefedPath = WriteBriefedRemediationCard(repo.Path, "b-own-0004", "B-OWN-0004", "S-0006", "finding-x006");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-recurred", "B-OWN-0004",
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("recurring-finding-not-approved", refusal.GetProperty("code").GetString());
        Assert.Equal("briefed", AssertParseSuccess(CardStore.ReadCard(briefedPath)).Frontmatter.Status);

        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);
        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(sectionPath)).Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // All-or-nothing (8a.10): a verdict combining a valid new finding with an invalid recurring
    // target refuses the whole call — the new card is not left behind on disk.
    [Fact]
    public void SectionVerdict_InvalidRecurrenceAlongsideAValidNewFinding_RefusesTheWholeCall_LeavesNoNewCard()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0007", "S-0007");
        var taskBlockPath = WriteApprovedTaskImplementingBlockCard(repo.Path, "b-task-0002", "B-TASK-0002", "S-0007");
        var newCardPath = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar), "b-new-0007.md");
        var manifest = WriteManifestFile(repo.Path, "finding-x007", newCardPath, "Never created", "Would be created, but the call as a whole refuses.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName,
                "--finding-recurred", "B-TASK-0002", "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.False(File.Exists(newCardPath));
        Assert.Empty(AssertParseSuccess(CardStore.ReadCard(sectionPath)).SectionFields.Verdicts);
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(taskBlockPath)).Frontmatter.Status);
    }

    // Reviewer finding, block B nit (fix-before-land): a refused call must not create the
    // containing directory for a --finding-new target that is never written — the doc comment's
    // "the filesystem... exactly as found" claim, taken literally. Mutation target: reverting the
    // fix (creating the directory ahead of validation again) is what this test would go red
    // against; a not-yet-existing nested directory makes a stray create observable, unlike the
    // sibling tests above whose new-card directory already exists for other reasons.
    [Fact]
    public void SectionVerdict_RefusedCall_CreatesNoStrayDirectoryForAFindingNewTargetNeverWritten()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0017", "S-0017");
        var owningPath = WriteApprovedRemediationCard(repo.Path, "b-own-0017", "B-OWN-0017", "S-0017", "finding-x017", round: 1);
        var changeDir = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        var neverCreatedDirectory = Path.Combine(changeDir, "not-yet-existing");
        var newCardPath = Path.Combine(neverCreatedDirectory, "b-new-0017.md");
        Assert.False(Directory.Exists(neverCreatedDirectory));
        // Same key the owning card already owns — a genuine 8a.9 refusal, not a contrived failure.
        var manifest = WriteManifestFile(repo.Path, "finding-x017", newCardPath, "Would collide", "Never written — the call refuses first.");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifest,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("finding-already-owned", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(newCardPath));
        Assert.False(Directory.Exists(neverCreatedDirectory));
        Assert.Equal("approved", AssertParseSuccess(CardStore.ReadCard(owningPath)).Frontmatter.Status);
    }

    // CLI parse-level: a --finding-new manifest that does not exist refuses cleanly, naming the
    // manifest path.
    [Fact]
    public void SectionVerdict_FindingNewManifestFileMissing_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0012", "S-0012");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", Path.Combine(repo.Path, "does-not-exist.md"),
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("finding-new-manifest-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // A manifest missing its closing fence refuses at parse — reverting NewFindingCardManifest's
    // own closing-fence check to always succeed is what this test would go red against.
    [Fact]
    public void SectionVerdict_FindingNewManifestMissingClosingFence_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0013", "S-0013");
        var manifestPath = Path.Combine(repo.Path, "manifest-no-closing-fence.txt");
        File.WriteAllText(manifestPath, "---\nkey: finding-x013\nnew-card-file: /tmp/x.md\ntitle: X\nBody with no closing fence.\n");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifestPath,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-new-manifest-malformed", refusal.GetProperty("code").GetString());
        Assert.Contains("closing", refusal.GetProperty("message").GetString());
    }

    // A manifest missing a required header key refuses at parse.
    [Fact]
    public void SectionVerdict_FindingNewManifestMissingRequiredKey_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0014", "S-0014");
        var manifestPath = Path.Combine(repo.Path, "manifest-missing-key.txt");
        File.WriteAllText(manifestPath, "---\nkey: finding-x014\nnew-card-file: /tmp/x.md\n---\nBody.\n");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifestPath,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-new-manifest-malformed", refusal.GetProperty("code").GetString());
        Assert.Contains("title", refusal.GetProperty("message").GetString());
    }

    // A manifest with a duplicate header key refuses at parse.
    [Fact]
    public void SectionVerdict_FindingNewManifestDuplicateKey_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0015", "S-0015");
        var manifestPath = Path.Combine(repo.Path, "manifest-duplicate-key.txt");
        File.WriteAllText(manifestPath, "---\nkey: finding-x015\nkey: finding-x015-again\nnew-card-file: /tmp/x.md\ntitle: X\n---\nBody.\n");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifestPath,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-new-manifest-malformed", refusal.GetProperty("code").GetString());
        Assert.Contains("more than once", refusal.GetProperty("message").GetString());
    }

    // A manifest with an unrecognised header key refuses at parse — a closed set, not a silent
    // extra field.
    [Fact]
    public void SectionVerdict_FindingNewManifestUnrecognisedKey_Refuses()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0016", "S-0016");
        var manifestPath = Path.Combine(repo.Path, "manifest-unrecognised-key.txt");
        File.WriteAllText(manifestPath, "---\nkey: finding-x016\nnew-card-file: /tmp/x.md\ntitle: X\nauthor: someone\n---\nBody.\n");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-new", manifestPath,
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("finding-new-manifest-malformed", refusal.GetProperty("code").GetString());
        Assert.Contains("unrecognised", refusal.GetProperty("message").GetString());
    }

    // block transition ... finding-recurred is refused at parse (one-door discipline) — the
    // dedicated test lives in BlockLandUnreachableExceptSectionCloseTests; this is the
    // --finding-recurred id-resolution failure instead: an id naming no card at all.
    [Fact]
    public void FindingRecurred_UnknownCardId_RefusesWithCardIdNotFound()
    {
        using var repo = new TempGitRepo();
        var sectionPath = WriteInitialSectionCard(repo.Path, "s-0009", "S-0009");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "section", "verdict", sectionPath, "--verdict", "request-changes", "--range-from", "aaa", "--range-to", "bbb",
                "--role", "supervisor", "--change", ChangeName, "--finding-recurred", "B-NOPE",
            ],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
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

    private static string WriteApprovedRemediationCard(string repoRoot, string fileStem, string id, string sectionId, string findingKey, int round)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: [], Round: round, BlockedBy: [], GateResults: [], FindingKey: findingKey);
        // 8a.17, "Stored round agrees with the transition history" — CardStore now refuses to act
        // on a block card whose stored round disagrees with its own history, so round > 1 needs
        // matching synthetic changes-requested transitions to keep this fixture consistent.
        var transitions = Enumerable.Range(0, round - 1)
            .Select(_ => new CardBlockTransitionEntry(CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, FixedNow, []))
            .ToList();
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, transitions);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteBriefedRemediationCard(string repoRoot, string fileStem, string id, string sectionId, string findingKey)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "briefed", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: 1, BlockedBy: [], GateResults: [], FindingKey: findingKey);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteApprovedTaskImplementingBlockCard(string repoRoot, string fileStem, string id, string sectionId)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, FixedNow, FixedNow);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["8a.1"], Round: null, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteManifestFile(string repoRoot, string key, string newCardFile, string title, string body)
    {
        var path = Path.Combine(repoRoot, "manifest-" + Guid.NewGuid().ToString("N") + ".txt");
        var content = $"---\nkey: {key}\nnew-card-file: {newCardFile}\ntitle: {title}\n---\n{body}";
        File.WriteAllText(path, content);
        return path;
    }

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-section-verdict-remediation-tests-" + Guid.NewGuid().ToString("N"));
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
