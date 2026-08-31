using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §7 remediation, blocker 1: <c>question create</c> — creation only. Card-model already models
/// <see cref="CardKind.Question"/> in full (scope rules, file writer, parser, wire format, identity
/// prefix), but no CLI verb had ever constructed one before this. §9 block D added <c>answer</c>/
/// <c>defer</c> (see <c>CommandDispatcherQuestionAnswerTests</c>/<c>CommandDispatcherQuestionDeferTests</c>)
/// and 9.9/9.10 remain later blocks' own — so this covers only that a question card can be created,
/// repository-scoped, and refuses the same wrong-scope/missing-argument shapes every block A
/// creation verb already refuses.
///
/// <para>
/// 14.5: <c>question create</c> no longer takes a positional card file path — the file is named for
/// the identity the system mints, and every test below learns the path from the response's own
/// <c>filePath</c> field.
/// </para>
/// </summary>
public sealed class CommandDispatcherQuestionCreateTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);

    // §7 second remediation: owner is the role that owes the answer (--owed-by), never the
    // acting role — the defect this test used to pin the other way.
    [Fact]
    public void QuestionCreate_Succeeds_RepositoryScoped_OwnedByTheOwedByRole_NotTheActingRole()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "Should these rules become one family?", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("question", result.GetProperty("kind").GetString());
        Assert.Equal("repository", result.GetProperty("scope").GetString());

        // The response's actingRole still reports the raiser — the fact its name says — even
        // though the card itself is owned by someone else entirely.
        Assert.Equal("worker", result.GetProperty("actingRole").GetString());

        // §9 block D, carried item G: the response names who owes the answer — previously omitted
        // entirely, since a question carries no RegisterCardFields for MapCardCreateOutcome's own
        // OwedBy read to fall back to.
        Assert.Equal("product-owner", result.GetProperty("owedBy").GetString());
        var path = result.GetProperty("filePath").GetString()!;
        Assert.Equal(Path.Combine(repo.RegisterDirectory, "Q-0001.md"), path);
        Assert.True(File.Exists(path));

        var card = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardKind.Question, card.Frontmatter.Kind);
        Assert.Equal(CardScope.Repository, card.Frontmatter.Scope);
        Assert.Equal(CardOwner.ProductOwner, card.Frontmatter.Owner);
        Assert.NotEqual(CardOwner.Worker, card.Frontmatter.Owner);
        Assert.Equal("Body.", card.Body);
    }

    [Fact]
    public void QuestionCreate_MissingTitle_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        AssertNoQuestionCardWasWritten(repo);
    }

    [Fact]
    public void QuestionCreate_MissingOwedBy_Refuses_WithoutWritingAnything()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "Should these rules become one family?", "--role", "worker"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        AssertNoQuestionCardWasWritten(repo);
    }

    [Fact]
    public void QuestionCreate_UnrecognisedOwedByRole_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "Should these rules become one family?", "--role", "worker", "--owed-by", "nobody"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-role", refusal.GetProperty("code").GetString());
        AssertNoQuestionCardWasWritten(repo);
    }

    // §9 block E ruling: --section is optional, and records CardFrontmatter.Section — the fact
    // 9.5's "section close settles its questions" gate reads to find a question raised in it.
    [Fact]
    public void QuestionCreate_WithSection_Succeeds_AndRecordsTheSectionOnTheCard()
    {
        using var repo = new TempGitRepo();
        var sectionId = WriteSectionCard(repo.Path, "establish-callboard", "s-0001", "S-0001");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "A section-raised question", "--role", "worker", "--owed-by", "product-owner", "--section", sectionId],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(sectionId, result.GetProperty("section").GetString());
        var path = result.GetProperty("filePath").GetString()!;

        var card = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(sectionId, card.Frontmatter.Section);
    }

    // A question raised outside any section is legitimate (register: "Question outlives its
    // change") — omitting --section is never refused.
    [Fact]
    public void QuestionCreate_WithoutSection_Succeeds_WithAnEmptySection()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "A repository-wide question", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(string.Empty, result.GetProperty("section").GetString());
        var path = result.GetProperty("filePath").GetString()!;

        var card = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(string.Empty, card.Frontmatter.Section);
    }

    // Unlike an omitted --section, a supplied one that names nothing real is refused — the same
    // "argv names it, execute resolves it against the record" split obligation create's own
    // --section already follows.
    [Fact]
    public void QuestionCreate_WithSectionNamingNoRealCard_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "A question", "--role", "worker", "--owed-by", "product-owner", "--section", "S-9999"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        AssertNoQuestionCardWasWritten(repo);
    }

    [Fact]
    public void QuestionCreate_NoSubcommand_Refuses_WithMissingSubcommand()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["question"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-subcommand", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void QuestionCreate_UnknownSubcommand_Refuses()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        // §9 block D added 'answer'/'defer' as recognised subcommands — 'frobnicate' stays
        // genuinely unrecognised, unlike this test's own former probe ('answer').
        var exitCode = RunInRepo(["question", "frobnicate"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unknown-subcommand", refusal.GetProperty("code").GetString());
    }

    // 14.5: the caller can no longer collide with its own prior creation by naming the same path
    // twice — the tool always names the next, unclaimed identity. `card-already-exists` is still
    // reachable, but only the way `CardCreateTests.CreateCard_TargetAlreadyExists_Refuses` proves it
    // for `decision create`: a hand-authored file sitting, unindexed, at the exact name the
    // allocator's next identity resolves to. Retired the old caller-collision shape rather than
    // preserving it unprovoked (§9 ruling 2: the coverage gate is the standard).
    [Fact]
    public void QuestionCreate_TargetAlreadyExists_Refuses_WithCardAlreadyExists()
    {
        using var repo = new TempGitRepo();
        var conflictingPath = Path.Combine(repo.RegisterDirectory, "Q-0001.md");
        var handAuthored = new CardFile(
            new CardFrontmatter("Q-9999", CardKind.Question, "Hand-authored, wrong name", "open", CardOwner.ProductOwner, CardScope.Repository, string.Empty, FixedNow, FixedNow),
            "Body.", [], []);
        File.WriteAllText(conflictingPath, CardFileWriter.Serialize(handAuthored));

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", "--title", "First", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-already-exists", refusal.GetProperty("code").GetString());
    }

    // 14.5: a refused 'question create' can no longer be checked against one caller-named path —
    // the caller never named one. No file bearing the question kind prefix exists at all is the
    // corresponding "wrote nothing" proof.
    private static void AssertNoQuestionCardWasWritten(TempGitRepo repo)
    {
        if (!Directory.Exists(repo.RegisterDirectory))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFiles(repo.RegisterDirectory, "Q-*.md", SearchOption.TopDirectoryOnly));
    }

    private static string WriteSectionCard(string repoRoot, string changeName, string fileStem, string id)
    {
        var directory = Path.Combine(repoRoot, CardLayout.ChangesDirectory(changeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, FixedNow, FixedNow);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-question-create-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
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
