using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 5.2/5.3/5.5 at the CLI boundary: <c>block transition</c>, the first CLI verb whose side effect
/// writes a card (O-3's trigger, discharged generically in the funnel this section wired — this
/// class is the verb-level proof the brief owes on top of that: a refusal here specifically leaves
/// no card written and no card modified, not merely relies on the funnel's own claim). The clock
/// is threaded through <see cref="CommandDispatcher.Run"/> rather than read from
/// <see cref="DateTimeOffset.UtcNow"/> inside the domain — every test here supplies a fixed one so
/// the emitted timestamp is asserted exactly, not merely "close to now".
/// </summary>
public sealed class CommandDispatcherBlockTransitionTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BlockTransition_LegalTransition_Succeeds_AndTheEnvelopeReportsWhatHappened()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0001", "B-0001", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        var result = root.GetProperty("result");
        Assert.Equal("brief", result.GetProperty("transition").GetString());
        Assert.Equal("drafting", result.GetProperty("from").GetString());
        Assert.Equal("briefed", result.GetProperty("to").GetString());
        Assert.Equal("architect", result.GetProperty("actingRole").GetString());
        Assert.Equal("commit-abc", result.GetProperty("base").GetString());
        Assert.Equal(1, result.GetProperty("round").GetInt32());
        Assert.Equal(FixedNow, result.GetProperty("timestamp").GetDateTimeOffset());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        var entry = Assert.Single(read.Transitions);
        Assert.Equal(CardOwner.Architect, entry.By);
        Assert.Equal(FixedNow, entry.Timestamp);
    }

    // §9 block D, 9.8: process-enforcement "Work cannot proceed past a stop-and-ask" — a forward
    // transition (claim: briefed -> building) is refused while the card is blocked by an open
    // question owned by the product owner, and the attempt is recorded against the block card.
    [Fact]
    public void BlockTransition_BlockedByOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        WriteOpenProductOwnerQuestion(repo.Path, "Q-0001");
        var path = WriteInitialBlockCardBlockedBy(repo.Path, "b-0016", "B-0016", BlockFlowState.Briefed, "Q-0001");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("blocked-by-open-product-owner-question", refusal.GetProperty("code").GetString());
        Assert.Contains("Q-0001", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Empty(read.Transitions);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // §13.7 (Product Owner task line): a blocked_by id that resolves ambiguously — two files claim
    // it — cannot be confirmed as either an open product-owner question or not, so the transition
    // fails shut rather than silently proceeding as the pre-13.7 build did.
    [Fact]
    public void BlockTransition_BlockingQuestionDuplicateId_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        WriteDuplicateQuestion(repo.Path, "Q-0090");
        var path = WriteInitialBlockCardBlockedBy(repo.Path, "b-0090", "B-0090", BlockFlowState.Briefed, "Q-0090");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("blocking-question-unreadable", refusal.GetProperty("code").GetString());
        Assert.Contains("Q-0090", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Empty(read.Transitions);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Worker, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // §10 remediation, round two, S2: a deferred product-owner question halts advancement exactly
    // as an open one does — deferring does not lift the halt (Product Owner ruling). Same shape as
    // the open-question test above, deferred rather than open.
    [Fact]
    public void BlockTransition_BlockedByDeferredProductOwnerQuestion_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo.Path, "Q-0022", CardOwner.ProductOwner, QuestionStatus.Deferred);
        var path = WriteInitialBlockCardBlockedBy(repo.Path, "b-0022", "B-0022", BlockFlowState.Briefed, "Q-0022");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("blocked-by-open-product-owner-question", refusal.GetProperty("code").GetString());
        Assert.Contains("Q-0022", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        var read2 = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read2.Frontmatter.Status);
        Assert.Empty(read2.Transitions);
        Assert.Single(read2.Refusals);
    }

    // §9 block D: the exemption half of the same guard — changes-requested is a back-edge (returns
    // work to briefed, does not advance past the blocker), so it is NOT refused even while the
    // card is blocked by the same open product-owner question.
    [Fact]
    public void BlockTransition_ChangesRequested_NotRefusedByOpenProductOwnerQuestion()
    {
        using var repo = new TempGitRepo();
        WriteOpenProductOwnerQuestion(repo.Path, "Q-0002");
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "b-0017.md");
        var frontmatter = new CardFrontmatter(
            "B-0017", CardKind.Block, "Title", BlockFlowState.InReview.ToWireString(), CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var blockFields = new BlockCardFields("commit-abc", null, [], 1, ["Q-0002"], []);
        File.WriteAllText(path, CardFileWriter.Serialize(new CardFile(frontmatter, "Body.", [], [], [], blockFields, [])), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "changes-requested", "--role", "reviewer", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Empty(read.Refusals);
    }

    // §9 block D review finding, negative half 1: a question owned by a role other than the
    // product owner does NOT halt the card, even while open — the guard keys on ownership, not
    // on "is a question".
    [Fact]
    public void BlockTransition_BlockedByOpenQuestionOwnedByNonProductOwner_NotRefused()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo.Path, "Q-0018", CardOwner.Architect, QuestionStatus.Open);
        var path = WriteInitialBlockCardBlockedBy(repo.Path, "b-0018", "B-0018", BlockFlowState.Briefed, "Q-0018");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", read.Frontmatter.Status);
        Assert.Empty(read.Refusals);
    }

    // §9 block D review finding, negative half 2: a product-owner-owned question that is no
    // longer open (answered here) does NOT halt the card — the guard keys on status, not merely
    // on ownership.
    [Fact]
    public void BlockTransition_BlockedByAnsweredProductOwnerQuestion_NotRefused()
    {
        using var repo = new TempGitRepo();
        WriteQuestion(repo.Path, "Q-0019", CardOwner.ProductOwner, QuestionStatus.Answered);
        var path = WriteInitialBlockCardBlockedBy(repo.Path, "b-0019", "B-0019", BlockFlowState.Briefed, "Q-0019");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", read.Frontmatter.Status);
        Assert.Empty(read.Refusals);
    }

    // Owed evidence: the first card-writing verb's own proof that a refusal leaves no card written
    // and no card modified — asserted on the card file's bytes, not on the outcome object (§3's
    // rule: green tests do not exercise the machine contract).
    [Fact]
    public void BlockTransition_UndefinedTransition_Refuses_NamesAvailableTransitions_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0002", "B-0002", BlockFlowState.Drafting);
        var output = new StringWriter();

        // "submit-for-review" (not "approve"/"land"/"fix-before-land" — §8 and §8a block A refuse
        // those names outright, their own separate '...-via-transition-refused' codes, tested
        // elsewhere; "amendment-requested" is no longer a named edge at all since §8a block A's
        // revision cut it, so it would land here too, but as an ordinary undefined-transition) is a
        // real transition name that is simply not available from 'drafting'.
        var exitCode = RunInRepo(
            ["block", "transition", path, "submit-for-review", "--role", "reviewer", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("undefined-transition", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString();
        Assert.Contains("brief", message, StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("result", out _));

        // process-enforcement (§9 block A): "Refusals are explained and attributable" — the
        // envelope carries the refusing rule and its remedy, and the same pair is recorded against
        // the card under the acting role and the time.
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    // A card-writing verb's own proof for the funnel's boundary too: a trailing unrecognised token
    // must refuse via the generic funnel (block B) without this specific verb's write ever running.
    [Fact]
    public void BlockTransition_TrailingUnrecognisedToken_RefusesThroughTheFunnel_AndLeavesTheCardByteIdentical()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0003", "B-0003", BlockFlowState.Drafting);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName, "--oops"],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Reviewer finding (first remediation round): card-not-found and card-layout-mismatch are
    // refusal-shaped; a corrupt card is not — it must exit as a tool-failure, never a refusal.
    [Fact]
    public void BlockTransition_CardNotFound_RefusesWithCardNotFoundCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "missing.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_LayoutMismatch_RefusesWithCardLayoutMismatchCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0007", "B-0007", BlockFlowState.Drafting);
        var output = new StringWriter();

        // The card genuinely lives under ChangeName's directory; declaring a different --change
        // makes AnchoredCardPath expect a directory the file does not live in.
        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", "a-different-change"],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-layout-mismatch", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // The disposition this whole remediation round is about: a corrupt card must exit as a
    // tool-failure (exit code 2), never a refusal (exit code 1) — the opposite instruction to the
    // caller (§3's standing rule). Before this fix, this asserted RefusalExitCode with code
    // "card-write-failed"; it now asserts the tool-failure exit and the generic tool-failure
    // envelope, the same shape index rebuild's own SQLite I/O failures already produce.
    [Fact]
    public void BlockTransition_CorruptCard_ExitsAsRefusal_NotAToolFailure()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("message").GetString()));
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));

        // The card is untouched — a corrupt-card refusal means the tool read and rejected the
        // record, not that a partial write happened.
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Reviewer finding (third remediation round): "missing-argument" has three construction
    // sites (filePath, transitionName, and the --role flag entirely absent) — each needs its own
    // CLI-level proof, not one test standing in for all three.
    [Fact]
    public void BlockTransition_MissingFilePath_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "transition"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_MissingTransitionName_RefusesWithMissingArgumentCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0010", "B-0010", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "transition", path], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Reviewer finding (third remediation round): "missing-flag-value" has three construction
    // sites (--role, --base, --change each dangling with no value) — the original round only
    // exercised none of them; each gets its own test here.
    [Fact]
    public void BlockTransition_RoleFlagWithNoValue_RefusesWithMissingFlagValueCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0011", "B-0011", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "transition", path, "brief", "--role"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-flag-value", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_BaseFlagWithNoValue_RefusesWithMissingFlagValueCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0012", "B-0012", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "transition", path, "brief", "--role", "architect", "--base"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-flag-value", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_ChangeFlagWithNoValue_RefusesWithMissingFlagValueCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0013", "B-0013", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-flag-value", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Reviewer finding (third remediation round): base-immutable and not-a-block-card were proven
    // only at the domain level; a caller only ever experiences the CLI's emitted code.
    [Fact]
    public void BlockTransition_BaseImmutable_RefusesWithBaseImmutableCode()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0014", "B-0014", BlockFlowState.Drafting);

        var briefExitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            TextWriter.Null, repo.Path);
        Assert.Equal(CommandDispatcher.SuccessExitCode, briefExitCode);

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["block", "transition", path, "claim", "--role", "worker", "--base", "a-different-commit", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-immutable", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
    }

    [Fact]
    public void BlockTransition_NotABlockCard_RefusesWithNotABlockCardCode()
    {
        using var repo = new TempGitRepo();
        var directory = Path.Combine(repo.Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        // §5 remediation (finding N3): one code covering both "not a block card" and "not a
        // section card" only says strictly as much as the two it replaces if the message still
        // names which kind was expected and which was actually found.
        var message = refusal.GetProperty("message").GetString();
        Assert.Contains("'question'", message, StringComparison.Ordinal);
        Assert.Contains("'block'", message, StringComparison.Ordinal);
    }

    // Reviewer finding (third remediation round) — the sharpest one: onToolFailure's CLI mapping
    // had zero coverage. Four mutated call sites all stayed green under 263 tests, including a
    // reversion of the exact defect this whole remediation round exists to close. A domain-level
    // test proves CardStore constructs ToolFailure; only a real Run() through the lock-timeout
    // path proves the CLI hands the caller the right instruction. lockTimeout is overridden to
    // 200ms — the same seam clock already provides — so this runs in milliseconds, not 5 real
    // seconds.
    [Fact]
    public void BlockTransition_LockTimeout_ExitsAsToolFailure_NotARefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0015", "B-0015", BlockFlowState.Drafting);
        var before = File.ReadAllBytes(path);
        var holder = CardLock.Acquire(path, TimeSpan.FromSeconds(5)).Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"setup: expected to acquire the lock, timed out: {timedOut.Message}"));

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CommandDispatcher.Run(
                ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
                output, TextReader.Null, error, isInputRedirected: true, workingDirectory: repo.Path,
                clock: static () => FixedNow, lockTimeout: TimeSpan.FromMilliseconds(200));

            Assert.Equal(CommandDispatcher.ToolFailureExitCode, exitCode);
            Assert.NotEqual(CommandDispatcher.RefusalExitCode, exitCode);
            using var doc = JsonDocument.Parse(output.ToString());
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("tool-failure", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(error.ToString()));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            holder.Dispose();
        }
    }

    [Fact]
    public void BlockTransition_BriefWithNoBaseRecordedOrSupplied_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0004", "B-0004", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--change", ChangeName],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-not-recorded", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_MissingRoleFlag_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0005", "B-0005", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(["block", "transition", path, "brief", "--base", "commit-abc", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void BlockTransition_UnrecognisedRoleValue_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = WriteInitialBlockCard(repo.Path, "b-0006", "B-0006", BlockFlowState.Drafting);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "wizard", "--base", "commit-abc", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unrecognised-role", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Block_MissingSubcommand_Refuses()
    {
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(["block"], output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: ".");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Block_UnknownSubcommand_Refuses()
    {
        var output = new StringWriter();

        var exitCode = CommandDispatcher.Run(["block", "frobnicate"], output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: ".");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("unknown-subcommand", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Reviewer finding (fourth remediation round): RunBlockTransition's own repo-root-not-found
    // construction site had zero coverage — it sat beside index rebuild's tested sibling
    // (IndexRebuildTests.IndexRebuild_OutsideAnyGitRepository_Refuses) and survived two
    // enumeration passes before being caught. Sibling test, same shape.
    [Fact]
    public void BlockTransition_OutsideAnyGitRepository_RefusesWithRepoRootNotFoundCode()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "b-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "brief", "--role", "architect", "--base", "commit-abc", "--change", ChangeName],
            output, directory.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("repo-root-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // §5 remediation, round 2 (reviewer finding against the shipped block D binary): a transition
    // on a card carrying gate_results in the pre-B2 two-part shape must still succeed — the
    // reviewer reported "block transition fails identically" to "block gate" on such a card.
    // Broken and watched red first, the same way as the sibling test in
    // CommandDispatcherBlockGateTests: before the parser's legacy branch existed, this raw text
    // reproduced exitCode 2 / "code":"tool-failure".
    [Fact]
    public void BlockTransition_OnACardWithLegacyTwoPartGateResults_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = WriteLegacyBlockCardInReview(repo.Path, "b-0900", "B-0900");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["block", "transition", path, "changes-requested", "--role", "reviewer", "--change", ChangeName], output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        // The legacy result is still on the card, readable — the reviewer's actual failure mode —
        // and, now that changes-requested has moved the block to round 2, it correctly no longer
        // counts as this round's evidence (B2's own rule, applied to a legacy-shaped entry too).
        Assert.Equal([new GateResult("build", 0, 1)], read.BlockFields.GateResults);
        Assert.False(read.BlockFields.GateStatusOf("build").Passed);
    }

    private static string WriteLegacyBlockCardInReview(string repoRoot, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var raw =
            "---\n" +
            $"id: {id}\nkind: block\ntitle: Title\nstatus: in-review\nowner: worker\nscope: change\nsection: 5\n" +
            $"created: {FixedNow:O}\nupdated: {FixedNow:O}\n" +
            "base: commit-abc\n" +
            "round: 1\n" +
            "gate_results: build=0\n" + // exactly the pre-B2 two-part shape the shipped block D binary wrote
            "---\n" +
            "Body.\n";
        File.WriteAllText(path, raw, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteInitialBlockCard(string repoRoot, string fileStem, string id, BlockFlowState status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // §9 block D, 9.8: same shape as WriteInitialBlockCard, but with BlockedBy naming a question
    // card — the CardBlockTransitionOutcome.BlockedByOpenProductOwnerQuestion case's own fixture.
    private static string WriteInitialBlockCardBlockedBy(string repoRoot, string fileStem, string id, BlockFlowState status, string blockingCardId)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "5", FixedNow, FixedNow);
        var blockFields = new BlockCardFields(null, null, [], null, [blockingCardId], []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // §9 block D, 9.8: an open question owned by the product owner — the one shape that halts a
    // card advancing past it.
    private static void WriteOpenProductOwnerQuestion(string repoRoot, string id) =>
        WriteQuestion(repoRoot, id, CardOwner.ProductOwner, QuestionStatus.Open);

    // §9 block D review finding: the asymmetry (ownership, not kind; open, not any status) is
    // implemented in FindBlockingOpenProductOwnerQuestion but was only ever exercised on its
    // positive case. This writes either boundary's negative — a question that must NOT halt.
    private static void WriteQuestion(string repoRoot, string id, CardOwner owner, QuestionStatus status)
    {
        var directory = Path.Combine(repoRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id.ToLowerInvariant() + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "Should we ship X?", status.ToWireString(), owner,
            CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // §13.7: two register-scoped files both claiming id — CardIdentityResolution.Duplicate's own
    // fixture, reused here as FindBlockingOpenProductOwnerQuestion's Undetermined trigger.
    private static void WriteDuplicateQuestion(string repoRoot, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        foreach (var suffix in new[] { "-a", "-b" })
        {
            var path = Path.Combine(directory, id.ToLowerInvariant() + suffix + ".md");
            var frontmatter = new CardFrontmatter(
                id, CardKind.Question, "Should we ship X?", QuestionStatus.Open.ToWireString(), CardOwner.ProductOwner,
                CardScope.Repository, string.Empty, FixedNow, FixedNow);
            var card = new CardFile(frontmatter, "Body.", [], []);
            File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"callboard-block-transition-cli-nongit-{Guid.NewGuid():N}");

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
        private readonly string _path;

        internal string Path => _path;

        internal TempGitRepo()
        {
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-block-transition-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            Directory.CreateDirectory(System.IO.Path.Combine(_path, ".git"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
