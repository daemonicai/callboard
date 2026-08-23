using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.5 at the CLI boundary: <c>rule promote</c>. Domain-level correctness (bytes preserved, the
/// two-step failure shape, the self-heal) is <see cref="CardRulePromoteTests"/>'s job — this proves
/// the verb is wired end to end: identity-addressed via <c>--id</c>, resolved through
/// <see cref="CardIdentityResolver"/>, and every <see cref="CardRulePromoteOutcome"/> case reaches
/// its own refusal code.
/// </summary>
public sealed class CommandDispatcherRulePromoteTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RulePromote_ChangeScopedRule_Succeeds_AndMovesTheFile()
    {
        using var repo = new TempGitRepo();
        var oldPath = Path.Combine(repo.CardsDirectory, "r-0001.md");
        RunInRepo(
            ["rule", "create", oldPath, "--title", "Never trust a path string", "--role", "architect", "--scope", "change", "--change", ChangeName],
            new StringWriter(), repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = RunInRepo(["rule", "promote", "--id", "R-0001", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("R-0001", result.GetProperty("id").GetString());
        Assert.Equal(oldPath, result.GetProperty("oldFilePath").GetString());
        var newPath = result.GetProperty("newFilePath").GetString()!;
        Assert.Equal(Path.Combine(repo.RegisterDirectory, "r-0001.md"), newPath);
        Assert.Equal("repository", result.GetProperty("scope").GetString());

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void RulePromote_NoSuchId_Refuses_WithCardIdNotFound()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["rule", "promote", "--id", "R-9999", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("card-id-not-found", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RulePromote_AlreadyRepositoryScoped_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "r-0002.md");
        RunInRepo(
            ["rule", "create", path, "--title", "Already there", "--role", "architect", "--scope", "repository"],
            new StringWriter(), repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = RunInRepo(["rule", "promote", "--id", "R-0001", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("already-repository-scoped", refusal.GetProperty("code").GetString());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RulePromote_IdNamesADecisionNotARule_Refuses_WithWrongCardKind()
    {
        using var repo = new TempGitRepo();
        var decisionPath = Path.Combine(repo.DecisionsDirectory, "d-0001.md");
        RunInRepo(
            ["decision", "create", decisionPath, "--title", "Adopt option B", "--role", "product-owner"],
            new StringWriter(), repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = RunInRepo(["rule", "promote", "--id", "D-0001", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("wrong-card-kind", refusal.GetProperty("code").GetString());
    }

    [Fact]
    public void RulePromote_MissingId_Refuses_AtParseTime()
    {
        using var repo = new TempGitRepo();

        var output = new StringWriter();
        var exitCode = RunInRepo(["rule", "promote", "--role", "architect"], output, repo.Path, string.Empty);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("missing-argument", refusal.GetProperty("code").GetString());
    }

    private static int RunInRepo(string[] args, TextWriter output, string workingDirectory, string body) =>
        CommandDispatcher.Run(
            args, output, new StringReader(body), TextWriter.Null, isInputRedirected: true, workingDirectory: workingDirectory, clock: static () => FixedNow);

    private sealed class TempGitRepo : IDisposable
    {
        internal string Path { get; }

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        internal string DecisionsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-rule-promote-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
            CardsDirectory = System.IO.Path.Combine(Path, CardLayout.ChangesDirectory(ChangeName).Replace('/', System.IO.Path.DirectorySeparatorChar));
            RegisterDirectory = System.IO.Path.Combine(Path, CardLayout.RegisterDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            DecisionsDirectory = System.IO.Path.Combine(Path, CardLayout.DecisionsDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
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
