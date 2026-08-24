using System.Text.Json;
using Callboard.Cards;
using Callboard.Cli;

namespace Callboard.Tests;

/// <summary>
/// 7.1/7.11 at the CLI boundary: <c>rule|hazard|obligation|decision create</c>, <c>section create</c>
/// and <c>rule|hazard|obligation|decision discharge</c>. Same "own refusal code, own test"
/// discipline earlier sections established — the load-bearing test here is
/// <see cref="HazardCreate_MissingCondition_Refuses_AndStatesTheConditionRequired"/>: register's
/// "the system refuses and states the condition it requires" scenario, checked against the actual
/// message text.
/// </summary>
public sealed class CommandDispatcherRegisterTests
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuleCreate_ChangeScoped_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "r-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", "Never trust a path string", "--role", "architect", "--scope", "change", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("R-0001", result.GetProperty("id").GetString());
        Assert.Equal("rule", result.GetProperty("kind").GetString());
        Assert.Equal("change", result.GetProperty("scope").GetString());
        Assert.Equal("open", result.GetProperty("status").GetString());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RuleCreate_SectionScoped_Refuses_WithTheSpecsExactWording()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "r-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["rule", "create", path, "--title", "Bad", "--role", "architect", "--scope", "section", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("scope-refused", refusal.GetProperty("code").GetString());
        Assert.Contains("a rule applying to one section is a constraint in a brief", refusal.GetProperty("message").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void HazardCreate_WithConditionAndCadence_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "hazard", "create", path, "--title", "Rotating key", "--role", "worker",
                "--condition", "The staging key changes every 90 days", "--cadence", "monthly",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("hazard", result.GetProperty("kind").GetString());
        Assert.Equal("repository", result.GetProperty("scope").GetString());
        Assert.Equal("The staging key changes every 90 days", result.GetProperty("condition").GetString());
        Assert.Equal("monthly", result.GetProperty("cadence").GetString());
    }

    // The load-bearing refusal (register: "the system refuses and states the condition it
    // requires"). What would have to break for this to go red: ParseHazardCreate accepting an
    // absent or blank --condition instead of refusing.
    [Fact]
    public void HazardCreate_MissingCondition_Refuses_AndStatesTheConditionRequired()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["hazard", "create", path, "--title", "Rotating key", "--role", "worker", "--cadence", "monthly"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("hazard-missing-condition", refusal.GetProperty("code").GetString());
        Assert.Contains("condition", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    // Reviewer finding, block A review round 1: a missing --cadence must mint its own code
    // (hazard-missing-cadence), distinct from a missing --condition's hazard-missing-condition —
    // one code silently covering two independently-triggerable conditions is exactly what a
    // refusal code exists to make unnecessary to disambiguate from prose.
    [Fact]
    public void HazardCreate_MissingCadence_Refuses_WithItsOwnDistinctCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["hazard", "create", path, "--title", "Rotating key", "--role", "worker", "--condition", "The key rotates"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("hazard-missing-cadence", refusal.GetProperty("code").GetString());
        Assert.Contains("cadence", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ObligationCreate_Succeeds()
    {
        using var repo = new TempGitRepo();
        var sectionPath = Path.Combine(repo.CardsDirectory, "s-0001.md");
        var sectionOutput = new StringWriter();
        RunInRepo(
            ["section", "create", sectionPath, "--title", "7. Register", "--role", "architect", "--change", ChangeName],
            sectionOutput, repo.Path, "Body.");
        using var sectionDoc = JsonDocument.Parse(sectionOutput.ToString());
        var sectionId = sectionDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var path = Path.Combine(repo.CardsDirectory, "o-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["obligation", "create", path, "--title", "Settle the migration", "--role", "architect", "--change", ChangeName, "--owed-by", sectionId!],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("obligation", result.GetProperty("kind").GetString());
        Assert.Equal(sectionId, result.GetProperty("owedBy").GetString());
    }

    [Fact]
    public void ObligationCreate_MissingOwedBy_Refuses_WithItsOwnDistinctCode()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "o-0002.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["obligation", "create", path, "--title", "Settle the migration", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var refusal = doc.RootElement.GetProperty("refusal");
        Assert.Equal("obligation-missing-owed-by", refusal.GetProperty("code").GetString());
        Assert.Contains("owed-by", refusal.GetProperty("message").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ObligationCreate_OwedByDoesNotResolve_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "o-0003.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "obligation", "create", path, "--title", "Settle the migration", "--role", "architect",
                "--change", ChangeName, "--owed-by", "S-9999",
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ObligationCreate_OwedByNamesANonSectionCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var decisionPath = Path.Combine(repo.DecisionsDirectory, "d-0009.md");
        var decisionOutput = new StringWriter();
        RunInRepo(
            ["decision", "create", decisionPath, "--title", "Adopt option A", "--role", "product-owner"],
            decisionOutput, repo.Path, "Body.");
        using var decisionDoc = JsonDocument.Parse(decisionOutput.ToString());
        var decisionId = decisionDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var path = Path.Combine(repo.CardsDirectory, "o-0004.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            [
                "obligation", "create", path, "--title", "Settle the migration", "--role", "architect",
                "--change", ChangeName, "--owed-by", decisionId!,
            ],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DecisionCreate_Succeeds_AndDoesNotAcceptAChangeFlag()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.DecisionsDirectory, "d-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["decision", "create", path, "--title", "Adopt option A", "--role", "product-owner"],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("decision", doc.RootElement.GetProperty("result").GetProperty("kind").GetString());
        Assert.Equal("capability", doc.RootElement.GetProperty("result").GetProperty("scope").GetString());
    }

    [Fact]
    public void SectionCreate_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "s-0001.md");
        var output = new StringWriter();

        var exitCode = RunInRepo(
            ["section", "create", path, "--title", "9. Review", "--role", "architect", "--change", ChangeName],
            output, repo.Path, "Body.");

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("section", result.GetProperty("kind").GetString());
        Assert.Equal("open", result.GetProperty("status").GetString());
    }

    [Fact]
    public void RuleDischarge_OnAnOpenRule_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "r-0003.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["rule", "create", path, "--title", "A repository rule", "--role", "architect", "--scope", "repository"],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "discharge", path, "--role", "architect"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("architect", result.GetProperty("actingRole").GetString());

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("discharged", read.Frontmatter.Status);
    }

    [Fact]
    public void HazardDischarge_WhoseConditionHasLapsed_Succeeds()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.RegisterDirectory, "h-0004.md");
        var createOutput = new StringWriter();
        RunInRepo(
            [
                "hazard", "create", path, "--title", "Rotating key", "--role", "worker",
                "--condition", "The staging key never rotates", "--cadence", "weekly",
            ],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["hazard", "discharge", path, "--role", "worker"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("discharged", read.Frontmatter.Status);
        Assert.Equal(CardOwner.Worker, read.RegisterFields.DischargedBy);
    }

    [Fact]
    public void Discharge_AlreadyDischarged_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.DecisionsDirectory, "d-0002.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["decision", "create", path, "--title", "Adopt option A", "--role", "product-owner"],
            createOutput, repo.Path, "Body.");

        var firstDischarge = new StringWriter();
        var firstExitCode = CommandDispatcher.Run(
            ["decision", "discharge", path, "--role", "product-owner"],
            firstDischarge, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);
        Assert.Equal(CommandDispatcher.SuccessExitCode, firstExitCode);

        var second = new StringWriter();
        var secondExitCode = CommandDispatcher.Run(
            ["decision", "discharge", path, "--role", "product-owner"],
            second, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, secondExitCode);
        using var doc = JsonDocument.Parse(second.ToString());
        Assert.Equal("already-discharged", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void Discharge_TargetIsNotARegisterCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var path = Path.Combine(repo.CardsDirectory, "s-0002.md");
        var createOutput = new StringWriter();
        RunInRepo(
            ["section", "create", path, "--title", "10. Section", "--role", "architect", "--change", ChangeName],
            createOutput, repo.Path, "Body.");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["rule", "discharge", path, "--role", "architect", "--change", ChangeName],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("not-a-register-card", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_TwoOpenDecisions_Succeeds()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0003", "Adopt option B");
        var supersededId = CreateDecision(repo, "d-0004", "Adopt option A");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", supersededId, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.SuccessExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(supersedingId, result.GetProperty("supersedingId").GetString());
        Assert.Equal(supersededId, result.GetProperty("supersededId").GetString());
        Assert.Equal("product-owner", result.GetProperty("actingRole").GetString());
    }

    [Fact]
    public void DecisionSupersede_SameIdOnBothSides_Refuses_WithoutHanging()
    {
        using var repo = new TempGitRepo();
        var id = CreateDecision(repo, "d-0005", "Adopt option A");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", id, "--supersedes", id, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("self-supersession", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersededAlreadyDischarged_Refuses()
    {
        using var repo = new TempGitRepo();
        var first = CreateDecision(repo, "d-0006", "Adopt option A");
        var second = CreateDecision(repo, "d-0007", "Adopt option B");
        var third = CreateDecision(repo, "d-0008", "Adopt option C");

        var firstOutput = new StringWriter();
        CommandDispatcher.Run(
            ["decision", "supersede", second, "--supersedes", first, "--role", "product-owner"],
            firstOutput, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", third, "--supersedes", first, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("already-discharged", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersedesIdDoesNotResolve_Refuses()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0009", "Adopt option B");

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", "D-9999", "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("card-id-not-found", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    [Fact]
    public void DecisionSupersede_SupersedesNamesANonDecisionCard_Refuses()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0010", "Adopt option B");

        var rulePath = Path.Combine(repo.RegisterDirectory, "r-0010.md");
        var ruleOutput = new StringWriter();
        RunInRepo(
            ["rule", "create", rulePath, "--title", "A repository rule", "--role", "architect", "--scope", "repository"],
            ruleOutput, repo.Path, "Body.");
        using var ruleDoc = JsonDocument.Parse(ruleOutput.ToString());
        var ruleId = ruleDoc.RootElement.GetProperty("result").GetProperty("id").GetString();

        var output = new StringWriter();
        var exitCode = CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", ruleId!, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        Assert.Equal(CommandDispatcher.RefusalExitCode, exitCode);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal("wrong-card-kind", doc.RootElement.GetProperty("refusal").GetProperty("code").GetString());
    }

    // Retrieval by id after supersession, proven through the CLI boundary rather than the domain
    // layer alone: 'decision status' does not exist (11.1 is out of scope for this block), so this
    // reads back the file CardStore.ReadCard sees, the same evidence the resolver itself uses.
    [Fact]
    public void DecisionSupersede_SupersededDecision_StillReadableFromDiskAfterwards()
    {
        using var repo = new TempGitRepo();
        var supersedingId = CreateDecision(repo, "d-0011", "Adopt option B");
        var supersededId = CreateDecision(repo, "d-0012", "Adopt option A");
        var supersededPath = Path.Combine(repo.DecisionsDirectory, "d-0012.md");

        var output = new StringWriter();
        CommandDispatcher.Run(
            ["decision", "supersede", supersedingId, "--supersedes", supersededId, "--role", "product-owner"],
            output, TextReader.Null, TextWriter.Null, isInputRedirected: false, workingDirectory: repo.Path, clock: static () => FixedNow);

        var read = AssertParseSuccess(CardStore.ReadCard(supersededPath));
        Assert.Equal(supersededId, read.Frontmatter.Id);
        Assert.Equal("discharged", read.Frontmatter.Status);
        Assert.Equal(supersedingId, read.RegisterFields.SupersededBy);
    }

    private static string CreateDecision(TempGitRepo repo, string fileStem, string title)
    {
        var path = Path.Combine(repo.DecisionsDirectory, fileStem + ".md");
        var output = new StringWriter();
        RunInRepo(["decision", "create", path, "--title", title, "--role", "product-owner"], output, repo.Path, "Body.");
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()!;
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

        internal string CardsDirectory { get; }

        internal string RegisterDirectory { get; }

        internal string DecisionsDirectory { get; }

        internal TempGitRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "callboard-register-cli-tests-" + Guid.NewGuid().ToString("N"));
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
