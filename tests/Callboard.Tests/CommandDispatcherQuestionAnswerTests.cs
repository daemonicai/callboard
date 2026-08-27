using System.Text;
using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §9 block D: <c>question answer</c> — process-enforcement's "An answer must be written down".
/// Both legitimate routes (a named <c>decision</c> card, or a non-empty inline answer on stdin) are
/// exercised, alongside the refusal that fires when neither is supplied and the two card-addressed
/// refusals (<see cref="CardQuestionAnswerOutcome.NotAQuestionCard"/>, <see cref="
/// CardQuestionAnswerOutcome.NotOpen"/>) this block's brief calls out for the refusal coverage gate.
/// </summary>
public sealed class CommandDispatcherQuestionAnswerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QuestionAnswer_NamedDecision_Succeeds_RecordsTheDecisionReference()
    {
        using var repo = new TempGitRepo();
        var decisionId = WriteDecisionCard(repo, "d-0001", "D-0001");
        var path = WriteOpenQuestion(repo, "q-0001", "Q-0001", CardOwner.ProductOwner);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "answer", path, "--role", "product-owner", "--decision", decisionId],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("answered", result.GetProperty("status").GetString());
        Assert.Equal(decisionId, result.GetProperty("decisionId").GetString());
        Assert.False(result.TryGetProperty("inlineAnswer", out _));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("answered", read.Frontmatter.Status);
        Assert.Equal(decisionId, read.QuestionFields.AnswerDecisionId);
        Assert.Null(read.QuestionFields.AnswerInline);
        Assert.Equal(CardOwner.ProductOwner, read.QuestionFields.AnsweredBy);
        Assert.Equal(FixedNow, read.QuestionFields.AnsweredAt);
    }

    [Fact]
    public void QuestionAnswer_InlineAnswer_Succeeds_RecordsTheInlineText()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0002", "Q-0002", CardOwner.ProductOwner);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "answer", path, "--role", "product-owner"],
            output, repo.Path, "Yes, ship it.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("Yes, ship it.", result.GetProperty("inlineAnswer").GetString());
        Assert.False(result.TryGetProperty("decisionId", out _));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("answered", read.Frontmatter.Status);
        Assert.Equal("Yes, ship it.", read.QuestionFields.AnswerInline);
        Assert.Null(read.QuestionFields.AnswerDecisionId);
    }

    // process-enforcement: "Question closed with no recorded answer" — argv-decidable (neither
    // '--decision' nor a non-empty stdin body was supplied), so this is refused at parse, before
    // any card is ever read — never card-addressed, per the base ruling (§9 architect ruling under
    // '## 9.'), and deliberately not one of RefusalCoverageGateTests.Registry's entries.
    [Fact]
    public void QuestionAnswer_NeitherDecisionNorInlineAnswer_Refuses_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0003", "Q-0003", CardOwner.ProductOwner);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "answer", path, "--role", "product-owner"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("question-answer-missing-answer", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void QuestionAnswer_TargetIsNotAQuestionCard_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var decisionId = WriteDecisionCard(repo, "d-0004", "D-0004");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "answer", Path.Combine(repo.DecisionsDirectory, "d-0004.md"), "--role", "product-owner", "--decision", decisionId],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(Path.Combine(repo.DecisionsDirectory, "d-0004.md")));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    [Fact]
    public void QuestionAnswer_AlreadyAnswered_Refuses_AndRecordsTheRefusal()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0005", "Q-0005", CardOwner.ProductOwner);
        var firstOutput = new StringWriter();
        var firstExitCode = RunInRepo(["question", "answer", path, "--role", "product-owner"], firstOutput, repo.Path, "Already answered.");
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        var output = new StringWriter();
        var exitCode = RunInRepo(["question", "answer", path, "--role", "product-owner"], output, repo.Path, "Trying again.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("question-not-open", refusal.GetProperty("code").GetString());
        var rule = refusal.GetProperty("rule").GetString();
        var remedy = refusal.GetProperty("remedy").GetString();
        Assert.NotNull(rule);
        Assert.NotNull(remedy);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("Already answered.", read.QuestionFields.AnswerInline);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.Equal(rule, recorded.Rule);
        Assert.Equal(remedy, recorded.Remedy);
    }

    [Fact]
    public void QuestionAnswer_DecisionDoesNotResolve_Refuses_WithoutTouchingTheCard()
    {
        using var repo = new TempGitRepo();
        var path = WriteOpenQuestion(repo, "q-0006", "Q-0006", CardOwner.ProductOwner);
        var before = File.ReadAllBytes(path);
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["question", "answer", path, "--role", "product-owner", "--decision", "D-9999"],
            output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // §12 block A round two, item 3: the envelope-category regression. CLI-level proof for the
    // "question" command family — `onCardCorrupt` in `RunQuestionAnswer` must return a refusal
    // (corrupt.Reason verbatim), not throw into the tool-failure envelope. Status "answered-ish" is
    // not a legal question status, so the §12 block A parse door refuses the card at read time.
    [Fact]
    public void QuestionAnswer_CorruptCard_ExitsAsRefusal_WithReasonIntact()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "q-9001.md");
        var frontmatter = new CardFrontmatter(
            "Q-9001", CardKind.Question, "Title", "answered-ish", CardOwner.ProductOwner,
            CardScope.Repository, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CommandDispatcher.Run(
            ["question", "answer", path, "--role", "product-owner"],
            output, new StringReader("Yes."), error, isInputRedirected: true, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        Assert.NotEqual(CommandDispatcher.ToolFailureExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-corrupt", refusal.GetProperty("code").GetString());
        var message = refusal.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("'answered-ish'", message, StringComparison.Ordinal);
        Assert.Contains("'question'", message, StringComparison.Ordinal);
        Assert.Contains(QuestionStatusWireFormat.RecognisedValues, message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(error.ToString()));
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
        return id;
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-question-answer-cli-tests-" + Guid.NewGuid().ToString("N"));
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
