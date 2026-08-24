using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// §7 remediation, blocker 1: <c>question create</c> — creation only. Card-model already models
/// <see cref="CardKind.Question"/> in full (scope rules, file writer, parser, wire format, identity
/// prefix), but no CLI verb had ever constructed one before this. §9 owns everything past creation —
/// answering, deferring, and every refusal tied to a question's own lifecycle (9.7, 9.9, 9.10) — so
/// this covers only that a question card can be created, repository-scoped, and refuses the same
/// wrong-scope/missing-argument shapes every block A creation verb already refuses.
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
        var path = Path.Combine(repo.RegisterDirectory, "q-0001.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", path, "--title", "Should these rules become one family?", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("question", result.GetProperty("kind").GetString());
        Assert.Equal("repository", result.GetProperty("scope").GetString());

        // The response's actingRole still reports the raiser — the fact its name says — even
        // though the card itself is owned by someone else entirely.
        Assert.Equal("worker", result.GetProperty("actingRole").GetString());
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
        var path = Path.Combine(repo.RegisterDirectory, "q-0002.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", path, "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void QuestionCreate_MissingOwedBy_Refuses_WithoutWritingAnything()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "q-0004.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", path, "--title", "Should these rules become one family?", "--role", "worker"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void QuestionCreate_UnrecognisedOwedByRole_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "q-0005.md");

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", path, "--title", "Should these rules become one family?", "--role", "worker", "--owed-by", "nobody"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unrecognised-role", refusal.GetProperty("code").GetString());
        Assert.False(File.Exists(path));
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
        var exitCode = RunInRepo(["question", "answer"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("unknown-subcommand", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void QuestionCreate_AlreadyExistingPath_Refuses_WithCardAlreadyExists()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "q-0003.md");
        var firstOutput = new StringWriter();
        Assert.Equal(
            CommandDispatcher.SuccessExitCode,
            RunInRepo(["question", "create", path, "--title", "First", "--role", "worker", "--owed-by", "product-owner"], firstOutput, repo.Path, "Body."));

        var output = new StringWriter();
        var exitCode = RunInRepo(
            ["question", "create", path, "--title", "Second", "--role", "worker", "--owed-by", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-already-exists", refusal.GetProperty("code").GetString());
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
