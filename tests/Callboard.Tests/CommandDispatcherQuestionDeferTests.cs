using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §9 block D — the question status vocabulary entire, including <c>deferred</c>: block E's own
/// <c>9.5</c> ("Refuse section close over open undeferred questions") has no meaning until a
/// question can actually be deferred, so this block builds <c>question defer</c> and its two
/// card-addressed refusals (<see cref="CardQuestionDeferOutcome.NotAQuestionCard"/>, <see cref="
/// CardQuestionDeferOutcome.NotOpen"/>) even though no <c>9.M</c> task number names this verb.
/// </summary>
public sealed class CommandDispatcherQuestionDeferTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuestionDefer_Succeeds_RecordsTheTargetAndStaysOpenForItsOwedAnswer()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0001", "Q-0001", CardOwner.ProductOwner);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "defer", path, "--role", "product-owner", "--target", "section 12 of a-later-change"],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("deferred", result.GetProperty("status").GetString());
        Assert.Equal("section 12 of a-later-change", result.GetProperty("deferredTarget").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("deferred", read.Frontmatter.Status);
        Assert.Equal("section 12 of a-later-change", read.QuestionFields.DeferredTarget);
        Assert.Equal(CardOwner.ProductOwner, read.QuestionFields.DeferredBy);
        Assert.Equal(FixedNow, read.QuestionFields.DeferredAt);

        // register: "the question remains open and continues to surface to the role that owes its
        // answer" — deferring redirects when it is settled, it does not change who owes it.
        Assert.Equal(CardOwner.ProductOwner, read.Frontmatter.Owner);
    }

    // Argv-decidable ('--target' is missing) — refused at parse, never card-addressed, the same
    // disposition ParseObligationCreate's missing-'--section' check already has.
    [Fact]
    public void QuestionDefer_MissingTarget_Refuses_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0002", "Q-0002", CardOwner.ProductOwner);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(["question", "defer", path, "--role", "product-owner"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("missing-argument", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void QuestionDefer_TargetIsNotAQuestionCard_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var decisionPath = WriteDecisionCard(repo, "d-0003", "D-0003");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "defer", decisionPath, "--role", "product-owner", "--target", "section 3"],
            output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(decisionPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    [Fact]
    public void QuestionDefer_AlreadyDeferred_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0004", "Q-0004", CardOwner.ProductOwner);
        var firstOutput = new StringWriter();
        var firstExitCode = RunInRepo(["question", "defer", path, "--role", "product-owner", "--target", "section 1"], firstOutput, repo.Path);
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        var output = new StringWriter();
        var exitCode = RunInRepo(["question", "defer", path, "--role", "product-owner", "--target", "section 2"], output, repo.Path);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("question-not-open", refusal.GetProperty("code").GetString());
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("section 1", read.QuestionFields.DeferredTarget);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    private static string WriteOpenQuestion(TempGitRepo repo, string fileStem, string id, CardOwner owner)
    {
        var path = Path.Combine(repo.RegisterDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "Should we ship X?", QuestionStatus.Open.ToWireString(), owner,
            CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string WriteDecisionCard(TempGitRepo repo, string fileStem, string id)
    {
        var path = Path.Combine(repo.DecisionsDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Decision, "A decision", RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner,
            CardScope.Capability, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory) =>
        CommandDispatcher.Run(
            args, output, TextReader.Null, TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string RegisterDirectory { get; }

        internal string DecisionsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-question-defer-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(RegisterDirectory);
            DecisionsDirectory = System.IO.Path.Combine(Path, CardLayout.DecisionsDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(DecisionsDirectory);
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
